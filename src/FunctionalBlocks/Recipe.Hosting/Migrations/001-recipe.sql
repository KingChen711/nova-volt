IF SCHEMA_ID('recipe') IS NULL EXEC('CREATE SCHEMA recipe');

IF OBJECT_ID('recipe.RecipeVersions', 'U') IS NULL
CREATE TABLE recipe.RecipeVersions (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RecipeId varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version int NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StepCode nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EquipmentClass nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ParametersJson nvarchar(max) NOT NULL,
    Status varchar(12) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EffectiveFrom datetimeoffset(7) NULL,
    EffectiveTo datetimeoffset(7) NULL,
    ContentSha256 char(64) COLLATE Latin1_General_100_BIN2 NULL,
    AuthoredBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AuthoredAt datetimeoffset(7) NOT NULL,
    ApprovedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
    ApprovedAt datetimeoffset(7) NULL,
    CONSTRAINT PK_RecipeVersions PRIMARY KEY (SiteId, RecipeId, Version),
    CONSTRAINT CK_RecipeVersions_Status CHECK (Status IN ('Draft', 'Active', 'Superseded')),
    CONSTRAINT CK_RecipeVersions_Parameters CHECK (ISJSON(ParametersJson) = 1),
    CONSTRAINT CK_RecipeVersions_Effective CHECK (
        (Status = 'Draft' AND EffectiveFrom IS NULL AND EffectiveTo IS NULL) OR
        (Status = 'Active' AND EffectiveFrom IS NOT NULL AND EffectiveTo IS NULL AND ContentSha256 IS NOT NULL) OR
        (Status = 'Superseded' AND EffectiveFrom IS NOT NULL AND EffectiveTo > EffectiveFrom AND ContentSha256 IS NOT NULL))
);

-- scope §6.8: chỉ một version Active cho mỗi (EquipmentClass, ProductCode, StepCode). DB chặn, không phải if.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RecipeVersions_OneActive'
               AND object_id = OBJECT_ID('recipe.RecipeVersions'))
CREATE UNIQUE INDEX UX_RecipeVersions_OneActive ON recipe.RecipeVersions (SiteId, EquipmentClass, ProductCode, StepCode)
    WHERE Status = 'Active';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RecipeVersions_Effective'
               AND object_id = OBJECT_ID('recipe.RecipeVersions'))
CREATE INDEX IX_RecipeVersions_Effective ON recipe.RecipeVersions (SiteId, EquipmentClass, ProductCode, StepCode, EffectiveFrom)
    INCLUDE (EffectiveTo, Status);

-- Recipe đã dùng trên từng máy: append-only, trả lời "lúc T máy X chạy recipe nào".
IF OBJECT_ID('recipe.Applications', 'U') IS NULL
CREATE TABLE recipe.Applications (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EventId uniqueidentifier NOT NULL,
    EquipmentPath nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AppliedAt datetimeoffset(7) NOT NULL,
    RecipeId varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version int NOT NULL,
    ContentSha256 char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OperationRunId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    LotId nvarchar(100) COLLATE Latin1_General_100_BIN2 NULL,
    CONSTRAINT PK_RecipeApplications PRIMARY KEY (SiteId, EventId)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RecipeApplications_Equipment'
               AND object_id = OBJECT_ID('recipe.Applications'))
CREATE INDEX IX_RecipeApplications_Equipment ON recipe.Applications (SiteId, EquipmentPath, AppliedAt)
    INCLUDE (RecipeId, Version, ContentSha256, OperationRunId, LotId);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON recipe.RecipeVersions TO nvm_app;
    GRANT UPDATE (Status, EffectiveFrom, EffectiveTo, ContentSha256, ApprovedBy, ApprovedAt) ON recipe.RecipeVersions TO nvm_app;
    DENY DELETE ON recipe.RecipeVersions TO nvm_app;
    GRANT SELECT, INSERT ON recipe.Applications TO nvm_app;
    DENY UPDATE, DELETE ON recipe.Applications TO nvm_app;
END;
