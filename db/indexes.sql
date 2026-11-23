-- Step 1 of the ladder. Each index matches one query's WHERE and ORDER BY exactly.
CREATE UNIQUE INDEX IF NOT EXISTS shipments_tracking_number ON shipments (tracking_number);
CREATE INDEX IF NOT EXISTS tracking_events_shipment ON tracking_events (shipment_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS shipments_customer ON shipments (customer_id, created_at DESC, id DESC);
