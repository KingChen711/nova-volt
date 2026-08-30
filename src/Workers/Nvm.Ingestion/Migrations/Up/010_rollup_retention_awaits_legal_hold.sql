-- The other half of migration 007: the one-minute rollup stops deleting itself too.
--
-- 007 unscheduled retention on raw telemetry and argued that the rollup could keep its 15-year
-- policy because it is derived data, rebuildable from raw. That argument does not survive contact
-- with the contract it was meant to satisfy. scope.md section 8.4 says the legal-hold flag blocks
-- EVERY retention policy — not every policy on raw telemetry — and a rule with one exception carved
-- out by the person implementing it is not a rule.
--
-- It does not survive the mechanism either. The rollup is only rebuildable while the raw rows it was
-- computed from still exist, and ADR-032 already records that raw is dropped at 400 days while the
-- rollup was to be kept for 15 years. So from day 401 onward the rollup is not derived data at all:
-- it is the ONLY remaining record of that period, and the retention job on it was quietly the last
-- deletion in the chain. Fifteen years is far away, which makes this the kind of thing that is
-- discovered by an auditor rather than by a test.
--
-- Both halves come back together in M12, behind the hold, and neither before it.
SELECT remove_retention_policy('ts.process_signal_1m', if_exists => true);

-- The refresh policy stays. It materializes; it does not delete.
