-- Puts the rollup's 15-year retention policy of migration 005 back on the schedule.
--
-- Same warning as the down script of 007: this re-arms unconditional deletion with no legal hold in
-- front of it. After day 400 the rollup is the only surviving record of its period, so this one
-- deletes evidence that cannot be rebuilt from anywhere.
SELECT add_retention_policy(
    'ts.process_signal_1m',
    drop_after => INTERVAL '15 years',
    if_not_exists => true);
