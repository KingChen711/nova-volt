-- Raw telemetry has two independent lifecycle decisions:
--
-- * compression starts after seven days, when investigations are much less likely to need the
--   newest samples repeatedly; and
-- * retention removes complete chunks after 400 days, which is the raw-evidence horizon agreed in
--   scope.md section 8.4. The one-minute rollup gets a different, much longer horizon in C07.
--
-- Segment boundaries deliberately follow one signal on one piece of equipment at one site. Values
-- next to each other inside that segment are samples from the same physical curve, so columnar
-- compression can exploit both repeated identifiers and correlation over time. Mixing equipment or
-- signals in one segment would discard that property.
ALTER TABLE ts.telemetry_measurement SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'site_id, equipment_id, signal_code',
    timescaledb.compress_orderby = 'device_timestamp DESC'
);

SELECT add_compression_policy(
    'ts.telemetry_measurement',
    compress_after => INTERVAL '7 days');

-- Retention follows device_timestamp because that is the hypertable partitioning clock (ADR-011).
-- A device clock that is wrong by years can therefore put a valid row straight into an expired
-- chunk. The C06 retention lab measures that failure mode rather than hiding it in a comment.
SELECT add_retention_policy(
    'ts.telemetry_measurement',
    drop_after => INTERVAL '400 days');
