-- K5 for the raw-curve archive: a correction says who made it, why, and what it replaces.
--
-- Migration 006 called this table a legal record and made it append-only, with corrections expressed
-- as another row rather than as an edit. That is the right shape and it was half built: the new row
-- carried no reason, no actor, and no link to the row it corrects. An auditor reading two rows for
-- the same channel and interval could see that the evidence changed and could not see who changed it
-- or why, which is exactly the question a correction exists to answer (AGENTS.md K5).
--
-- Existing rows are backfilled rather than deleted. They were written by C12 before this column
-- existed, and saying so is more honest than inventing an actor for them; the default is dropped
-- immediately afterwards so nothing written from here on can leave the fields unstated.

ALTER TABLE ts.raw_curve_archive
    ADD COLUMN actor TEXT NOT NULL DEFAULT 'unattributed:pre-M3-audit',
    ADD COLUMN reason TEXT NOT NULL DEFAULT 'original archive, recorded before K5 was enforced here',
    ADD COLUMN supersedes_archive_id UUID NULL;

ALTER TABLE ts.raw_curve_archive
    ALTER COLUMN actor DROP DEFAULT,
    ALTER COLUMN reason DROP DEFAULT;

ALTER TABLE ts.raw_curve_archive
    ADD CONSTRAINT ck_raw_curve_archive_actor CHECK (length(btrim(actor)) > 0),
    ADD CONSTRAINT ck_raw_curve_archive_reason CHECK (length(btrim(reason)) > 0),
    -- A row that corrects itself is a loop an auditor cannot walk, and it is the shape a copy-paste
    -- of the archive id produces.
    ADD CONSTRAINT ck_raw_curve_archive_supersedes_other
        CHECK (supersedes_archive_id IS DISTINCT FROM archive_id),
    ADD CONSTRAINT fk_raw_curve_archive_supersedes
        FOREIGN KEY (supersedes_archive_id) REFERENCES ts.raw_curve_archive (archive_id);

-- One correction per corrected record. Two rows both claiming to replace the same evidence give an
-- auditor two answers and no way to choose, which is worse than the missing link this replaces.
CREATE UNIQUE INDEX uq_raw_curve_archive_supersedes
    ON ts.raw_curve_archive (supersedes_archive_id)
    WHERE supersedes_archive_id IS NOT NULL;

COMMENT ON COLUMN ts.raw_curve_archive.actor IS
    'Who caused this archive row. A person or a named automated caller, never a bare service account.';
COMMENT ON COLUMN ts.raw_curve_archive.reason IS
    'Why these bytes were archived. For a correction, what was wrong with the row it supersedes.';
COMMENT ON COLUMN ts.raw_curve_archive.supersedes_archive_id IS
    'The archive row this one corrects. NULL for an original; the object it points at is never deleted.';
