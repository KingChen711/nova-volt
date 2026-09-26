IF SCHEMA_ID('material') IS NULL EXEC('CREATE SCHEMA material');

-- Lot vật liệu mua vào (scope §6.9). Remaining giảm theo mỗi lần tiêu hao trong cùng transaction.
IF OBJECT_ID('material.Lots', 'U') IS NULL
CREATE TABLE material.Lots (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    LotId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MaterialCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Remaining decimal(18,6) NOT NULL,
    UnitOfMeasure nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReceivedAt datetimeoffset(7) NOT NULL,
    ExpiresAt datetimeoffset(7) NULL,
    MaxExposureMinutes int NULL,
    OpenedAt datetimeoffset(7) NULL,
    Quality varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamVersion bigint NOT NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MaterialLots PRIMARY KEY (SiteId, LotId),
    CONSTRAINT CK_MaterialLots_Quality CHECK (Quality IN ('Pending', 'Released', 'Rejected')),
    CONSTRAINT CK_MaterialLots_Remaining CHECK (Remaining >= 0)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MaterialLots_Fifo' AND object_id = OBJECT_ID('material.Lots'))
CREATE INDEX IX_MaterialLots_Fifo ON material.Lots (SiteId, MaterialCode, ReceivedAt) INCLUDE (Quality, Remaining, ExpiresAt);

IF OBJECT_ID('material.Overrides', 'U') IS NULL
CREATE TABLE material.Overrides (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OverrideId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    LotId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RuleCode varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ValidUntil datetimeoffset(7) NOT NULL,
    GrantedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    GrantedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MaterialOverrides PRIMARY KEY (SiteId, OverrideId)
);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON material.Lots TO nvm_app;
    GRANT UPDATE (Remaining, OpenedAt, Quality, StreamVersion, UpdatedAt) ON material.Lots TO nvm_app;
    DENY DELETE ON material.Lots TO nvm_app;
    GRANT SELECT, INSERT ON material.Overrides TO nvm_app;
    DENY UPDATE, DELETE ON material.Overrides TO nvm_app;
END;
