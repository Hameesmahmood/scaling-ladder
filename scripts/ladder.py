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
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Each step is the previous one plus one change.
STEPS = {
    0: {"name": "Baseline", "change": "Primary keys only, a new connection per request, no cache, one instance",
        "indexes": False, "pooling": False},
    1: {"name": "Indexes", "change": "Indexes matching each query's WHERE and ORDER BY",
        "indexes": True, "pooling": False},
    2: {"name": "Connection pooling", "change": "Npgsql's pool, 20 connections per instance",
        "indexes": True, "pooling": True},
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


def counters():
    """Postgres's own totals since it started: sessions (connections) opened, and rows read."""
    row = psql("SELECT sessions, tup_returned + tup_fetched FROM pg_stat_database WHERE datname = 'tracker'").split("|")
    return {"sessions": int(row[0]), "rowsRead": int(row[1])}


def build_api():
    """Builds the API image once per run, retrying: fetching the .NET base image can time out."""
    for attempt in range(1, 4):
        if subprocess.run(["docker", "compose", "build", "api"], cwd=ROOT, capture_output=True).returncode == 0:
            return
        print(f"  building the API image failed (attempt {attempt}), retrying", flush=True)
        time.sleep(10)
    sys.exit("Couldn't build the API image.")


def start_api(step):
    env = {**os.environ, "TRACKER_POOLING": str(STEPS[step]["pooling"]).lower()}
    run("docker", "compose", "up", "-d", "--no-build", "--wait", "api", env=env)


def k6(rate, seconds):
    out = run("docker", "compose", "run", "--rm", "-e", f"RATE={rate}", "-e", f"DURATION={seconds}s",
              "k6", "run", "--quiet", "--no-color", "/load/workload.js")
    return json.loads(out.strip().splitlines()[-1])


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

    def probe(rate):
        time.sleep(5)  # let any queue from the last probe drain
        result = k6(rate, probe_seconds(rate, quick))
        result["sustained"] = sustains(result, limit_ms)
        probes.append(result)
        print(f"    {rate:>8g}/s  p99 {result['latencyMs']['p99']} ms  failed {result['failedRate']:.2%}  "
              f"dropped {result['dropped']}  {'ok' if result['sustained'] else 'too much'}", flush=True)
        return result["sustained"]

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
    after = counters()
    print(f"    p50 {light['latencyMs']['p50']} ms  p95 {light['latencyMs']['p95']} ms  p99 {light['latencyMs']['p99']} ms", flush=True)
    limit_ms = max(P99_LIMIT_MS, round(2 * light["latencyMs"]["p99"]))
    print(f"  capacity (p99 limit {limit_ms} ms)", flush=True)
    sustained, probes = capacity(quick, limit_ms, 0.25 if quick else starting_rate(step))
    print(f"  sustains {sustained}/s", flush=True)

    commit = run("git", "rev-parse", "--short", "HEAD").strip()
    dirty = bool(run("git", "status", "--porcelain", "--", "src", "db", "load", "scripts", "compose.yaml").strip())
    result = {
        "step": step, **config, "measuredAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "commit": commit + ("-dirty" if dirty else ""), "quick": quick,
        "criteria": {"p99BelowMs": limit_ms, "failedBelow": MAX_FAILED, "dropped": 0},
        "light": {**light, "perRequest": {k: round((after[k] - before[k]) / max(light["requests"], 1), 2) for k in after}},
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
