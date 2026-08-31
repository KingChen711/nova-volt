-- K3 for the correction trail. Migration 009 linked a correction to the row it replaces with
--
--     FOREIGN KEY (supersedes_archive_id) REFERENCES ts.raw_curve_archive (archive_id)
--
-- which constrains the id and says nothing about the plant. A row at NV1 could therefore declare
-- itself the correction of a row at DE1, and both K3 (every query and every row is scoped to one
-- site) and K5 (a correction names what it replaces) would be reported as satisfied while the
-- evidence chain crossed a boundary that is supposed to be a security boundary.
--
-- Found by an independent audit on 2026-09-01, in the same migration that had just been written to
-- close the K5 half of this table. Closing half of a rule and reporting the rule closed is the
-- failure mode this project keeps finding, so the constraint is stated in the schema rather than
-- left to the caller: RawCurveArchiveStore passes the id straight through from whoever called it.
--
-- A composite foreign key needs a matching unique key on the referenced side. archive_id is already
-- the primary key, so (site_id, archive_id) is unique by construction and this index costs one more
-- btree to say so in a form the planner and the FK machinery can both use.
ALTER TABLE ts.raw_curve_archive
    ADD CONSTRAINT uq_raw_curve_archive_site_scoped_id UNIQUE (site_id, archive_id);

ALTER TABLE ts.raw_curve_archive
    DROP CONSTRAINT fk_raw_curve_archive_supersedes;

ALTER TABLE ts.raw_curve_archive
    ADD CONSTRAINT fk_raw_curve_archive_supersedes
        FOREIGN KEY (site_id, supersedes_archive_id)
        REFERENCES ts.raw_curve_archive (site_id, archive_id);

COMMENT ON CONSTRAINT fk_raw_curve_archive_supersedes ON ts.raw_curve_archive IS
    'A correction stays inside its own plant: the site_id travels with the link, so NV1 cannot claim '
    'to correct DE1 (K3, K5).';
