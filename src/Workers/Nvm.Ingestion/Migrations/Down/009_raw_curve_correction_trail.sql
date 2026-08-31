-- Drops the correction trail.
--
-- Reversible in schema terms and lossy in evidence terms: the recorded actor and reason of every
-- correction go with the columns. Rolling this back on a database an auditor has already been shown
-- is not a schema change, it is destroying the answer to "who changed this".
DROP INDEX ts.uq_raw_curve_archive_supersedes;

ALTER TABLE ts.raw_curve_archive
    DROP CONSTRAINT fk_raw_curve_archive_supersedes,
    DROP CONSTRAINT ck_raw_curve_archive_supersedes_other,
    DROP CONSTRAINT ck_raw_curve_archive_reason,
    DROP CONSTRAINT ck_raw_curve_archive_actor;

ALTER TABLE ts.raw_curve_archive
    DROP COLUMN supersedes_archive_id,
    DROP COLUMN reason,
    DROP COLUMN actor;
