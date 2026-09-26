IF SCHEMA_ID('execution') IS NULL EXEC('CREATE SCHEMA execution');

-- Một quá trình formation + aging cho mỗi cell (PK): một cell chỉ có một saga đang chạy.
IF OBJECT_ID('execution.FormationAging', 'U') IS NULL
CREATE TABLE execution.FormationAging (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    State varchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version bigint NOT NULL,
    TrayId nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Channel int NOT NULL,
    EquipmentPath nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    FormationDueAt datetimeoffset(7) NOT NULL,
    CapacityAh decimal(9,4) NULL,
    Ocv1Millivolt decimal(9,3) NULL,
    Ocv2Millivolt decimal(9,3) NULL,
    RackId nvarchar(20) COLLATE Latin1_General_100_BIN2 NULL,
    Level int NULL,
    AgingChannel int NULL,
    AgingDueAt datetimeoffset(7) NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_FormationAging PRIMARY KEY (SiteId, SerialNumber),
    CONSTRAINT CK_FormationAging_State CHECK (State IN
        ('Forming','Degassing','Aging','AwaitingMeasurement','Completed','Faulted','Quarantined')),
    CONSTRAINT CK_FormationAging_Version CHECK (Version > 0)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FormationAging_Rack'
               AND object_id = OBJECT_ID('execution.FormationAging'))
CREATE INDEX IX_FormationAging_Rack ON execution.FormationAging (SiteId, RackId, Level)
    INCLUDE (SerialNumber, AgingChannel, AgingDueAt) WHERE State = 'Aging';

-- Timeout bền: hạn chờ nhiều ngày sống trong DB, không trong RAM hay broker.
IF OBJECT_ID('execution.ProcessTimeouts', 'U') IS NULL
CREATE TABLE execution.ProcessTimeouts (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Kind varchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DueAt datetimeoffset(7) NOT NULL,
    CompletedAt datetimeoffset(7) NULL,
    CONSTRAINT PK_ProcessTimeouts PRIMARY KEY (SiteId, SerialNumber, Kind),
    CONSTRAINT FK_ProcessTimeouts_Process FOREIGN KEY (SiteId, SerialNumber)
        REFERENCES execution.FormationAging (SiteId, SerialNumber)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ProcessTimeouts_Due'
               AND object_id = OBJECT_ID('execution.ProcessTimeouts'))
CREATE INDEX IX_ProcessTimeouts_Due ON execution.ProcessTimeouts (DueAt)
    INCLUDE (SiteId, SerialNumber, Kind) WHERE CompletedAt IS NULL;

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON execution.FormationAging TO nvm_app;
    DENY DELETE ON execution.FormationAging TO nvm_app;
    GRANT SELECT, INSERT, UPDATE ON execution.ProcessTimeouts TO nvm_app;
    DENY DELETE ON execution.ProcessTimeouts TO nvm_app;
END;
