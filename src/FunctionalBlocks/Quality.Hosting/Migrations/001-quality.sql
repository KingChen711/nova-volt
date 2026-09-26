IF SCHEMA_ID('quality') IS NULL EXEC('CREATE SCHEMA quality');

-- Facet QualityState của ProductionUnit. Quality là chủ duy nhất; unit chưa có dòng = Pending.
IF OBJECT_ID('quality.UnitQuality', 'U') IS NULL
CREATE TABLE quality.UnitQuality (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    QualityState varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReasonCode varchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    StreamVersion bigint NOT NULL,
    LastEventId uniqueidentifier NOT NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_QualityUnitQuality PRIMARY KEY (SiteId, SerialNumber),
    CONSTRAINT CK_QualityUnitQuality_State CHECK (QualityState IN ('Pending','Released','Held','Rework','Scrapped')),
    CONSTRAINT CK_QualityUnitQuality_Version CHECK (StreamVersion >= 0)
);

-- Trước facet M5, Traceability tự giữ cột QualityState. Chuyển giá trị khác Pending sang chủ mới
-- (Held do serial trùng giữ lý do DUPLICATE_SERIAL), rồi bỏ cột cũ để không còn hai nguồn sự thật.
-- StreamVersion = 0: các dòng này chưa có event Quality nào; event đầu tiên sau này mở stream.
IF COL_LENGTH('traceability.SerialReservations', 'QualityState') IS NOT NULL
BEGIN
    EXEC('
    INSERT INTO quality.UnitQuality
        (SiteId, SerialNumber, QualityState, ReasonCode, StreamVersion, LastEventId, UpdatedAt)
    SELECT r.SiteId, r.SerialNumber, r.QualityState,
        CASE WHEN EXISTS (SELECT 1 FROM traceability.DuplicateSerialIncidents d
                          WHERE d.SiteId = r.SiteId AND d.SerialNumber = r.SerialNumber)
             THEN ''DUPLICATE_SERIAL'' ELSE NULL END,
        0, r.SerializedEventId, SYSDATETIMEOFFSET()
    FROM traceability.SerialReservations r
    WHERE r.QualityState <> ''Pending''
      AND NOT EXISTS (SELECT 1 FROM quality.UnitQuality q
                      WHERE q.SiteId = r.SiteId AND q.SerialNumber = r.SerialNumber);');
    IF OBJECT_ID('traceability.CK_Traceability_Quality', 'C') IS NOT NULL
        ALTER TABLE traceability.SerialReservations DROP CONSTRAINT CK_Traceability_Quality;
    IF OBJECT_ID('traceability.DF_Traceability_Quality', 'D') IS NOT NULL
        ALTER TABLE traceability.SerialReservations DROP CONSTRAINT DF_Traceability_Quality;
    ALTER TABLE traceability.SerialReservations DROP COLUMN QualityState;
END;

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON quality.UnitQuality TO nvm_app;
    DENY DELETE ON quality.UnitQuality TO nvm_app;
END;
