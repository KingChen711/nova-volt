-- Back to the id-only link of migration 009. Rolling this back re-opens the cross-site hole, which
-- is why it is spelled out here rather than left for a reader to notice.
ALTER TABLE ts.raw_curve_archive
    DROP CONSTRAINT fk_raw_curve_archive_supersedes;

ALTER TABLE ts.raw_curve_archive
    ADD CONSTRAINT fk_raw_curve_archive_supersedes
        FOREIGN KEY (supersedes_archive_id) REFERENCES ts.raw_curve_archive (archive_id);

ALTER TABLE ts.raw_curve_archive
    DROP CONSTRAINT uq_raw_curve_archive_site_scoped_id;
