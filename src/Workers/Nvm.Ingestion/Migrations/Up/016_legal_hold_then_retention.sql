-- M12: the blocking half ships, then the deleting half comes back behind it (migrations 007 and 010).
--
-- scope.md section 8.4: a legal_hold flag blocks EVERY retention policy. TimescaleDB's own
-- add_retention_policy cannot be blocked: it drops every chunk older than the horizon and asks
-- nothing. So retention is no longer a built-in policy but a job that walks expired chunks one by one
-- and drops a chunk only when nothing forbids it:
--
--   1. no active hold overlaps the chunk's time range. Holds are per site for the record, but a chunk
--      holds every site's rows and is dropped whole, so ANY active hold overlapping it keeps it;
--   2. for raw telemetry, the chunk holds no row RECORDED inside the horizon. That is the ADR-011
--      case migration 007 was written about: a reading stored today by a machine whose clock says
--      two years ago lands in an expired chunk. It is a fresh record, and age is judged by when the
--      system received it, not by what the machine claimed.
--
-- Every decision is written to ts.retention_log, so "why is this chunk still here" and "what did the
-- job delete" have answers after the fact.

CREATE TABLE ts.legal_hold (
    hold_id      TEXT        PRIMARY KEY,
    site_id      TEXT        NOT NULL,
    from_ts      TIMESTAMPTZ NOT NULL,
    to_ts        TIMESTAMPTZ NOT NULL,
    reason       TEXT        NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 1000),
    placed_by    TEXT        NOT NULL,
    placed_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    released_by  TEXT        NULL,
    released_at  TIMESTAMPTZ NULL,
    CHECK (to_ts > from_ts),
    CHECK ((released_at IS NULL) = (released_by IS NULL))
);

-- A hold is lifted by releasing it, never by deleting it: the hold itself is part of the record.
CREATE FUNCTION ts.legal_hold_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'legal hold % cannot be deleted; release it instead', OLD.hold_id;
    END IF;
    IF OLD.released_at IS NOT NULL
       OR NEW.hold_id <> OLD.hold_id OR NEW.site_id <> OLD.site_id OR NEW.from_ts <> OLD.from_ts
       OR NEW.to_ts <> OLD.to_ts OR NEW.reason <> OLD.reason OR NEW.placed_by <> OLD.placed_by
       OR NEW.placed_at <> OLD.placed_at THEN
        RAISE EXCEPTION 'legal hold % can only be released, once', OLD.hold_id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER tr_legal_hold_append_only
    BEFORE UPDATE OR DELETE ON ts.legal_hold
    FOR EACH ROW EXECUTE FUNCTION ts.legal_hold_append_only();

CREATE TABLE ts.retention_log (
    log_id       BIGSERIAL   PRIMARY KEY,
    run_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    relation     TEXT        NOT NULL,
    chunk        TEXT        NOT NULL,
    range_start  TIMESTAMPTZ NOT NULL,
    range_end    TIMESTAMPTZ NOT NULL,
    action       TEXT        NOT NULL CHECK (action IN ('dropped', 'held', 'recently_recorded', 'deferred')),
    hold_ids     TEXT[]      NULL
);

CREATE PROCEDURE ts.enforce_retention(job_id INT, config JSONB) LANGUAGE plpgsql AS $$
DECLARE
    target      REGCLASS := (config ->> 'relation')::REGCLASS;
    horizon     INTERVAL := (config ->> 'drop_after')::INTERVAL;
    check_recorded BOOLEAN := coalesce((config ->> 'check_recorded_at')::BOOLEAN, false);
    storage_schema TEXT;
    storage_name   TEXT;
    chunk       RECORD;
    blocking    TEXT[];
    fresh       BOOLEAN;
BEGIN
    IF target IS NULL OR horizon IS NULL THEN
        RAISE EXCEPTION 'enforce_retention needs relation and drop_after';
    END IF;

    -- A continuous aggregate stores its rows in a materialization hypertable; chunks are listed there
    -- but dropped through the aggregate, which is what drop_chunks accepts.
    SELECT materialization_hypertable_schema, materialization_hypertable_name
    INTO storage_schema, storage_name
    FROM timescaledb_information.continuous_aggregates
    WHERE format('%I.%I', view_schema, view_name)::REGCLASS = target;
    IF storage_name IS NULL THEN
        SELECT n.nspname, c.relname INTO storage_schema, storage_name
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = target;
    END IF;

    FOR chunk IN
        SELECT c.chunk_schema, c.chunk_name, c.range_start, c.range_end
        FROM timescaledb_information.chunks c
        WHERE c.hypertable_schema = storage_schema AND c.hypertable_name = storage_name
          AND c.range_end <= now() - horizon
          -- A hypertable with continuous aggregates keeps a dropped chunk in the catalogue (for
          -- invalidation); it is listed here but has no table any more.
          AND to_regclass(format('%I.%I', c.chunk_schema, c.chunk_name)) IS NOT NULL
        ORDER BY c.range_start
    LOOP
        SELECT array_agg(h.hold_id ORDER BY h.hold_id) INTO blocking
        FROM ts.legal_hold h
        WHERE h.released_at IS NULL AND h.from_ts < chunk.range_end AND h.to_ts > chunk.range_start;

        IF blocking IS NOT NULL THEN
            INSERT INTO ts.retention_log (relation, chunk, range_start, range_end, action, hold_ids)
            VALUES (target::TEXT, format('%I.%I', chunk.chunk_schema, chunk.chunk_name), chunk.range_start,
                    chunk.range_end, 'held', blocking);
            CONTINUE;
        END IF;

        IF check_recorded THEN
            EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I.%I WHERE recorded_at > $1)',
                           chunk.chunk_schema, chunk.chunk_name)
            INTO fresh USING now() - horizon;
            IF fresh THEN
                INSERT INTO ts.retention_log (relation, chunk, range_start, range_end, action)
                VALUES (target::TEXT, format('%I.%I', chunk.chunk_schema, chunk.chunk_name), chunk.range_start,
                        chunk.range_end, 'recently_recorded');
                CONTINUE;
            END IF;
        END IF;

        BEGIN
            PERFORM drop_chunks(target, older_than => chunk.range_end, newer_than => chunk.range_start);
            INSERT INTO ts.retention_log (relation, chunk, range_start, range_end, action)
            VALUES (target::TEXT, format('%I.%I', chunk.chunk_schema, chunk.chunk_name), chunk.range_start,
                    chunk.range_end, 'dropped');
        EXCEPTION WHEN deadlock_detected OR lock_not_available THEN
            -- Refresh/compression job đang giữ cùng hypertable. Bỏ chunk này ở lượt này; lượt sau thử lại.
            INSERT INTO ts.retention_log (relation, chunk, range_start, range_end, action)
            VALUES (target::TEXT, format('%I.%I', chunk.chunk_schema, chunk.chunk_name), chunk.range_start,
                    chunk.range_end, 'deferred');
        END;
    END LOOP;
END $$;

-- The horizons scope.md section 8.4 agreed to, back on the schedule — behind the hold this time. First run an
-- hour after migration: an operator placing a hold right after deploy is not racing the first deletion.
SELECT add_job('ts.enforce_retention', '1 day',
    config => '{"relation": "ts.telemetry_measurement", "drop_after": "400 days", "check_recorded_at": true}',
    initial_start => now() + INTERVAL '1 hour');
SELECT add_job('ts.enforce_retention', '1 day',
    config => '{"relation": "ts.process_signal_1m", "drop_after": "15 years"}',
    initial_start => now() + INTERVAL '1 hour');
SELECT add_job('ts.enforce_retention', '1 day',
    config => '{"relation": "ts.process_signal_machine_1m", "drop_after": "15 years"}',
    initial_start => now() + INTERVAL '1 hour');

COMMENT ON TABLE ts.legal_hold IS
    'Time ranges no retention job may delete while the hold is active (scope.md 8.4). Release, never delete.';
COMMENT ON TABLE ts.telemetry_measurement IS
    'Raw device telemetry. Hypertable on device_timestamp (ADR-011); every MetricValue kind, not just '
    'numbers. Retention runs as ts.enforce_retention behind ts.legal_hold (migration 016).';
