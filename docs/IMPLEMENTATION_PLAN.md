# Implementation plan: Scaling ladder

## The problem

"How would you scale this?" is the standard system design question, and the standard answers are a
list: add indexes, pool connections, add a cache, put a load balancer in front. The list is easy to
recite. What's hard, and what interviews and real incidents actually test, is knowing what each
change buys, what it costs, and where the bottleneck moves next.

This project answers that with an experiment. A small shipment-tracking API ("where's my parcel?")
starts deliberately naive and gets one change at a time. After every change the same load test
runs, and the results go on one chart: latency and throughput, step by step.

## Non-goals

- **Production capacity figures.** Everything runs on one laptop. The steps compare with each other;
  none of the numbers say what this would handle on real servers. The README will say so.
- **A complete tracking product.** Three endpoints, enough to have a hot read path, a list query and
  writes that make caching hard.
- **Auth, multi-tenancy, a UI.**
- **More than one database server**, unless the stretch goals are reached.
- **Kubernetes or autoscaling.** Load balancing is Nginx in front of a fixed number of instances.

## The API

| Method and path | Purpose | Responses |
| --- | --- | --- |
| `GET /v1/shipments/{trackingNumber}` | A shipment with its 20 most recent tracking events. The hot path | `200`, `404` |
| `GET /v1/customers/{customerId}/shipments?limit=20&cursor=` | A customer's shipments, newest first, keyset pagination | `200`, `400` bad cursor or limit |
| `POST /v1/shipments/{trackingNumber}/events` | Record a tracking event and update the shipment's status | `201`, `400`, `404` |
| `GET /healthz`, `GET /readyz` | Liveness, and readiness including Postgres and Redis | `200`, `503` |

Errors use RFC 9457 problem details, as in Week 1. Limits are between 1 and 100; the cursor is
opaque (base64 of the last row's `created_at` and `id`).

## Data

Seeded deterministically inside Postgres with `generate_series`, every value derived from the row
number. The full seed takes about 9 minutes on this laptop (535 s, mostly the 10 million events and
their foreign-key checks), once; the ladder never reseeds, it only adds or drops indexes:

- 100,000 customers.
- 1,000,000 shipments, each with a tracking number like `SL0001234567`, a customer, a service level,
  a status and timestamps over the last 90 days.
- About 10,000,000 tracking events, an average of 10 per shipment.

That's big enough that a missing index means reading millions of rows per request, and small enough
to seed and reset quickly.

**The baseline schema has primary keys only.** No index on `tracking_number`, on
`tracking_events.shipment_id`, or on `shipments.customer_id`. That's the naive starting point, and
step 1 fixes it. It's deliberately slow, and the README will say so.

## The workload

The same k6 script drives every step, as an **open model** (k6's `constant-arrival-rate` executor):
requests start on a fixed schedule whether or not earlier ones have finished. A closed model, where
each virtual user waits for its reply, slows down with the server and hides the queueing the ladder
is trying to show (coordinated omission).

The traffic mix:

- **90% `GET` a shipment.** 80% of these go to a hot set of 10,000 shipments currently in transit,
  and 20% to any of the million. Real tracking traffic is skewed like this: people refresh parcels
  that are on their way. Caches look great on skewed traffic and useless on uniform traffic, so the
  skew is stated, not hidden.
- **8% list a customer's shipments** (first page).
- **2% post a tracking event** to a shipment in the hot set, which is what forces invalidation.

## Two measurements per step

1. **Latency at a fixed light rate.** p50, p95 and p99 over 3 minutes at 0.5 requests a second,
   after a 15-second warm-up: 90 requests, so p99 is close to the slowest of them, and the README
   says so. This shows what one request costs. The rate is about half of what the baseline can
   sustain (one baseline lookup takes about 1.6 s), and it's the same for every step.
2. **Capacity.** The highest arrival rate the step sustains with errors under 0.1%, no dropped
   iterations (k6 couldn't start a request on time), and p99 under 250 ms, or under twice the
   step's own light-rate p99 if that's higher. Only the baseline needs that second rule: one of its
   requests takes 1.6 s on its own, so it could never meet 250 ms at any rate, and "capacity 0"
   would hide that it does keep up at about one request a second. Twice the single-request time
   still catches the point where requests start to queue. Found by doubling the rate from 0.25 a
   second until a probe fails, then bisecting to within 10%. Each probe runs long enough for about
   300 requests (30 s to 2 minutes).

Results for each step go to `results/step-N.json` with the commit, the configuration, the k6
summary and the database's own counters (queries run, connections opened).

**Resources are pinned** so steps compare fairly: each API instance gets 1 CPU, Postgres 2 CPUs and
2 GB, Redis 1 CPU, and k6 runs outside those limits. Without pinning, step 5's extra instances
would just fight over the same cores as everything else.

## The ladder

One codebase. Each step is the previous one plus one change, switched on by configuration, so any
step can be rerun on its own: `./scripts/ladder.sh 3`.

| Step | Change | What it should show | The cost |
| --- | --- | --- | --- |
| 0 | Baseline: primary keys only, a new database connection per request, no cache, one instance | The starting point | |
| 1 | **Indexes** on `tracking_number`, `tracking_events (shipment_id, occurred_at DESC)` and `shipments (customer_id, created_at DESC)` | Plans go from sequential scans to index scans (`EXPLAIN ANALYZE` before and after, published) | Slower writes and disk space, both measured |
| 2 | **Connection pooling** (Npgsql's pool, sized deliberately) | Requests stop paying for a new connection and server process | Requests queue when the pool is full |
| 3 | **Cache-aside in Redis** for `GET` shipment, with a TTL, and **invalidation** on every write | Most hot reads never touch Postgres | Stale reads are possible in a race; memory; one more thing to run |
| 4 | **Stampede protection**: one rebuild per key at a time (request coalescing), plus jittered TTLs | A hot key's expiry causes one database query, not hundreds | Some requests wait for another's result |
| 5 | **Load balancing**: Nginx in front of three API instances | Capacity rises until the next bottleneck | More processes, and a pool per instance adds up on the database |

The order is chosen so each step's effect is visible: pooling barely registers while every request
reads millions of rows, so indexes come first.

## Each concept switched off

As in Weeks 1 and 2, each concept gets a test or experiment that switches it off and shows what
breaks. The ladder itself is the performance evidence; these cover correctness and failure:

| Concept | With it | Switched off, this breaks |
| --- | --- | --- |
| Indexes | The lookup plan is an index scan | The plan is a sequential scan over 10 million rows |
| Connection pooling | 50 concurrent requests open at most the pool's 5 connections | 50 requests open 50 connections |
| Cache-aside and TTL | A second read doesn't query Postgres; after the TTL it does | Every read queries Postgres |
| Invalidation | A read straight after a write sees the new status | The read returns the old status until the TTL expires (stale reads counted in the load test too: `scripts/stale_reads.py`) |
| Failing fast when Redis is down | Reads and writes carry on through Postgres, each in milliseconds | Requests queue for the missing Redis and each waits out a 5-second timeout |
| Stampede protection | 200 concurrent misses on one key run 1 query | They run 200 queries |
| Load balancing | Killing one of three instances mid-run causes no failed `GET`s (Nginx retries idempotent requests on another instance; `scripts/kill_instance.py`) | Requests to the dead instance fail |
| TTL jitter | 100 entries cached together expire spread over 48 to 72 s | They all expire within a second or two of each other |
| p99 (measurement) | Reported per step | An average that hides the slow tail (shown side by side once) |

Integration tests run against real Postgres and Redis in Testcontainers.

### Decisions made while building step 3

- **Redis is an optimisation, never a dependency.** Any Redis error or timeout falls through to
  Postgres. The client is set to fail fast (`BacklogPolicy.FailFast`, 1-second timeouts): its
  default queues commands while it reconnects, which turned a dead cache into a 5-second wait on
  every request.
- **Delete after commit, not before.** Deleting first lets a read between the delete and the commit
  refill the old row. The remaining race (a slow read refills the entry just after the delete) is
  accepted: the TTL bounds it, and the stale-read experiment counts it.
- **404s aren't cached.** The workload only asks for shipments that exist. A public API would cache
  them briefly, so guessing tracking numbers can't go around the cache.
- **The index write cost is measured with `pgbench`, not through the API** (`scripts/write_cost.py`).
  Without indexes the API's write starts with a 70 ms lookup, which would hide what the indexes cost.
- **The capacity search warms up at its starting rate first.** Step 3's first run measured 960 a
  second, below step 2. The cause was the method, not the cache: its search started at 1,024 on a
  freshly started API, which fails a first heavy probe whatever the step (steps 2 and 3 both did,
  then passed every run after). Each search now runs 30 s at its starting rate, discarded.
- **A failed probe is run twice more, and passes on 2 of 3.** Step 3's next search measured 1,920 a
  second, ended by one probe that 22 late starts out of 61,000 failed. Repeated fixed-rate runs
  (`scripts/repeat.py`, 3 runs each) showed step 3 passing 2,560 a second 3 times in 3 and step 2
  failing it 3 times in 3, so one run near the limit can't decide a step's capacity.
- **Redis is emptied, and its statistics reset, at the start of every step**, and every probe
  records the hit rate, keys and memory, so a step never inherits another's warm cache.

### Decisions made while building steps 4 and 5

- **Coalescing is per instance.** At step 5 a key can be rebuilt by each of the three instances at
  once, three reads instead of one. A Redis lock would make it one, at the cost of a round trip on
  every miss and a lock that can outlive a crashed holder. Three is already the stampede solved.
- **The shared rebuild ignores any one caller's cancellation.** A client that hangs up stops waiting,
  but the read carries on for the others.
- **A miss looks in Redis once more before it reads Postgres**, so one that arrives just after a
  rebuild finished doesn't start a second. From step 4, Redis's own hit and miss counts therefore
  overstate misses, and the ladder takes the cache's hit rate from the instances' counters (`/stats`).
- **A write also drops any rebuild in progress on its instance.** Otherwise a read that starts after
  the write could join a rebuild that began before it, and return the old status: a new way to read
  stale that coalescing would have added. The stale-read experiment runs at step 4 too.
- **The stampede test makes the rebuild slow on purpose**, by locking the shipments table while 200
  requests arrive for one cold key. With the indexes the real rebuild takes about a millisecond, too
  short for misses to pile up in a test; under production load, or with a slower rebuild, they do.
- **Nginx retries idempotent requests only.** A `GET` that fails on one instance (refused, reset, or
  timed out) goes to another; a `POST` already sent doesn't, because recording an event twice is
  worse than failing it. The kill experiment counts both.
- **Nginx re-resolves the instances every 2 s** (`resolve`, open source since 1.27.3), so a killed
  instance drops out of Docker's DNS and out of rotation without a reload.
- **Before step 5 the ladder sends load straight to the API**, so steps 0 to 4 don't pay for the proxy
  hop; nginx stays up so `localhost:8090` always works.
- **Every probe records each container's CPU**, sampled halfway through, to show where the bottleneck is.
- **Step 5 runs out of laptop.** The Mac has 4 physical cores (8 hyperthreads). Step 5 pins 7 CPUs
  (three instances, Postgres 2, Redis, nginx) and k6 needs 1.5 to 2.5 more, so at 3,072 a second the
  containers already use about 7.3 of the 8. Its capacity is the machine's, not load balancing's,
  and the README must say so: on separate machines the instances wouldn't share cores.

## Code structure

- `src/Tracker.Api`: endpoints, the store (Npgsql and Dapper, hand-written SQL), the cache layer
  and its coalescing, configuration for each step.
- `db/`: schema, seed and the step-1 index script.
- `load/`: the k6 script and the capacity search.
- `scripts/ladder.sh`: resets the database to the step's schema, starts the stack with the step's
  configuration, warms up, measures, and writes the results.
- `tests/Tracker.Tests`: xUnit with Testcontainers.
- `tools/`: the chart script.

Stack: .NET 10 and ASP.NET Core minimal APIs, Npgsql and Dapper, StackExchange.Redis, Postgres 18,
Redis 8, Nginx, k6, Docker Compose.

## Definition of done

- `./scripts/ladder.sh all` reruns every step and rewrites the results.
- One chart shows latency (p50, p95, p99 at the fixed rate) and capacity for every step.
- Every row of the switched-off table has a passing test or a published experiment.
- CI runs the tests, and a short smoke run of the ladder.
- The README covers the problem, the ladder with its results, how to rerun it, the cost of each step
  and honest limits.
- The explain test is passed.

## Schedule (Monday 23 to Sunday 29 November)

- **Monday:** concepts, this plan, README pitch, skeleton (solution, compose with Postgres and Redis,
  schema and seed, health checks), CI.
- **Tuesday:** the three endpoints at baseline, the k6 workload and capacity search,
  `ladder.sh`, step 0 measured; fix the light rate.
- **Wednesday:** step 1 (indexes, with plans before and after) and step 2 (pooling), with their tests.
- **Thursday:** step 3 (cache-aside, TTL, invalidation), with the stale-read test.
- **Friday:** step 4 (stampede protection) and step 5 (Nginx, three instances, the kill experiment).
- **Saturday:** the full ladder run, the chart, and a stretch goal only if everything else is done.
- **Sunday:** results, README, explain test, ship.

If time runs short, cut in this order: the stretch goals, the instance-kill experiment, then the
capacity search's resolution (fewer, coarser rate steps).
