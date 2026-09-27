IF SCHEMA_ID('passport') IS NULL EXEC('CREATE SCHEMA passport');

IF OBJECT_ID('passport.BatteryModels', 'U') IS NULL
CREATE TABLE passport.BatteryModels (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Gtin char(14) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Manufacturer nvarchar(200) NOT NULL,
    Chemistry nvarchar(50) NOT NULL,
    NominalEnergyKwh decimal(9,3) NOT NULL,
    ExpectedLifetimeCycles int NOT NULL,
    RequiresPassport bit NOT NULL,
    MaterialCompositionJson nvarchar(max) NOT NULL,
    DismantlingUri nvarchar(500) NOT NULL,
    SafetyUri nvarchar(500) NOT NULL,
    DefinedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DefinedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_BatteryModels PRIMARY KEY (SiteId, Gtin),
    CONSTRAINT UX_BatteryModels_Product UNIQUE (SiteId, ProductCode),
    CONSTRAINT CK_BatteryModels_Composition CHECK (ISJSON(MaterialCompositionJson) = 1)
);

-- Carbon footprint theo (site = nhà máy, sản phẩm, năm), có version; ghi cho cả sản phẩm không cần passport.
IF OBJECT_ID('passport.CarbonFootprints', 'U') IS NULL
CREATE TABLE passport.CarbonFootprints (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Year int NOT NULL,
    Version int NOT NULL,
    KgCo2ePerKwh decimal(12,4) NOT NULL,
    RecycledContentJson nvarchar(max) NOT NULL,
    VerifiedBy nvarchar(200) NOT NULL,
    RecordedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_CarbonFootprints PRIMARY KEY (SiteId, ProductCode, Year, Version),
    CONSTRAINT CK_CarbonFootprints_Recycled CHECK (ISJSON(RecycledContentJson) = 1)
);

-- Mỗi version passport một dòng. Nội dung và hash không bao giờ đổi; bản đã Published không đổi gì nữa.
IF OBJECT_ID('passport.Passports', 'U') IS NULL
CREATE TABLE passport.Passports (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version int NOT NULL,
    Gtin char(14) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PreviousVersion int NULL,
    ContentJson nvarchar(max) NOT NULL,
    ContentSha256 char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Status varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PreparedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PreparedAt datetimeoffset(7) NOT NULL,
    SignatureIdsJson nvarchar(max) NULL,
    PublishedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
    PublishedAt datetimeoffset(7) NULL,
    CONSTRAINT PK_Passports PRIMARY KEY (SiteId, SerialNumber, Version),
    CONSTRAINT FK_Passports_Previous FOREIGN KEY (SiteId, SerialNumber, PreviousVersion)
        REFERENCES passport.Passports (SiteId, SerialNumber, Version),
    CONSTRAINT CK_Passports_Status CHECK (
        (Status = 'Draft' AND PublishedAt IS NULL AND SignatureIdsJson IS NULL) OR
        (Status = 'Published' AND PublishedAt IS NOT NULL AND ISJSON(SignatureIdsJson) = 1)),
    CONSTRAINT CK_Passports_Content CHECK (ISJSON(ContentJson) = 1)
);
EXEC('
CREATE OR ALTER TRIGGER passport.TR_Passports_Immutable ON passport.Passports AFTER UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    -- Passport là hồ sơ pháp lý: nội dung không đổi; bản đã công bố không đổi, không xoá, kể cả với tài khoản có quyền.
    IF EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i
                   ON i.SiteId = d.SiteId AND i.SerialNumber = d.SerialNumber AND i.Version = d.Version
               WHERE i.SiteId IS NULL OR d.Status = ''Published''
                  OR i.ContentJson <> d.ContentJson OR i.ContentSha256 <> d.ContentSha256 OR i.Gtin <> d.Gtin
                  OR ISNULL(i.PreviousVersion, -1) <> ISNULL(d.PreviousVersion, -1))
    BEGIN
        THROW 51000, ''Passport content is immutable; publish a new version instead.'', 1;
    END
END');

-- Mọi lượt đọc của regulator. Append-only.
IF OBJECT_ID('passport.ReadAudit', 'U') IS NULL
CREATE TABLE passport.ReadAudit (
    AuditId bigint IDENTITY(1,1) NOT NULL,
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version int NOT NULL,
    Audience varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Subject nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    FieldCount int NOT NULL,
    ReadAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_PassportReadAudit PRIMARY KEY (AuditId)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PassportReadAudit_Serial' AND object_id = OBJECT_ID('passport.ReadAudit'))
CREATE INDEX IX_PassportReadAudit_Serial ON passport.ReadAudit (SiteId, SerialNumber, ReadAt);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON passport.BatteryModels TO nvm_app;
    DENY UPDATE, DELETE ON passport.BatteryModels TO nvm_app;
    GRANT SELECT, INSERT ON passport.CarbonFootprints TO nvm_app;
    DENY UPDATE, DELETE ON passport.CarbonFootprints TO nvm_app;
    GRANT SELECT, INSERT ON passport.Passports TO nvm_app;
    GRANT UPDATE (Status, SignatureIdsJson, PublishedBy, PublishedAt) ON passport.Passports TO nvm_app;
    DENY DELETE ON passport.Passports TO nvm_app;
    GRANT SELECT, INSERT ON passport.ReadAudit TO nvm_app;
    DENY UPDATE, DELETE ON passport.ReadAudit TO nvm_app;
END;
