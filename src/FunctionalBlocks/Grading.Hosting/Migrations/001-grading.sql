IF SCHEMA_ID('grading') IS NULL EXEC('CREATE SCHEMA grading');

-- Rule set là dữ liệu có version. Bản đã duyệt bất biến; chỉ trạng thái Draft → Approved được đổi.
IF OBJECT_ID('grading.RuleSets', 'U') IS NULL
CREATE TABLE grading.RuleSets (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RuleSetId varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version int NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EffectiveFrom datetimeoffset(7) NOT NULL,
    DefinitionJson nvarchar(max) NOT NULL,
    Status varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AuthoredBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AuthoredAt datetimeoffset(7) NOT NULL,
    ApprovedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
    ApprovedAt datetimeoffset(7) NULL,
    ContentSha256 char(64) COLLATE Latin1_General_100_BIN2 NULL,
    CONSTRAINT PK_GradingRuleSets PRIMARY KEY (SiteId, RuleSetId, Version),
    CONSTRAINT CK_GradingRuleSets_Status CHECK (Status IN ('Draft', 'Approved')),
    CONSTRAINT CK_GradingRuleSets_Definition CHECK (ISJSON(DefinitionJson) = 1),
    CONSTRAINT CK_GradingRuleSets_Approval CHECK (
        (Status = 'Approved' AND ApprovedBy IS NOT NULL AND ApprovedAt IS NOT NULL AND ContentSha256 IS NOT NULL) OR
        (Status = 'Draft' AND ApprovedBy IS NULL AND ApprovedAt IS NULL AND ContentSha256 IS NULL)),
    CONSTRAINT CK_GradingRuleSets_SoD CHECK (ApprovedBy IS NULL OR ApprovedBy <> AuthoredBy)
);

-- Một sản phẩm không có hai rule set duyệt cùng thời điểm hiệu lực: DB chặn, không trông vào code.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_GradingRuleSets_Effective'
               AND object_id = OBJECT_ID('grading.RuleSets'))
CREATE UNIQUE INDEX UX_GradingRuleSets_Effective ON grading.RuleSets (SiteId, ProductCode, EffectiveFrom)
    WHERE Status = 'Approved';

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON grading.RuleSets TO nvm_app;
    GRANT UPDATE (Status, ApprovedBy, ApprovedAt, ContentSha256) ON grading.RuleSets TO nvm_app;
    DENY DELETE ON grading.RuleSets TO nvm_app;
END;
