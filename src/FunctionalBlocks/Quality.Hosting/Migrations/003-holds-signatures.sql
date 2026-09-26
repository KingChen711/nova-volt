-- Hold trên lot/cuộn/unit, job cascade có checkpoint, thành viên hold, chuỗi chữ ký điện tử (M9).
IF OBJECT_ID('quality.Holds', 'U') IS NULL
CREATE TABLE quality.Holds (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    HoldId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TargetKind varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TargetId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SpanFromMeter decimal(12,3) NULL,
    SpanToMeter decimal(12,3) NULL,
    ReasonCode varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    NcrId varchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    HeldBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Status varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamVersion bigint NOT NULL,
    PlacedAt datetimeoffset(7) NOT NULL,
    ReleasedAt datetimeoffset(7) NULL,
    CONSTRAINT PK_QualityHolds PRIMARY KEY (SiteId, HoldId),
    CONSTRAINT CK_QualityHolds_Status CHECK (Status IN ('Active', 'Released')),
    CONSTRAINT CK_QualityHolds_Kind CHECK (TargetKind IN ('Lot', 'Roll', 'Unit'))
);

IF OBJECT_ID('quality.CascadeJobs', 'U') IS NULL
CREATE TABLE quality.CascadeJobs (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    JobId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    HoldId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Status varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TotalUnits int NOT NULL,
    NextChunk int NOT NULL,
    ChunkSize int NOT NULL,
    PlanRounds int NOT NULL,
    StreamVersion bigint NOT NULL CONSTRAINT DF_QualityCascadeJobs_StreamVersion DEFAULT 0,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_QualityCascadeJobs PRIMARY KEY (SiteId, JobId),
    CONSTRAINT CK_QualityCascadeJobs_Status CHECK (Status IN ('Running', 'Completed'))
);

-- Danh sách mục tiêu chốt một lần (thứ tự Ordinal cố định) để resume từ checkpoint cho đúng chunk.
IF OBJECT_ID('quality.CascadeTargets', 'U') IS NULL
CREATE TABLE quality.CascadeTargets (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    JobId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Ordinal int NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_QualityCascadeTargets PRIMARY KEY (SiteId, JobId, Ordinal),
    CONSTRAINT UQ_QualityCascadeTargets_Serial UNIQUE (SiteId, JobId, SerialNumber)
);

-- Unit bị giữ bởi một hold. Hold Released thì thành viên hết hiệu lực; dòng không bị xoá.
IF OBJECT_ID('quality.HoldMembers', 'U') IS NULL
CREATE TABLE quality.HoldMembers (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    HoldId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    HeldAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_QualityHoldMembers PRIMARY KEY (SiteId, SerialNumber, HoldId)
);

IF OBJECT_ID('quality.Signatures', 'U') IS NULL
CREATE TABLE quality.Signatures (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Sequence bigint NOT NULL,
    SignatureId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SubjectType varchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SubjectId nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SignerId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SignerRole varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Meaning varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ContentSha256 char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SignedAt datetimeoffset(7) NOT NULL,
    PreviousHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Hash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_QualitySignatures PRIMARY KEY (SiteId, Sequence),
    CONSTRAINT UQ_QualitySignatures_Id UNIQUE (SiteId, SignatureId),
    CONSTRAINT UQ_QualitySignatures_Previous UNIQUE (SiteId, PreviousHash)
);

IF OBJECT_ID('quality.SpcSamples', 'U') IS NULL
CREATE TABLE quality.SpcSamples (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Characteristic varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SubgroupId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TakenAt datetimeoffset(7) NOT NULL,
    ValuesJson nvarchar(max) NOT NULL,
    RecordedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_QualitySpcSamples PRIMARY KEY (SiteId, Characteristic, SubgroupId),
    CONSTRAINT CK_QualitySpcSamples_Values CHECK (ISJSON(ValuesJson) = 1)
);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON quality.Holds TO nvm_app;
    GRANT UPDATE (Status, StreamVersion, ReleasedAt) ON quality.Holds TO nvm_app;
    DENY DELETE ON quality.Holds TO nvm_app;
    GRANT SELECT, INSERT, UPDATE ON quality.CascadeJobs TO nvm_app;
    GRANT SELECT, INSERT ON quality.CascadeTargets TO nvm_app;
    GRANT SELECT, INSERT ON quality.HoldMembers TO nvm_app;
    GRANT SELECT, INSERT ON quality.Signatures TO nvm_app;
    GRANT SELECT, INSERT ON quality.SpcSamples TO nvm_app;
    DENY UPDATE, DELETE ON quality.Signatures TO nvm_app;
    DENY UPDATE, DELETE ON quality.HoldMembers TO nvm_app;
    DENY DELETE ON quality.CascadeJobs TO nvm_app;
END;
