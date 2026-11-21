/* =====================================================================================
   Smart Meter Communication & Health Monitoring Platform  -  SQL Server 2022+ (port 1433)
   00_create_database.sql : database + production settings.
   Run in SSMS (connected to master) or:  sqlcmd -S localhost,1433 -U sa -i 00_create_database.sql
   ===================================================================================== */
IF DB_ID(N'SmartMeterDb') IS NULL
BEGIN
    CREATE DATABASE [SmartMeterDb];
END
GO
ALTER DATABASE [SmartMeterDb] SET COMPATIBILITY_LEVEL = 160;           -- SQL Server 2022 features (GENERATE_SERIES, BIT_COUNT, ordered CCI)
GO
-- Readers never block writers (and vice versa): dashboards keep reading during ingestion / refresh.
-- Only switched when off: WITH ROLLBACK IMMEDIATE would otherwise disconnect live sessions on every re-deploy.
IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = N'SmartMeterDb') = 0
    ALTER DATABASE [SmartMeterDb] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
ALTER DATABASE [SmartMeterDb] SET ALLOW_SNAPSHOT_ISOLATION ON;
ALTER DATABASE [SmartMeterDb] SET AUTO_UPDATE_STATISTICS_ASYNC ON;
ALTER DATABASE [SmartMeterDb] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = AUTO);
-- Production: FULL recovery + log backups. Switch to BULK_LOGGED around very large back-fills if desired.
ALTER DATABASE [SmartMeterDb] SET RECOVERY FULL;
GO
