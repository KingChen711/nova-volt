-- NovaVolt MOM v2 — khởi tạo SQL Server
--
-- DB MOM của một plant (plant là phạm vi deploy, AGENTS.md K3). Schema của từng lớp do
-- migration của Foundation dựng từ R1; script này chỉ tạo database và login ứng dụng.
--
-- Chạy lại ở MỌI lần `make up` (SQL Server không có cơ chế "chỉ chạy khi volume rỗng"),
-- nên mọi câu lệnh phải IDEMPOTENT. Gọi bởi service mssql-init với sqlcmd -b và
-- -v AppPassword=... (biến thay thế văn bản trước khi thực thi).

IF DB_ID('NovaVolt') IS NULL
BEGIN
    CREATE DATABASE NovaVolt;
    PRINT 'Created database NovaVolt';
END
ELSE
    PRINT 'Database NovaVolt already exists';
GO

-- Ứng dụng không chạy bằng sa: quyền của nó phải giới hạn được bằng DENY, mà DENY không
-- có tác dụng với sysadmin.
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

ALTER ROLE db_datareader ADD MEMBER nvm_app;
ALTER ROLE db_datawriter ADD MEMBER nvm_app;
ALTER ROLE db_ddladmin ADD MEMBER nvm_app;
GO

PRINT 'NovaVolt init completed';
GO
