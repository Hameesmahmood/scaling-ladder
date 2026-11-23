-- Deterministic seed data, generated inside Postgres so it takes about a minute instead of an hour
-- of client-side inserts. Sizes come from psql variables, with defaults for the full ladder:
--   psql -v customers=100000 -v shipments=1000000 -f db/seed.sql
-- Every value is derived from the row number, so two seeds with the same sizes are identical.
\if :{?customers} \else \set customers 100000 \endif
\if :{?shipments} \else \set shipments 1000000 \endif

BEGIN;

INSERT INTO customers (id, name)
SELECT g, 'Customer ' || g
FROM generate_series(1, :customers) AS g;

-- Every 100th shipment is in transit and recent: that's the hot set of 1% that most tracking
-- traffic goes to. The rest were created over the 90 days before 1 November 2026 and delivered.
INSERT INTO shipments (id, tracking_number, customer_id, service, origin, destination, status, created_at, updated_at)
SELECT g,
       'SL' || lpad(g::text, 10, '0'),
       1 + (g * 7919) % :customers,
       (ARRAY['economy', 'standard', 'express'])[1 + g % 3],
       (ARRAY['Karachi', 'Lahore', 'Islamabad', 'Peshawar', 'Quetta', 'Multan'])[1 + g % 6],
       (ARRAY['Dubai', 'London', 'Toronto', 'Riyadh', 'Berlin', 'Sydney', 'Doha'])[1 + (g / 6) % 7],
       CASE WHEN g % 100 = 0 THEN 'in_transit' ELSE 'delivered' END,
       created,
       created + interval '2 days'
FROM generate_series(1::bigint, :shipments) AS g,
     LATERAL (SELECT timestamptz '2026-11-01 00:00:00+00'
                     - CASE WHEN g % 100 = 0 THEN (g % 72) * interval '1 hour'
                            ELSE (g % 2160) * interval '1 hour' END AS created) AS c;

-- Between 1 and 19 events per shipment, 10 on average, six hours apart.
INSERT INTO tracking_events (shipment_id, occurred_at, status, location)
SELECT s.id,
       s.created_at + k * interval '6 hours',
       CASE WHEN k = 1 THEN 'label_created' ELSE 'in_transit' END,
       (ARRAY['Karachi hub', 'Lahore hub', 'Dubai hub', 'London hub', 'Customs', 'Local depot'])[1 + (s.id + k) % 6]
FROM shipments s,
     LATERAL generate_series(1, 1 + (s.id * 31) % 19) AS k;

SELECT setval(pg_get_serial_sequence('shipments', 'id'), (SELECT max(id) FROM shipments));
SELECT setval(pg_get_serial_sequence('tracking_events', 'id'), (SELECT max(id) FROM tracking_events));

COMMIT;

ANALYZE;
