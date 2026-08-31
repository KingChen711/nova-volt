CREATE TABLE ts.raw_curve_archive (
    archive_id       UUID        PRIMARY KEY,
    site_id          TEXT        NOT NULL,
    equipment_id     TEXT        NOT NULL,
    unit_id          TEXT        NULL,
    curve_start_at   TIMESTAMPTZ NOT NULL,
    curve_end_at     TIMESTAMPTZ NOT NULL,
    object_key       TEXT        NOT NULL,
    object_version_id TEXT       NOT NULL,
    sha256           TEXT        NOT NULL,
    byte_size        BIGINT      NOT NULL,
    recorded_at      TIMESTAMPTZ NOT NULL,

    CONSTRAINT ck_raw_curve_archive_site_id
        CHECK (site_id ~ '^[A-Z0-9]+$'),
    CONSTRAINT ck_raw_curve_archive_equipment_site
        CHECK (equipment_id LIKE 'NOVAVOLT/' || site_id || '/%'),
    CONSTRAINT ck_raw_curve_archive_interval
        CHECK (curve_end_at > curve_start_at),
    CONSTRAINT ck_raw_curve_archive_sha256
        CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_raw_curve_archive_byte_size
        CHECK (byte_size > 0),
    CONSTRAINT uq_raw_curve_archive_identity
        UNIQUE NULLS NOT DISTINCT (
            site_id,
            equipment_id,
            unit_id,
            curve_start_at,
            curve_end_at,
            sha256)
);

CREATE INDEX ix_raw_curve_archive_site_equipment_start
    ON ts.raw_curve_archive (site_id, equipment_id, curve_start_at DESC);

-- Metadata is part of the legal record too. A correction is another archive row with another digest;
-- changing or deleting what an auditor already saw would break the chain even if the object survived.
CREATE FUNCTION ts.reject_raw_curve_archive_mutation()
RETURNS TRIGGER
LANGUAGE plpgsql
AS '
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = ''P1201'',
        MESSAGE = ''ts.raw_curve_archive is append-only; record a new raw curve instead'';
END
';

CREATE TRIGGER raw_curve_archive_append_only
BEFORE UPDATE OR DELETE ON ts.raw_curve_archive
FOR EACH ROW
EXECUTE FUNCTION ts.reject_raw_curve_archive_mutation();

COMMENT ON TABLE ts.raw_curve_archive IS
    'Append-only index of exact formation CSV object versions retained in MinIO';
COMMENT ON COLUMN ts.raw_curve_archive.object_version_id IS
    'Exact S3 version; an unversioned delete may add a delete marker without deleting this evidence';
