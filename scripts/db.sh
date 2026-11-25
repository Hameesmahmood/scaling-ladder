#!/usr/bin/env bash
# Database helpers for the ladder.
#   ./scripts/db.sh seed        create the baseline schema and seed the full dataset (about 9 minutes)
#   ./scripts/db.sh reset       drop everything, then seed
#   ./scripts/db.sh psql        open a psql shell
set -euo pipefail
cd "$(dirname "$0")/.."
psql() { docker compose exec -T db psql -v ON_ERROR_STOP=1 -U tracker -d tracker -q "$@"; }

case "${1:-}" in
  seed)
    docker compose up -d --wait db
    psql < db/schema.sql
    echo "Seeding 1,000,000 shipments and about 10,000,000 events..."
    start=$(date +%s)
    psql < db/seed.sql
    echo "Seeded in $(( $(date +%s) - start )) s."
    ;;
  reset)
    docker compose up -d --wait db
    psql -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"
    "$0" seed
    ;;
  psql)
    docker compose exec db psql -U tracker -d tracker
    ;;
  *)
    sed -n '2,5p' "$0"
    exit 1
    ;;
esac
