#!/usr/bin/env python3
"""Counts stale reads at step 3 or later, with invalidation on and then switched off.

    python3 scripts/stale_reads.py           # step 3, 120 s each at 200 requests a second
    python3 scripts/stale_reads.py --step 4  # with coalescing, which adds a way to read stale
    python3 scripts/stale_reads.py --quick

Runs the ladder's workload with CHECK_STALE=1: every write becomes read, write, read again, and the
second read is checked for the event just written. With invalidation, a stale read needs the race
in ShipmentCache (a slower read refilling the entry after the write deleted it). Without it, the
first read cached the shipment, so the second one should be stale almost every time.
Writes results/stale-reads.json (step 3) or results/stale-reads-step-N.json.
"""
import argparse
import json
from datetime import datetime, timezone

from ladder import ROOT, build_api, counters, k6, per_request, run, set_indexes, start_api

RATE = 200


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--quick", action="store_true")
    parser.add_argument("--step", type=int, default=3)
    args = parser.parse_args()
    seconds = 10 if args.quick else 120

    build_api()
    set_indexes(True)
    runs = {}
    for invalidation in (True, False):
        label = "withInvalidation" if invalidation else "withoutInvalidation"
        print(f"\nInvalidation {'on' if invalidation else 'off'}: {RATE}/s for {seconds} s", flush=True)
        start_api(args.step, TRACKER_CACHE_INVALIDATION=invalidation)
        before = counters()
        result = k6(RATE, seconds, "CHECK_STALE=1")
        result["perRequest"] = per_request(before, counters(), result["requests"])
        checked, stale = result["readsAfterWrite"], result["staleReads"]
        result["staleShare"] = round(stale / checked, 4) if checked else None
        print(f"  {stale} stale of {checked} reads after a write; failed {result['failedRate']:.2%}", flush=True)
        runs[label] = result

    commit = run("git", "rev-parse", "--short", "HEAD").strip()
    dirty = bool(run("git", "status", "--porcelain", "--", "src", "load", "scripts", "compose.yaml").strip())
    name = "stale-reads" + ("" if args.step == 3 else f"-step-{args.step}") + ("-quick" if args.quick else "")
    out = ROOT / "results" / f"{name}.json"
    out.write_text(json.dumps({
        "step": args.step, "rate": RATE, "seconds": seconds, "ttlSeconds": 60,
        "measuredAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "commit": commit + ("-dirty" if dirty else ""), **runs,
    }, indent=2) + "\n")
    print(f"  wrote {out.relative_to(ROOT)}")
    # Leave the API as the ladder expects it.
    start_api(args.step)


if __name__ == "__main__":
    main()
