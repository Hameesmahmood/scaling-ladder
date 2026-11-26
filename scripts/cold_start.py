"""The check behind the capacity warm-up: start the API fresh, then run 1,024 a second three times.

    python3 scripts/cold_start.py

For steps 2 and 3 the first run drops requests and the later ones pass (26 Nov, at 01149be).
"""
import sys, time, json, subprocess
sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from ladder import ROOT, start_api, k6, counters, per_request, set_indexes, run
set_indexes(True)
for step in (2, 3):
    # A truly fresh process: stop the API, then start it with the step's settings.
    run("docker", "compose", "stop", "api")
    start_api(step)
    k6(0.5, 15)  # the ladder's usual warm-up
    for attempt in (1, 2, 3):
        time.sleep(5)
        before = counters()
        r = k6(1024, 30)
        pr = per_request(before, counters(), r["requests"])
        print(f"step {step} run {attempt}: 1024/s p50 {r['latencyMs']['p50']} p99 {r['latencyMs']['p99']} dropped {r['dropped']} hit {pr['cacheHitRate']}", flush=True)
