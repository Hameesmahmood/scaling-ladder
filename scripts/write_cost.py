#!/usr/bin/env python3
"""Measures what step 1's indexes cost writes: the same write, with and without them.

    python3 scripts/write_cost.py            # 3 rounds of 30 s each way
    python3 scripts/write_cost.py --quick

The write is what POST /events does once it has the shipment's id: insert a tracking event and
update the shipment, in one transaction. pgbench runs it straight against Postgres, so the
measurement isn't mixed up with the lookup (which without indexes takes most of a second). The
update rewrites the row without changing any value, so the seeded data stays as it was, but it
still writes a new row version, as a real status change does. Rounds alternate without and with,
so drift (caches, checkpoints) hits both alike. The rows it inserts are deleted at the end.
Writes results/index-write-cost.json.
"""
import argparse
import json
import statistics
import subprocess
from datetime import datetime, timezone

from ladder import ROOT, psql, run, set_indexes

CLIENTS = 4
LOCATION = "write-cost bench"
SCRIPT = f"""\\set id random(1, 1000000)
BEGIN;
INSERT INTO tracking_events (shipment_id, occurred_at, status, location) VALUES (:id, now(), 'in_transit', '{LOCATION}');
UPDATE shipments SET status = status, updated_at = updated_at WHERE id = :id;
COMMIT;
"""


def db(*args, stdin=None):
    return subprocess.run(["docker", "compose", "exec", "-T", "db", *args], cwd=ROOT, input=stdin,
                          capture_output=True, text=True, check=True).stdout


def bench(seconds):
    db("sh", "-c", "rm -f /tmp/wc_log* && cat > /tmp/write.sql", stdin=SCRIPT)
    psql("CHECKPOINT")
    wal_before = psql("SELECT pg_current_wal_lsn()")
    out = db("pgbench", "-n", "-U", "tracker", "-d", "tracker", "-f", "/tmp/write.sql", "-c", str(CLIENTS),
             "-j", str(CLIENTS), "-T", str(seconds), "-l", "--log-prefix=/tmp/wc_log")
    wal = int(psql(f"SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '{wal_before}')"))
    # One line per transaction: client, transaction number, latency in microseconds, ...
    latencies = sorted(int(line.split()[2]) / 1000 for line in db("sh", "-c", "cat /tmp/wc_log*").splitlines() if line)
    tps = next(float(line.split("=")[1].split()[0]) for line in out.splitlines() if line.startswith("tps"))
    pick = lambda q: round(latencies[min(len(latencies) - 1, int(q * len(latencies)))], 2)
    return {"transactions": len(latencies), "perSecond": round(tps, 1),
            "latencyMs": {"p50": pick(0.5), "p95": pick(0.95), "p99": pick(0.99), "avg": round(statistics.fmean(latencies), 2)},
            "walBytesPerWrite": round(wal / len(latencies))}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--quick", action="store_true")
    args = parser.parse_args()
    rounds, seconds = (1, 5) if args.quick else (3, 30)

    runs = {"withoutIndexes": [], "withIndexes": []}
    try:
        for r in range(1, rounds + 1):
            for on in (False, True):
                set_indexes(on)
                result = bench(seconds)
                runs["withIndexes" if on else "withoutIndexes"].append(result)
                print(f"  round {r}, indexes {'on ' if on else 'off'}: {result['perSecond']:>7} writes/s  "
                      f"p50 {result['latencyMs']['p50']} ms  p99 {result['latencyMs']['p99']} ms  "
                      f"WAL {result['walBytesPerWrite']} B/write", flush=True)
    finally:
        # Deleting by location scans the table, but only once, and leaves the seed as it was.
        print("  deleting the benchmark's rows", flush=True)
        set_indexes(True)
        psql(f"DELETE FROM tracking_events WHERE location = '{LOCATION}'")
        psql("VACUUM ANALYZE tracking_events, shipments")

    median = lambda key, sub=None: {
        k: statistics.median((r[key][sub] if sub else r[key]) for r in v) for k, v in runs.items()}
    summary = {"perSecond": median("perSecond"), "p50Ms": median("latencyMs", "p50"),
               "p99Ms": median("latencyMs", "p99"), "walBytesPerWrite": median("walBytesPerWrite")}
    commit = run("git", "rev-parse", "--short", "HEAD").strip()
    dirty = bool(run("git", "status", "--porcelain", "--", "src", "db", "scripts").strip())
    out = ROOT / "results" / f"index-write-cost{'-quick' if args.quick else ''}.json"
    out.write_text(json.dumps({
        "measuredAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "commit": commit + ("-dirty" if dirty else ""), "clients": CLIENTS, "secondsPerRound": seconds,
        "write": SCRIPT, "medians": summary, "rounds": runs,
    }, indent=2) + "\n")
    print(f"  medians: {json.dumps(summary)}\n  wrote {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
