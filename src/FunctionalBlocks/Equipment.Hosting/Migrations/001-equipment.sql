IF SCHEMA_ID('equipment') IS NULL EXEC('CREATE SCHEMA equipment');

-- Trạng thái hiện tại của từng máy; lịch sử nằm trong stream equipment:{path} và bảng Downtimes.
IF OBJECT_ID('equipment.Equipment', 'U') IS NULL
CREATE TABLE equipment.Equipment (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EquipmentPath nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EquipmentClass nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    State varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StateSince datetimeoffset(7) NOT NULL,
    ReasonCode varchar(40) COLLATE Latin1_General_100_BIN2 NULL,
    StreamVersion bigint NOT NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_Equipment PRIMARY KEY (SiteId, EquipmentPath),
    CONSTRAINT CK_Equipment_State CHECK (
        (State = 'Running' AND ReasonCode IS NULL) OR (State = 'Stopped' AND ReasonCode IS NOT NULL))
);

-- Cây lý do dừng theo site. Chỉ lá được gán cho một lần dừng; Category của lá quyết định Planned/Unplanned.
IF OBJECT_ID('equipment.DowntimeReasons', 'U') IS NULL
CREATE TABLE equipment.DowntimeReasons (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Code varchar(40) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ParentCode varchar(40) COLLATE Latin1_General_100_BIN2 NULL,
    Category varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IsLeaf bit NOT NULL,
    Name nvarchar(100) NOT NULL,
    CONSTRAINT PK_DowntimeReasons PRIMARY KEY (SiteId, Code),
    CONSTRAINT FK_DowntimeReasons_Parent FOREIGN KEY (SiteId, ParentCode) REFERENCES equipment.DowntimeReasons (SiteId, Code),
    CONSTRAINT CK_DowntimeReasons_Category CHECK (Category IN ('Planned', 'Unplanned'))
);

-- Seed cây lý do cho hai site. Chạy lại không nhân dòng.
DECLARE @reasons TABLE (Code varchar(40) COLLATE Latin1_General_100_BIN2, ParentCode varchar(40) COLLATE Latin1_General_100_BIN2 NULL,
    Category varchar(10) COLLATE Latin1_General_100_BIN2, IsLeaf bit, Name nvarchar(100), Ord int);
INSERT INTO @reasons VALUES
    ('PLANNED', NULL, 'Planned', 0, N'Dừng có kế hoạch', 1),
    ('CHANGEOVER', 'PLANNED', 'Planned', 1, N'Đổi sản phẩm', 2),
    ('PLANNED_MAINTENANCE', 'PLANNED', 'Planned', 1, N'Bảo trì theo kế hoạch', 3),
    ('NO_ORDER', 'PLANNED', 'Planned', 1, N'Không có lệnh sản xuất', 4),
    ('BREAK', 'PLANNED', 'Planned', 1, N'Nghỉ ca', 5),
    ('UNPLANNED', NULL, 'Unplanned', 0, N'Dừng không kế hoạch', 6),
    ('MECHANICAL', 'UNPLANNED', 'Unplanned', 0, N'Cơ khí', 7),
    ('MECH_JAM', 'MECHANICAL', 'Unplanned', 1, N'Kẹt vật liệu', 8),
    ('MECH_BREAKDOWN', 'MECHANICAL', 'Unplanned', 1, N'Hỏng cơ khí', 9),
    ('ELECTRICAL', 'UNPLANNED', 'Unplanned', 0, N'Điện', 10),
    ('ELEC_FAULT', 'ELECTRICAL', 'Unplanned', 1, N'Lỗi điện/điều khiển', 11),
    ('MATERIAL', 'UNPLANNED', 'Unplanned', 0, N'Vật liệu', 12),
    ('MATERIAL_SHORTAGE', 'MATERIAL', 'Unplanned', 1, N'Thiếu vật liệu', 13),
    ('QUALITY', 'UNPLANNED', 'Unplanned', 0, N'Chất lượng', 14),
    ('QUALITY_HOLD', 'QUALITY', 'Unplanned', 1, N'Chờ Quality quyết định', 15),
    ('UNASSIGNED', 'UNPLANNED', 'Unplanned', 1, N'Chưa gán lý do', 16);
DECLARE @sites TABLE (SiteId varchar(3) COLLATE Latin1_General_100_BIN2);
INSERT INTO @sites VALUES ('NV1'), ('DE1');
INSERT INTO equipment.DowntimeReasons (SiteId, Code, ParentCode, Category, IsLeaf, Name)
SELECT s.SiteId, r.Code, r.ParentCode, r.Category, r.IsLeaf, r.Name
FROM @sites s CROSS JOIN @reasons r
WHERE NOT EXISTS (SELECT 1 FROM equipment.DowntimeReasons d WHERE d.SiteId = s.SiteId AND d.Code = r.Code)
ORDER BY r.Ord;

-- Lần dừng đã kết thúc, append-only.
IF OBJECT_ID('equipment.Downtimes', 'U') IS NULL
CREATE TABLE equipment.Downtimes (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EventId uniqueidentifier NOT NULL,
    EquipmentPath nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StartedAt datetimeoffset(7) NOT NULL,
    EndedAt datetimeoffset(7) NOT NULL,
    ReasonCode varchar(40) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Category varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_Downtimes PRIMARY KEY (SiteId, EventId),
    CONSTRAINT CK_Downtimes_Range CHECK (EndedAt >= StartedAt),
    CONSTRAINT CK_Downtimes_Category CHECK (Category IN ('Planned', 'Unplanned', 'MicroStop'))
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Downtimes_Equipment' AND object_id = OBJECT_ID('equipment.Downtimes'))
CREATE INDEX IX_Downtimes_Equipment ON equipment.Downtimes (SiteId, EquipmentPath, StartedAt)
    INCLUDE (EndedAt, ReasonCode, Category);

-- Ideal cycle time theo (EquipmentClass, ProductCode), có version (scope §6.10). Append-only.
IF OBJECT_ID('equipment.IdealCycleTimes', 'U') IS NULL
CREATE TABLE equipment.IdealCycleTimes (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EquipmentClass nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version int NOT NULL,
    CycleMilliseconds int NOT NULL,
    EffectiveFrom datetimeoffset(7) NOT NULL,
    SetBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SetAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_IdealCycleTimes PRIMARY KEY (SiteId, EquipmentClass, ProductCode, Version),
    CONSTRAINT CK_IdealCycleTimes_Cycle CHECK (CycleMilliseconds > 0)
);

-- Sản lượng theo khoảng; ideal cycle chốt lúc ghi. Một khoảng của một máy chỉ ghi một lần.
IF OBJECT_ID('equipment.ProductionCounts', 'U') IS NULL
CREATE TABLE equipment.ProductionCounts (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EventId uniqueidentifier NOT NULL,
    EquipmentPath nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    WindowFrom datetimeoffset(7) NOT NULL,
    WindowTo datetimeoffset(7) NOT NULL,
    TotalCount bigint NOT NULL,
    GoodCount bigint NOT NULL,
    IdealCycleMilliseconds int NOT NULL,
    IdealCycleVersion int NOT NULL,
    CONSTRAINT PK_ProductionCounts PRIMARY KEY (SiteId, EventId),
    CONSTRAINT UX_ProductionCounts_Window UNIQUE (SiteId, EquipmentPath, WindowFrom),
    CONSTRAINT CK_ProductionCounts_Values CHECK (WindowTo > WindowFrom AND GoodCount >= 0 AND GoodCount <= TotalCount)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ProductionCounts_WindowTo'
               AND object_id = OBJECT_ID('equipment.ProductionCounts'))
CREATE INDEX IX_ProductionCounts_WindowTo ON equipment.ProductionCounts (SiteId, EquipmentPath, WindowTo)
    INCLUDE (TotalCount, GoodCount, IdealCycleMilliseconds, WindowFrom);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON equipment.Equipment TO nvm_app;
    DENY DELETE ON equipment.Equipment TO nvm_app;
    GRANT SELECT ON equipment.DowntimeReasons TO nvm_app;
    GRANT SELECT, INSERT ON equipment.Downtimes TO nvm_app;
    DENY UPDATE, DELETE ON equipment.Downtimes TO nvm_app;
    GRANT SELECT, INSERT ON equipment.IdealCycleTimes TO nvm_app;
    DENY UPDATE, DELETE ON equipment.IdealCycleTimes TO nvm_app;
    GRANT SELECT, INSERT ON equipment.ProductionCounts TO nvm_app;
    DENY UPDATE, DELETE ON equipment.ProductionCounts TO nvm_app;
END;
