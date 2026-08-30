-- Jobs truoc, view sau: mot lan chay cua scheduler chen vao giua se lam rollback that bai giua chung.
SELECT remove_compression_policy('ts.process_signal_machine_1m', if_exists => true);
SELECT remove_continuous_aggregate_policy('ts.process_signal_machine_1m', if_exists => true);

DROP MATERIALIZED VIEW IF EXISTS ts.process_signal_machine_1m;
