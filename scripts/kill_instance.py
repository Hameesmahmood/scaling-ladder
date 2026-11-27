#!/usr/bin/env python3
"""Kills one of step 5's three API instances mid-run, with nginx's retries on and then off.

    python3 scripts/kill_instance.py           # 60 s at 1,000 a second, one instance killed at 20 s
    python3 scripts/kill_instance.py --quick

With retries on, a GET that fails on the dead instance (refused, or reset mid-request) is sent to
another, so no GET should fail. A POST already sent isn't retried, since recording it twice would be
worse, so the few in flight at the moment of the kill may fail. With retries off, every request
routed to the dead instance before nginx notices fails. Writes results/kill-instance.json.
"""
import argparse
import json
import re
import threading
from collections import Counter
import time
from datetime import datetime, timezone

from ladder import ROOT, build_api, k6, run, set_indexes, start_api

STEP = 5
RATE = 1000


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--quick", action="store_true")
    args = parser.parse_args()
    seconds, kill_at = (15, 5) if args.quick else (60, 20)

    build_api()
    set_indexes(True)
    runs = {}
    for retries in (True, False):
        label = "withRetries" if retries else "withoutRetries"
        print(f"\nRetries {'on' if retries else 'off'}: {RATE}/s for {seconds} s, one instance killed at {kill_at} s", flush=True)
        start_api(STEP, NGINX_CONF="nginx.conf" if retries else "no-retry.conf")
        k6(RATE // 2, 15)  # warm the fresh instances up (scripts/cold_start.py)
        victim = run("docker", "compose", "ps", "-q", "api").split()[1]
        killed = {}

        def kill():
            run("docker", "kill", "-s", "KILL", victim)
            killed["at"] = round(time.monotonic() - started, 1)

        started = time.monotonic()
        timer = threading.Timer(kill_at, kill)
        timer.start()
        result = k6(RATE, seconds)
        timer.join()
        result["killedAtSeconds"] = killed.get("at")
        # What nginx saw: each upstream error, by kind ("connect() failed", "upstream prematurely
        # closed connection", ...), and whether it then tried another instance.
        log = run("docker", "compose", "logs", "--no-log-prefix", "nginx")
        errors = [line for line in log.splitlines() if "upstream" in line and ("[error]" in line or "[warn]" in line)]
        kind = lambda line: re.sub(r"^.*\[(?:error|warn)\] \d+#\d+: \*\d+ ", "", line).split(" while ")[0].split(" (")[0]
        result["nginxErrors"] = dict(Counter(kind(e) for e in errors))
        result["nginxErrorSamples"] = errors[:5]
        runs[label] = result
        for endpoint, counts in result["failedByEndpoint"].items():
            print(f"  {endpoint:<15} {counts['failed']:>5} failed of {counts['requests']:,}", flush=True)
        print(f"  nginx: {result['nginxErrors']}", flush=True)

    commit = run("git", "rev-parse", "--short", "HEAD").strip()
    dirty = bool(run("git", "status", "--porcelain", "--", "src", "load", "scripts", "nginx", "compose.yaml").strip())
    out = ROOT / "results" / f"kill-instance{'-quick' if args.quick else ''}.json"
    out.write_text(json.dumps({
        "step": STEP, "rate": RATE, "seconds": seconds, "killAtSeconds": kill_at,
        "measuredAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "commit": commit + ("-dirty" if dirty else ""), **runs,
    }, indent=2) + "\n")
    print(f"  wrote {out.relative_to(ROOT)}")
    start_api(STEP)  # all three back, with retries on


if __name__ == "__main__":
    main()
