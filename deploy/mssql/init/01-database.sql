-- NovaVolt MES — khởi tạo SQL Server
--
-- KHÁC Postgres: script này chạy lại ở MỌI lần `docker compose up`, vì SQL Server
-- không có cơ chế "chỉ chạy khi volume rỗng". Nên mọi câu lệnh phải IDEMPOTENT.
-- Chạy hai lần phải cho kết quả y hệt chạy một lần.
--
-- Được gọi bởi service mssql-init với sqlcmd -b (thoát mã lỗi khi SQL lỗi)
-- và -v AppPassword=... (biến thay thế văn bản trước khi thực thi).

-- ─────────────────────────────────────────────────────────
-- Database
-- ─────────────────────────────────────────────────────────
IF DB_ID('NovaVolt') IS NULL
BEGIN
    CREATE DATABASE NovaVolt;
    PRINT 'Created database NovaVolt';
END
ELSE
    PRINT 'Database NovaVolt already exists';
GO

USE NovaVolt;
GO

-- ─────────────────────────────────────────────────────────
-- Schema es — event store (scope.md §8.1)
--
-- CREATE SCHEMA bắt buộc phải là câu lệnh đầu tiên của batch, nên không đặt
-- thẳng trong IF được. Bọc qua EXEC là cách chuẩn để lách ràng buộc đó.
-- ─────────────────────────────────────────────────────────
IF SCHEMA_ID('es') IS NULL
BEGIN
    EXEC('CREATE SCHEMA es AUTHORIZATION dbo;');
    PRINT 'Created schema es';
END
ELSE
    PRINT 'Schema es already exists';
GO

-- ─────────────────────────────────────────────────────────
-- Login và user cho ứng dụng
--
-- Ứng dụng KHÔNG chạy bằng sa. Ngay cả ở dev, vì K4 (event store append-only)
-- sẽ được ép bằng DENY UPDATE/DELETE trên chính principal này ở M5 — và
-- DENY không có tác dụng với sysadmin.
-- ─────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'nvm_app')
BEGIN
    CREATE LOGIN nvm_app WITH PASSWORD = '$(AppPassword)', CHECK_POLICY = ON;
    PRINT 'Created login nvm_app';
END
ELSE
    PRINT 'Login nvm_app already exists';
GO

USE NovaVolt;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'nvm_app')
BEGIN
    CREATE USER nvm_app FOR LOGIN nvm_app;
    PRINT 'Created user nvm_app';
END
ELSE
    PRINT 'User nvm_app already exists';
GO

-- Quyền tối thiểu cho giai đoạn này. M5 sẽ siết lại: cấp quyền ghi trên es.event
-- nhưng DENY UPDATE, DELETE để ép tính bất biến ở tầng CSDL chứ không tin vào kỷ luật code.
ALTER ROLE db_datareader ADD MEMBER nvm_app;
ALTER ROLE db_datawriter ADD MEMBER nvm_app;
GRANT CREATE TABLE TO nvm_app;
GRANT ALTER, CONTROL ON SCHEMA::es TO nvm_app;
GO

PRINT 'NovaVolt init completed';
GO
