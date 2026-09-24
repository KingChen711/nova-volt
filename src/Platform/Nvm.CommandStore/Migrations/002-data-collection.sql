IF OBJECT_ID('execution.DataCollection') IS NULL
CREATE TABLE execution.DataCollection (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IdempotencyKey uniqueidentifier NOT NULL,
    SubmissionId varchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OperationRunId nvarchar(100) NOT NULL,
    StepCode varchar(20) NOT NULL,
    EquipmentPath nvarchar(200) NOT NULL,
    SignalCode varchar(50) NOT NULL,
    -- Decimal dạng round-trip: không ép fixed-scale rồi làm tròn số người vận hành đã nhập.
    Value nvarchar(50) NOT NULL,
    UnitOfMeasure varchar(20) NOT NULL,
    ActorId nvarchar(200) NOT NULL,
    OccurredAt datetimeoffset(7) NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL,
    PayloadJson nvarchar(max) NOT NULL,
    CONSTRAINT PK_DataCollection PRIMARY KEY (SiteId, IdempotencyKey),
    CONSTRAINT CK_DataCollection_Json CHECK (ISJSON(PayloadJson) = 1)
);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON execution.DataCollection TO nvm_app;
    DENY UPDATE, DELETE ON execution.DataCollection TO nvm_app;
END;
