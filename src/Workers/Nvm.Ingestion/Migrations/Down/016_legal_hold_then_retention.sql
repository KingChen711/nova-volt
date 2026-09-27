-- Down removes the deleting half first, then the blocking half: never a moment with retention and no hold.
SELECT delete_job(job_id) FROM timescaledb_information.jobs WHERE proc_schema = 'ts' AND proc_name = 'enforce_retention';
DROP PROCEDURE IF EXISTS ts.enforce_retention(INT, JSONB);
DROP TABLE IF EXISTS ts.retention_log;
DROP TABLE IF EXISTS ts.legal_hold;
DROP FUNCTION IF EXISTS ts.legal_hold_append_only();
