-- NCR (non-conformance report). Mở từ fact phát hiện sai lệch; một fact nguồn mở đúng một NCR.
IF OBJECT_ID('quality.NonConformance', 'U') IS NULL
CREATE TABLE quality.NonConformance (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    NcrId varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReasonCode varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Description nvarchar(1000) NOT NULL,
    Source varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SourceEventId uniqueidentifier NOT NULL,
    Status varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamVersion bigint NOT NULL,
    RaisedAt datetimeoffset(7) NOT NULL,
    RaisedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_QualityNonConformance PRIMARY KEY (SiteId, NcrId),
    CONSTRAINT UQ_QualityNonConformance_Source UNIQUE (SiteId, SourceEventId),
    CONSTRAINT CK_QualityNonConformance_Status CHECK (Status IN ('Open','Investigating','Dispositioned','Closed'))
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_QualityNonConformance_Serial'
               AND object_id = OBJECT_ID('quality.NonConformance'))
CREATE INDEX IX_QualityNonConformance_Serial ON quality.NonConformance (SiteId, SerialNumber);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON quality.NonConformance TO nvm_app;
    DENY DELETE ON quality.NonConformance TO nvm_app;
END;
