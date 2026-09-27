IF SCHEMA_ID('masterdata') IS NULL EXEC('CREATE SCHEMA masterdata');

-- Mã chuẩn của MES theo site.
IF OBJECT_ID('masterdata.Items', 'U') IS NULL
CREATE TABLE masterdata.Items (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Kind varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CanonicalId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Name nvarchar(200) NOT NULL,
    BaseUom nvarchar(20) COLLATE Latin1_General_100_BIN2 NULL,
    DefinedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DefinedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MasterDataItems PRIMARY KEY (SiteId, Kind, CanonicalId),
    CONSTRAINT CK_MasterDataItems_Kind CHECK (Kind IN ('Material', 'Product', 'Equipment')),
    CONSTRAINT CK_MasterDataItems_Uom CHECK (Kind <> 'Material' OR BaseUom IS NOT NULL)
);

-- Alias: mã ERP (đã chuẩn hoá) → mã chuẩn. Append-only; ánh xạ lại là quyết định riêng, không ghi đè.
IF OBJECT_ID('masterdata.IdentityAliases', 'U') IS NULL
CREATE TABLE masterdata.IdentityAliases (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Kind varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ExternalCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CanonicalId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Reason nvarchar(500) NOT NULL,
    MappedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MappedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_IdentityAliases PRIMARY KEY (SiteId, Kind, ExternalCode),
    CONSTRAINT FK_IdentityAliases_Item FOREIGN KEY (SiteId, Kind, CanonicalId)
        REFERENCES masterdata.Items (SiteId, Kind, CanonicalId)
);

-- Việc đối soát: mở một lần cho mỗi (sai lệch, chứng từ). Id tất định.
IF OBJECT_ID('masterdata.ReconciliationTasks', 'U') IS NULL
CREATE TABLE masterdata.ReconciliationTasks (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TaskId varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IssueKind varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ExternalCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CanonicalId nvarchar(100) COLLATE Latin1_General_100_BIN2 NULL,
    ReceivedUom nvarchar(20) COLLATE Latin1_General_100_BIN2 NULL,
    ExpectedUom nvarchar(20) COLLATE Latin1_General_100_BIN2 NULL,
    SourceDocument nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Detail nvarchar(1000) NOT NULL,
    Status varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Resolution varchar(10) COLLATE Latin1_General_100_BIN2 NULL,
    Note nvarchar(500) NULL,
    OpenedAt datetimeoffset(7) NOT NULL,
    ResolvedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
    ResolvedAt datetimeoffset(7) NULL,
    CONSTRAINT PK_ReconciliationTasks PRIMARY KEY (SiteId, TaskId),
    CONSTRAINT CK_ReconciliationTasks_Kind CHECK (IssueKind IN ('UnknownMaterial', 'UnknownProduct', 'UomMismatch')),
    CONSTRAINT CK_ReconciliationTasks_Status CHECK (
        (Status = 'Open' AND Resolution IS NULL AND ResolvedAt IS NULL) OR
        (Status = 'Resolved' AND Resolution IN ('Mapped', 'Accepted') AND ResolvedAt IS NOT NULL AND ResolvedBy IS NOT NULL))
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ReconciliationTasks_Open'
               AND object_id = OBJECT_ID('masterdata.ReconciliationTasks'))
CREATE INDEX IX_ReconciliationTasks_Open ON masterdata.ReconciliationTasks (SiteId, Status, IssueKind, ExternalCode);

-- Revision master data theo site: tăng mỗi lần đổi, để đánh giá lại lệnh đang chờ đúng một lần mỗi thay đổi.
IF OBJECT_ID('masterdata.Revisions', 'U') IS NULL
CREATE TABLE masterdata.Revisions (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Revision bigint NOT NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MasterDataRevisions PRIMARY KEY (SiteId)
);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON masterdata.Items TO nvm_app;
    DENY UPDATE, DELETE ON masterdata.Items TO nvm_app;
    GRANT SELECT, INSERT ON masterdata.IdentityAliases TO nvm_app;
    DENY UPDATE, DELETE ON masterdata.IdentityAliases TO nvm_app;
    GRANT SELECT, INSERT ON masterdata.ReconciliationTasks TO nvm_app;
    GRANT UPDATE (Status, Resolution, Note, ResolvedBy, ResolvedAt) ON masterdata.ReconciliationTasks TO nvm_app;
    DENY DELETE ON masterdata.ReconciliationTasks TO nvm_app;
    GRANT SELECT, INSERT, UPDATE ON masterdata.Revisions TO nvm_app;
    DENY DELETE ON masterdata.Revisions TO nvm_app;
END;
