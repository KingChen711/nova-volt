-- Automatic retention on raw telemetry is switched OFF until the legal-hold gate exists.
--
-- Migration 004 scheduled add_retention_policy(..., drop_after => INTERVAL '400 days'), and 400 days
-- is the raw-evidence horizon scope.md section 8.4 agreed to. What the same section requires, in the
-- same table, is a legal_hold flag that blocks every retention policy — and M3 does not build it.
-- The milestone plan lists legal hold under M12, explicitly outside M3.
--
-- Shipping the deleting half without the blocking half is not most of a feature; it is the dangerous
-- half on its own. Retention drops whole chunks by device_timestamp, and ADR-011 already records
-- device_timestamp as the axis a wrong device clock moves a row along: a record written today by a
-- cycler whose clock says 2024 lands in an already-expired chunk, and the next policy run deletes it.
-- No error, no event, nothing to notice. The row it deletes is a legal record (K4), and a hold that
-- does not exist yet cannot stop it.
--
-- So the order is reversed. The block ships first, in M12, and the deletion follows it. Until then
-- raw telemetry grows without a ceiling, which is a disk problem: visible, recoverable, and cheap
-- next to losing evidence nobody knew was gone.
--
-- The counter shipped alongside this migration — nvm.ingest.retention_risk, per site — measures how
-- often a stored reading is more than one chunk away from the moment it was recorded, so M12 starts
-- from a measured rate rather than from an argument about whether the case is real. The C06 lab keeps
-- demonstrating the failure by calling drop_chunks by hand, which is now the only way it can happen.
SELECT remove_retention_policy('ts.telemetry_measurement', if_exists => true);

-- The one-minute rollup keeps its own 15-year horizon from migration 005. It is a derived read
-- product rebuildable from raw telemetry, not the record an auditor asks for, and 15 years is far
-- beyond the point where legal hold lands.

COMMENT ON TABLE ts.telemetry_measurement IS
    'Raw device telemetry. Hypertable on device_timestamp (ADR-011); every MetricValue kind, not just '
    'numbers. Retention is deliberately unscheduled until legal hold exists (M12) — see migration 007.';
