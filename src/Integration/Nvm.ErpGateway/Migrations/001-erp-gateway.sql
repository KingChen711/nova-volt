IF SCHEMA_ID('erp') IS NULL EXEC('CREATE SCHEMA erp');

-- Checkpoint backflush theo site. Lô đang gửi được chốt (Pending*) trước khi POST: gửi lại đúng lô đó tới khi ERP
-- xác nhận, để mất phản hồi không làm ERP nhận hai lô chồng nhau.
IF OBJECT_ID('erp.BackflushCheckpoints', 'U') IS NULL
CREATE TABLE erp.BackflushCheckpoints (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    LastGlobalSequence bigint NOT NULL,
    PendingFrom bigint NULL,
    PendingTo bigint NULL,
    PendingBatchId uniqueidentifier NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_BackflushCheckpoints PRIMARY KEY (SiteId),
    CONSTRAINT CK_BackflushCheckpoints_Pending CHECK (
        (PendingFrom IS NULL AND PendingTo IS NULL AND PendingBatchId IS NULL) OR
        (PendingFrom > LastGlobalSequence AND PendingTo >= PendingFrom AND PendingBatchId IS NOT NULL))
);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON erp.BackflushCheckpoints TO nvm_app;
    DENY DELETE ON erp.BackflushCheckpoints TO nvm_app;
END;
