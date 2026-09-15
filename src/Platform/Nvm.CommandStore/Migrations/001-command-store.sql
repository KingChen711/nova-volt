IF SCHEMA_ID('command_store') IS NULL EXEC('CREATE SCHEMA command_store');
IF SCHEMA_ID('execution') IS NULL EXEC('CREATE SCHEMA execution');

IF OBJECT_ID('command_store.CommandOutcomes') IS NULL
CREATE TABLE command_store.CommandOutcomes (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IdempotencyKey uniqueidentifier NOT NULL,
    ActorId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CommandType nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PayloadHash binary(32) NOT NULL,
    ResultType nvarchar(500) NOT NULL,
    OutcomeJson nvarchar(max) NULL,
    HandledAt datetimeoffset(7) NULL,
    CONSTRAINT PK_CommandOutcomes PRIMARY KEY (SiteId, IdempotencyKey),
    CONSTRAINT CK_CommandOutcomes_Completion CHECK (
        (OutcomeJson IS NULL AND HandledAt IS NULL) OR (OutcomeJson IS NOT NULL AND HandledAt IS NOT NULL))
);

IF OBJECT_ID('execution.UnitContext') IS NULL
CREATE TABLE execution.UnitContext (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ContextJson nvarchar(max) NOT NULL,
    CONSTRAINT PK_UnitContext PRIMARY KEY (SiteId, SerialNumber),
    CONSTRAINT CK_UnitContext_Json CHECK (ISJSON(ContextJson) = 1)
);

-- Runtime chỉ đọc context; fixture/migration dùng principal riêng.
IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON command_store.CommandOutcomes TO nvm_app;
    DENY DELETE ON command_store.CommandOutcomes TO nvm_app;
    GRANT SELECT ON execution.UnitContext TO nvm_app;
    DENY INSERT, UPDATE, DELETE ON execution.UnitContext TO nvm_app;
END;
