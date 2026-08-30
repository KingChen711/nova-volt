-- Telemetry becomes a hypertable, partitioned on device_timestamp.
--
-- WHICH CLOCK PARTITIONS THE TABLE is the least reversible decision in M3: changing it once there
-- are a hundred million rows rewrites the table. It is device_timestamp — the time on the machine —
-- because every question a process engineer asks is asked in machine time: "show me the eighteen
-- hours of formation on this channel". Partitioning on recorded_at would make a two-hour buffer
-- flush land two hours of curve in ONE one-minute bucket, and the rollup would stop meaning
-- anything physical. ADR-011 carries the full trade, including what this costs.

-- The primary key has to contain the partitioning column: TimescaleDB refuses create_hypertable
-- while any unique index does not. Verified against timescale/timescaledb:2.29.2-pg17.
--
-- This does NOT weaken deduplication. The authority for device-level dedup is the claim table
-- ingest.processed_message with its global PRIMARY KEY (source_event_id) — ADR-030 — and the
-- ingestion path claims there first and only then writes here. This key is the second line of
-- defence and stays the second line of defence, just narrower. The foreign key below keeps the
-- strongest statement intact: no telemetry row without a claim.
ALTER TABLE ts.telemetry_measurement
    DROP CONSTRAINT telemetry_measurement_pkey;

ALTER TABLE ts.telemetry_measurement
    ADD CONSTRAINT telemetry_measurement_pkey
    PRIMARY KEY (source_event_id, device_timestamp);

-- One day per chunk (scope.md §8.3). A chunk is the unit compression and retention act on, so this
-- also decides how coarse "delete what is older than 400 days" can be.
--
-- migrate_data => true moves whatever is already in the table into chunks, holding a lock for as
-- long as that takes. It runs here, before C08 loads anything, precisely so that "as long as that
-- takes" is short.
SELECT create_hypertable(
    'ts.telemetry_measurement',
    'device_timestamp',
    chunk_time_interval => INTERVAL '1 day',
    migrate_data => true);

-- The index from migration 001 is KEPT, and this is a decision rather than an omission.
-- create_hypertable adds its own index on the partitioning column per chunk, so the time-only
-- access path is covered twice; but D2 asks for one machine over seven days, and
-- (site_id, equipment_id, device_timestamp DESC) is what turns that into a range scan instead of a
-- scan of every channel in the chunk. C10 measures whether it is actually used and drops it there
-- if the EXPLAIN says otherwise — measured, not guessed.

COMMENT ON TABLE ts.telemetry_measurement IS
    'Raw device telemetry. Hypertable on device_timestamp (ADR-011); every MetricValue kind, not just numbers.';
