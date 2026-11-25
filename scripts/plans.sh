#!/usr/bin/env bash
# Captures EXPLAIN (ANALYZE, BUFFERS) for the API's three hot queries, without and with the step-1
# indexes, into results/plans-without-indexes.txt and results/plans-with-indexes.txt.
# Leaves the indexes in place afterwards. Run it while nothing else is using the database.
set -euo pipefail
cd "$(dirname "$0")/.."
psql() { docker compose exec -T db psql -v ON_ERROR_STOP=1 -U tracker -d tracker -q "$@"; }

queries() {
  cat <<'SQL'
\echo '--- GET /v1/shipments/{trackingNumber}: find the shipment'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) SELECT id FROM shipments WHERE tracking_number = 'SL0000500100';
\echo '--- GET /v1/shipments/{trackingNumber}: its 20 most recent events'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) SELECT occurred_at, status, location FROM tracking_events WHERE shipment_id = 500100 ORDER BY occurred_at DESC LIMIT 20;
\echo '--- GET /v1/customers/{customerId}/shipments: the first page'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) SELECT id, tracking_number, service, destination, status, created_at FROM shipments WHERE customer_id = 4242 ORDER BY created_at DESC, id DESC LIMIT 21;
SQL
}

psql -c "DROP INDEX IF EXISTS shipments_tracking_number, tracking_events_shipment, shipments_customer"
psql -c "ANALYZE"
queries | psql > results/plans-without-indexes.txt
start=$(date +%s)
psql < db/indexes.sql
echo "Built the indexes in $(( $(date +%s) - start )) s."
psql -c "ANALYZE"
queries | psql > results/plans-with-indexes.txt
grep -h -E "^---|Execution Time" results/plans-without-indexes.txt results/plans-with-indexes.txt
