-- Work order nhận từ ERP (M11). Giữ nguyên mã và đơn vị ERP gửi; mã chuẩn điền khi đã giải được.
IF OBJECT_ID('execution.WorkOrders', 'U') IS NULL
CREATE TABLE execution.WorkOrders (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    WorkOrderId nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ScheduleId nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ExternalProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NULL,
    EarliestStart datetimeoffset(7) NULL,
    MaterialsJson nvarchar(max) NOT NULL,
    Status varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OpenTaskIdsJson nvarchar(max) NOT NULL,
    StreamVersion bigint NOT NULL,
    ReceivedAt datetimeoffset(7) NOT NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_WorkOrders PRIMARY KEY (SiteId, WorkOrderId),
    CONSTRAINT CK_WorkOrders_Status CHECK (Status IN ('Released', 'PendingMasterData')),
    CONSTRAINT CK_WorkOrders_Released CHECK (Status <> 'Released' OR ProductCode IS NOT NULL),
    CONSTRAINT CK_WorkOrders_Json CHECK (ISJSON(MaterialsJson) = 1 AND ISJSON(OpenTaskIdsJson) = 1)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkOrders_Pending' AND object_id = OBJECT_ID('execution.WorkOrders'))
CREATE INDEX IX_WorkOrders_Pending ON execution.WorkOrders (SiteId, Status) INCLUDE (UpdatedAt);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON execution.WorkOrders TO nvm_app;
    DENY DELETE ON execution.WorkOrders TO nvm_app;
END;
