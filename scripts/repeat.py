#!/usr/bin/env python3
"""Runs steps at fixed rates, several times each, to see how much one capacity search can be trusted.

    python3 scripts/repeat.py --steps 2 3 --rates 1536 2048 2560 --repeats 3

Built after two capacity searches of step 3 gave 2,816 and 1,920 a second. For every run it
records the ladder's pass or fail, latency, the database work per request, the cache's hit rate,
and each container's CPU sampled halfway through. Writes results/repeat-steps-<steps>.json.
"""
import argparse
import json
import subprocess
import threading
import time
from datetime import datetime, timezone

from ladder import (P99_LIMIT_MS, ROOT, build_api, counters, k6, per_request, run, set_indexes,
                    start_api, sustains)

SECONDS = 30


def cpu():
    """Each container's CPU use right now, in cores (docker stats reports percent of one core)."""
    out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.Name}} {{.CPUPerc}}"],
                         capture_output=True, text=True, check=True).stdout
    usage = {}
    for line in out.splitlines():
        name, percent = line.rsplit(" ", 1)
        service = "k6" if "-k6-" in name else name.removeprefix("scaling-ladder-").removesuffix("-1")
        usage[service] = round(float(percent.rstrip("%")) / 100, 2)
    return usage


def timed_run(rate):
    result, sample = {}, {}
    worker = threading.Thread(target=lambda: result.update(k6(rate, SECONDS)))
    worker.start()
    time.sleep(SECONDS / 2)
    sample.update(cpu())
    worker.join()
    return result, sample


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--steps", type=int, nargs="+", required=True)
    parser.add_argument("--rates", type=float, nargs="+", required=True)
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()

    build_api()
    set_indexes(True)
    runs = []
    for step in args.steps:
        print(f"\nStep {step}", flush=True)
        run("docker", "compose", "stop", "api")
        start_api(step)
        # A fresh API fails its first heavy run (scripts/cold_start.py), so warm up at the lowest
        # rate first, the same for every step.
        k6(min(args.rates), SECONDS)
        for repeat in range(1, args.repeats + 1):
            for rate in args.rates:
                time.sleep(5)
                before = counters()
                result, usage = timed_run(rate)
                result["perRequest"] = per_request(before, counters(), result["requests"])
                result["sustained"] = sustains(result, P99_LIMIT_MS)
                result["cpuCores"] = usage
                runs.append({"step": step, "repeat": repeat, **result})
                hit = result["perRequest"]["cacheHitRate"]
                print(f"  #{repeat} {rate:>6g}/s  p99 {result['latencyMs']['p99']:>8} ms  dropped {result['dropped']:>5}  "
                      f"{'ok      ' if result['sustained'] else 'too much'}  rows/req {result['perRequest']['rowsRead']:>6}  "
                      f"{'' if hit is None else f'hits {hit:.0%}  '}cpu api {usage.get('api')} db {usage.get('db')} "
                      f"redis {usage.get('redis')} k6 {usage.get('k6')}", flush=True)

    commit = run("git", "rev-parse", "--short", "HEAD").strip()
    dirty = bool(run("git", "status", "--porcelain", "--", "src", "db", "load", "scripts", "compose.yaml").strip())
    out = ROOT / "results" / f"repeat-steps-{'-'.join(map(str, args.steps))}.json"
    out.write_text(json.dumps({
        "measuredAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "commit": commit + ("-dirty" if dirty else ""), "secondsPerRun": SECONDS, "p99LimitMs": P99_LIMIT_MS,
        "runs": runs,
    }, indent=2) + "\n")
    print(f"\n  wrote {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
