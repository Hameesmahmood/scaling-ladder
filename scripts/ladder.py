#!/usr/bin/env python3
"""Runs one step of the scaling ladder (or all of them) and writes results/step-N.json.

    python3 scripts/ladder.py 0          # one step
    python3 scripts/ladder.py all        # every step, in order
    python3 scripts/ladder.py 1 --quick  # short runs, for CI and smoke tests

For each step: put the database in the step's shape (indexes on or off), start the API with the
step's settings, warm up, measure latency at the fixed light rate, then search for the highest rate
the step sustains. Needs Docker and the seeded database (scripts/db.sh seed).
"""
import argparse
import json
import math
import os
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Each step is the previous one plus one change.
STEPS = {
    0: {"name": "Baseline", "change": "Primary keys only, a new connection per request, no cache, one instance",
        "indexes": False, "pooling": False, "cache": False},
    1: {"name": "Indexes", "change": "Indexes matching each query's WHERE and ORDER BY",
        "indexes": True, "pooling": False, "cache": False},
    2: {"name": "Connection pooling", "change": "Npgsql's pool, 20 connections per instance",
        "indexes": True, "pooling": True, "cache": False},
    3: {"name": "Cache-aside", "change": "Redis in front of GET shipment, 60-second TTL, deleted on every write",
        "indexes": True, "pooling": True, "cache": True},
    4: {"name": "Stampede protection", "change": "Concurrent misses on a key share one read; TTLs jittered by up to 20%",
        "indexes": True, "pooling": True, "cache": True, "coalescing": True, "jitter": 0.2},
    5: {"name": "Load balancing", "change": "Nginx in front of three instances, retrying failed GETs on another",
        "indexes": True, "pooling": True, "cache": True, "coalescing": True, "jitter": 0.2, "instances": 3},
}

# Chosen from step 0's capacity (about half of it), then fixed for every step so they compare.
LIGHT_RATE = 0.5
# A step sustains a rate if p99 stays under this limit, almost nothing fails, and k6 starts every
# request on time. The limit is 250 ms, or twice the step's own p99 at the light rate if that's higher.
# Only the baseline needs the second rule: one of its requests alone takes over 1.5 s, so it could
# never meet 250 ms at any rate, and "capacity 0" would hide that it does keep up at about 1 a second.
# Twice the single-request time still catches the point where requests start queueing.
P99_LIMIT_MS = 250
MAX_FAILED = 0.001
INDEXES = ["shipments_tracking_number", "tracking_events_shipment", "shipments_customer"]


def run(*args, env=None, capture=True):
    result = subprocess.run(args, cwd=ROOT, env=env, capture_output=capture, text=True)
    if result.returncode != 0:
        sys.exit(f"Failed: {' '.join(args)}\n{result.stderr}")
    return result.stdout


def psql(sql):
    return run("docker", "compose", "exec", "-T", "db", "psql", "-U", "tracker", "-d", "tracker", "-tAc", sql).strip()


def set_indexes(on):
    """Creates or drops the step-1 indexes. Returns how long creating them took, and their size."""
    present = int(psql(f"SELECT count(*) FROM pg_indexes WHERE indexname IN ({','.join(repr(i) for i in INDEXES)})"))
    if not on:
        if present:
            psql("DROP INDEX IF EXISTS " + ", ".join(INDEXES))
        return None
    info = {}
    if present < len(INDEXES):
        started = time.monotonic()
        with open(ROOT / "db" / "indexes.sql") as sql:
            subprocess.run(["docker", "compose", "exec", "-T", "db", "psql", "-v", "ON_ERROR_STOP=1",
                            "-U", "tracker", "-d", "tracker", "-q"], cwd=ROOT, stdin=sql, check=True)
        info["buildSeconds"] = round(time.monotonic() - started, 1)
    psql("ANALYZE")
    sizes = psql("SELECT string_agg(indexname || '=' || pg_relation_size(indexname::regclass), ',') "
                 f"FROM pg_indexes WHERE indexname IN ({','.join(repr(i) for i in INDEXES)})")
    info["bytes"] = {k: int(v) for k, v in (pair.split("=") for pair in sizes.split(","))}
    return info


def redis(*args):
    return run("docker", "compose", "exec", "-T", "redis", "redis-cli", *args).strip()


def redis_info():
    lines = redis("INFO", "everything").splitlines()
    return dict(line.split(":", 1) for line in lines if ":" in line and not line.startswith("#"))


def api_stats():
    """Each instance's own counters (GET /stats), summed. The image has no curl, so plain bash."""
    totals = {"shipmentReads": 0, "cacheHits": 0, "cacheMisses": 0, "cacheJoined": 0, "cacheUnavailable": 0}
    request = "exec 3<>/dev/tcp/localhost/8080 && printf 'GET /stats HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && cat <&3"
    for container in run("docker", "compose", "ps", "-q", "api").split():
        # Text mode has already turned the response's \r\n into \n.
        body = json.loads(run("docker", "exec", container, "bash", "-c", request).split("\n\n", 1)[1])
        totals["shipmentReads"] += body["shipment_reads"]
        totals["cacheHits"] += body["cache"]["hits"]
        totals["cacheMisses"] += body["cache"]["misses"]
        totals["cacheJoined"] += body["cache"]["joined"]
        totals["cacheUnavailable"] += body["cache"]["unavailable"]
    return totals


def counters():
    """Running totals: Postgres's sessions (connections) opened and rows read; Redis's lookups
    (keyspace hits and misses; from step 4 a miss looks twice, once more just before reading
    Postgres); and the instances' own counts of cache hits and misses, shipment reads from
    Postgres, misses that joined another's read, and reads that skipped Redis."""
    row = psql("SELECT sessions, tup_returned + tup_fetched FROM pg_stat_database WHERE datname = 'tracker'").split("|")
    info = redis_info()
    return {"sessions": int(row[0]), "rowsRead": int(row[1]),
            "redisHits": int(info["keyspace_hits"]), "redisMisses": int(info["keyspace_misses"]), **api_stats()}


def cpu():
    """Each container's CPU use right now, in cores (docker stats reports percent of one core).
    Instances of the API are listed separately, as api-1, api-2 and so on."""
    out = run("docker", "stats", "--no-stream", "--format", "{{.Name}} {{.CPUPerc}}")
    usage = {}
    for line in out.splitlines():
        name, percent = line.rsplit(" ", 1)
        service = "k6" if "-k6-" in name else name.removeprefix("scaling-ladder-")
        if not service.startswith("api-"):
            service = service.removesuffix("-1")
        usage[service] = round(float(percent.rstrip("%")) / 100, 2)
    return usage


def per_request(before, after, requests):
    """Counters per request over a run, plus the cache's hit rate and Redis's memory at the end."""
    delta = {k: after[k] - before[k] for k in after}
    result = {k: round(v / max(requests, 1), 2) for k, v in delta.items()}
    lookups = delta["cacheHits"] + delta["cacheMisses"]
    result["cacheHitRate"] = round(delta["cacheHits"] / lookups, 3) if lookups else None
    info = redis_info()
    result["redisKeys"] = int(redis("DBSIZE"))
    result["redisMemoryBytes"] = int(info["used_memory"])
    return result


def build_api():
    """Builds the API image once per run, retrying: fetching the .NET base image can time out."""
    for attempt in range(1, 4):
        if subprocess.run(["docker", "compose", "build", "api"], cwd=ROOT, capture_output=True).returncode == 0:
            return
        print(f"  building the API image failed (attempt {attempt}), retrying", flush=True)
        time.sleep(10)
    sys.exit("Couldn't build the API image.")


# Where k6 sends load: straight to the API before step 5, through nginx from step 5.
target = "http://api:8080"


def start_api(step, **overrides):
    """Starts the API with the step's settings (and any overrides, as environment variables for
    compose.yaml), with Redis emptied, so no step inherits another's cache. Stopped first, so every
    step starts from a fresh process."""
    global target
    config = STEPS[step]
    instances = config.get("instances", 1)
    settings = {"TRACKER_POOLING": config["pooling"], "TRACKER_CACHE": config["cache"],
                "TRACKER_COALESCING": config.get("coalescing", False),
                "TRACKER_CACHE_TTL_JITTER": config.get("jitter", 0), **overrides}
    env = {**os.environ, **{k: str(v).lower() for k, v in settings.items()}}
    run("docker", "compose", "stop", "api", "nginx")
    run("docker", "compose", "up", "-d", "--no-build", "--wait", "--scale", f"api={instances}", "api", "nginx", env=env)
    target = "http://nginx:80" if instances > 1 else "http://api:8080"
    redis("FLUSHALL")
    redis("CONFIG", "RESETSTAT")


def k6(rate, seconds, *env, sample_cpu=False):
    """Runs the workload. With sample_cpu, also records each container's CPU halfway through."""
    extra = [arg for e in env for arg in ("-e", e)]
    command = ["docker", "compose", "run", "--rm", "-e", f"RATE={rate}", "-e", f"DURATION={seconds}s",
               "-e", f"TARGET={target}", *extra, "k6", "run", "--quiet", "--no-color", "/load/workload.js"]
    if not sample_cpu:
        return json.loads(run(*command).strip().splitlines()[-1])
    usage = {}
    sampler = threading.Timer(seconds / 2, lambda: usage.update(cpu()))
    sampler.start()
    result = json.loads(run(*command).strip().splitlines()[-1])
    sampler.join()
    return {**result, "cpuCores": usage}


def sustains(result, limit_ms):
    p99 = result["latencyMs"]["p99"]
    return p99 is not None and p99 < limit_ms and result["failedRate"] < MAX_FAILED and result["dropped"] == 0


def probe_seconds(rate, quick):
    # At least 30 s, and long enough for about 300 requests so p99 means something, up to 2 minutes.
    return 5 if quick else min(120, max(30, math.ceil(300 / rate)))


def starting_rate(step):
    """Half the previous step's capacity, rounded down to a power of two, so fast steps don't spend
    ten minutes on easy probes. The baseline starts at 0.25 a second."""
    previous = ROOT / "results" / f"step-{step - 1}.json"
    if step == 0 or not previous.exists():
        return 0.25
    sustained = json.loads(previous.read_text())["capacity"]["sustainedRate"]
    return max(0.25, 2 ** math.floor(math.log2(max(sustained, 0.25) / 2)))


def capacity(quick, limit_ms, start):
    """Doubles the rate from `start` until the step can't sustain it (halving first if even `start`
    is too much), then narrows the gap by bisection."""
    probes = []

    def attempt(rate):
        time.sleep(5)  # let any queue from the last probe drain
        before = counters()
        result = k6(rate, probe_seconds(rate, quick), sample_cpu=True)
        result["perRequest"] = per_request(before, counters(), result["requests"])
        result["sustained"] = sustains(result, limit_ms)
        probes.append(result)
        print(f"    {rate:>8g}/s  p99 {result['latencyMs']['p99']} ms  failed {result['failedRate']:.2%}  "
              f"dropped {result['dropped']}  {'ok' if result['sustained'] else 'too much'}"
              + (f"  cache hits {hit:.0%}" if (hit := result["perRequest"]["cacheHitRate"]) is not None else ""), flush=True)
        return result["sustained"]

    def probe(rate):
        # Near its limit a step passes some runs and fails others (scripts/repeat.py: step 2 passed
        # 2,048 a second 2 times in 3), and one unlucky run used to end the search. So a failure is
        # rerun, and the rate counts as sustained if 2 of 3 runs pass.
        # After a failure, both reruns must pass, so the first failed rerun settles it.
        return attempt(rate) or (attempt(rate) and attempt(rate))

    low, high, rate = None, None, start
    while rate <= (8 if quick else 8192):
        if probe(rate):
            low, rate = rate, rate * 2
        else:
            high = rate
            break
    while low is None and rate > 0.25:
        rate /= 2
        if probe(rate):
            low = rate
        else:
            high = rate
    if low is None:
        return 0, probes
    while high is not None and (high - low) / low > (0.5 if quick else 0.1):
        middle = round((low + high) / 2, 2)
        if probe(middle):
            low = middle
        else:
            high = middle
    return low, probes


def measure(step, quick):
    config = STEPS[step]
    print(f"\nStep {step}: {config['name']}", flush=True)
    indexes = set_indexes(config["indexes"])
    start_api(step)
    print("  warming up", flush=True)
    k6(LIGHT_RATE, 5 if quick else 15)
    before = counters()
    seconds = 10 if quick else 180
    print(f"  latency at {LIGHT_RATE}/s for {seconds} s", flush=True)
    light = k6(LIGHT_RATE, seconds)
    # Right away: per_request also snapshots Redis's keys and memory, which the capacity search changes.
    light["perRequest"] = per_request(before, counters(), light["requests"])
    print(f"    p50 {light['latencyMs']['p50']} ms  p95 {light['latencyMs']['p95']} ms  p99 {light['latencyMs']['p99']} ms", flush=True)
    limit_ms = max(P99_LIMIT_MS, round(2 * light["latencyMs"]["p99"]))
    start = 0.25 if quick else starting_rate(step)
    # A freshly started API fails its first heavy probe whatever the step: .NET hasn't finished
    # compiling the hot paths, and its thread pool grows only gradually. Measured: steps 2 and 3
    # both dropped requests on a first 30 s at 1,024 a second, then passed every run after it. So
    # warm up at the starting rate, discarded, or a step that starts high would be measured cold.
    print(f"  warming up at {start}/s", flush=True)
    k6(start, 5 if quick else 30)
    print(f"  capacity (p99 limit {limit_ms} ms)", flush=True)
    sustained, probes = capacity(quick, limit_ms, start)
    print(f"  sustains {sustained}/s", flush=True)

    commit = run("git", "rev-parse", "--short", "HEAD").strip()
    dirty = bool(run("git", "status", "--porcelain", "--", "src", "db", "load", "scripts", "compose.yaml").strip())
    result = {
        "step": step, **config, "measuredAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "commit": commit + ("-dirty" if dirty else ""), "quick": quick,
        "criteria": {"p99BelowMs": limit_ms, "failedBelow": MAX_FAILED, "dropped": 0},
        "light": light,
        "capacity": {"sustainedRate": sustained, "probes": probes},
        "indexes": indexes,
    }
    out = ROOT / "results" / f"step-{step}{'-quick' if quick else ''}.json"
    out.parent.mkdir(exist_ok=True)
    out.write_text(json.dumps(result, indent=2) + "\n")
    print(f"  wrote {out.relative_to(ROOT)}", flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("step", help="a step number, or all")
    parser.add_argument("--quick", action="store_true", help="short runs, for CI and smoke tests")
    args = parser.parse_args()
    steps = sorted(STEPS) if args.step == "all" else [int(args.step)]
    for step in steps:
        if step not in STEPS:
            sys.exit(f"No step {step}. Steps: {sorted(STEPS)}")
    build_api()
    for step in steps:
        measure(step, args.quick)


if __name__ == "__main__":
    main()
