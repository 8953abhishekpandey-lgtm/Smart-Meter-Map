/* =====================================================================================
   06_security.sql : least-privilege application principal.
   The API connects as [smartmeter_app]; it can read/write data and EXECUTE the sm.* procedures.
   All DDL (partition SPLIT/MERGE, staging tables, generation swap) happens inside procedures
   declared WITH EXECUTE AS OWNER, so the login never needs db_owner / db_ddladmin.

   SSMS: enable Query > SQLCMD Mode, then set the password below (or pass -v AppPassword=... to sqlcmd).
   Windows / Entra ID: replace the SQL login with CREATE USER [DOMAIN\SmartMeterSvc] FROM LOGIN ... .
   ===================================================================================== */
:setvar AppLogin "smartmeter_app"
-- The password is intentionally NOT defaulted here (a :setvar in the file would override sqlcmd -v).
-- sqlcmd:  sqlcmd ... -v AppPassword="<from your vault>" -i 06_security.sql
-- SSMS (SQLCMD Mode): uncomment and set the next line for this run only - never commit a real value.
-- :setvar AppPassword "<strong password>"
USE [master];
GO
IF SUSER_ID(N'$(AppLogin)') IS NULL
    CREATE LOGIN [$(AppLogin)] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON, DEFAULT_DATABASE = [SmartMeterDb];
ELSE
    ALTER LOGIN [$(AppLogin)] WITH PASSWORD = N'$(AppPassword)';   -- re-running the script rotates the password
GO
USE [SmartMeterDb];
GO
IF USER_ID(N'$(AppLogin)') IS NULL
    CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)] WITH DEFAULT_SCHEMA = sm;
GO
ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
GRANT EXECUTE ON SCHEMA::sm TO [$(AppLogin)];
-- SqlBulkCopy into per-job staging heaps created by sm.usp_CreateStageTable
GRANT INSERT, SELECT ON SCHEMA::stage TO [$(AppLogin)];
GRANT VIEW DATABASE STATE TO [$(AppLogin)];          -- health checks / diagnostics
GO
-- Hangfire job storage lives in its own schema owned by the app so it can self-install / upgrade.
IF SCHEMA_ID(N'HangFire') IS NULL EXEC (N'CREATE SCHEMA [HangFire] AUTHORIZATION [$(AppLogin)]');
GRANT CREATE TABLE TO [$(AppLogin)];
GO
