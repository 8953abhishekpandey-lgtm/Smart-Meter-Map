/* =====================================================================================
   04_ingestion.sql : set-based merge of SqlBulkCopy-loaded staging heaps into production.

   Pipeline (driven by the .NET Hangfire job):
     1. EXEC sm.usp_CreateStageTable            -> stage.[cis_<job>] / stage.[mdm_<job>] heap
     2. SqlBulkCopy (TABLOCK, streaming) of validated rows from the Excel/CSV reader
     3. EXEC sm.usp_MergeCisStage  |  sm.usp_MergeMdmStageDay (once per day in the file)
     4. EXEC sm.usp_DropStageTable ; enqueue sm.usp_RefreshAnalytics
   ===================================================================================== */
USE [SmartMeterDb];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Validates a staging table name produced by usp_CreateStageTable (defence against injection).
CREATE OR ALTER FUNCTION sm.fn_IsStageTable (@StageTable nvarchar(300), @Kind varchar(8))
RETURNS bit
AS
BEGIN
    RETURN CASE WHEN PARSENAME(@StageTable, 2) = N'stage'
                 AND PARSENAME(@StageTable, 3) IS NULL
                 AND PARSENAME(@StageTable, 1) LIKE @Kind + N'[_]' + REPLICATE(N'[0-9a-f]', 32)
                 AND LEN(PARSENAME(@StageTable, 1)) = LEN(@Kind) + 33
                 AND OBJECT_ID(@StageTable, N'U') IS NOT NULL
                THEN 1 ELSE 0 END;
END
GO

CREATE OR ALTER PROCEDURE sm.usp_CreateStageTable
    @Kind       varchar(8),
    @JobId      uniqueidentifier,
    @StageTable nvarchar(300) OUTPUT
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @name sysname = @Kind + N'_' + LOWER(REPLACE(CONVERT(nvarchar(36), @JobId), N'-', N''));
    SET @StageTable = N'stage.' + QUOTENAME(@name);
    IF OBJECT_ID(@StageTable, N'U') IS NOT NULL EXEC (N'DROP TABLE ' + @StageTable);

    IF @Kind = 'cis'
        EXEC (N'CREATE TABLE ' + @StageTable + N' (
                    RowNo            bigint        NOT NULL,
                    MeterId          varchar(32)   NOT NULL,
                    BuildingId       varchar(64)   NULL,
                    Latitude         float         NULL,
                    Longitude        float         NULL,
                    Address          nvarchar(400) NULL,
                    Nsp              nvarchar(100) NOT NULL,
                    Manufacturer     nvarchar(100) NOT NULL,
                    Zone             nvarchar(100) NOT NULL,
                    InstallationDate date          NULL,
                    DtId             varchar(64)   NULL)');
    ELSE IF @Kind = 'mdm'
        EXEC (N'CREATE TABLE ' + @StageTable + N' (
                    RowNo            bigint      NOT NULL,
                    MeterId          varchar(32) NOT NULL,
                    LogDate          date        NOT NULL,
                    IsCommunicating  bit         NOT NULL)');
    ELSE
        THROW 50030, N'usp_CreateStageTable: @Kind must be cis or mdm.', 1;
END
GO

CREATE OR ALTER PROCEDURE sm.usp_DropStageTable
    @StageTable nvarchar(300)
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    IF sm.fn_IsStageTable(@StageTable, 'cis') = 1 OR sm.fn_IsStageTable(@StageTable, 'mdm') = 1
        EXEC (N'DROP TABLE ' + @StageTable);
END
GO

/* ------------------------------------------------------------------------------------
   CIS master upsert. Last row wins for duplicate MeterIds inside one file. New Zone / NSP /
   Manufacturer names are added to the dimensions. Unchanged rows are skipped (null-safe
   EXCEPT comparison => no log, no version churn). Batches of 200K keep transactions and
   the log bounded on 15M-row loads.                                                       */
CREATE OR ALTER PROCEDURE sm.usp_MergeCisStage
    @StageTable nvarchar(300),
    @Inserted   bigint OUTPUT,
    @Updated    bigint OUTPUT
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF sm.fn_IsStageTable(@StageTable, 'cis') = 0 THROW 50031, N'usp_MergeCisStage: invalid staging table.', 1;

    DECLARE @sql nvarchar(max) = N'
        SELECT s.MeterId, s.BuildingId, s.Latitude, s.Longitude, s.Address, s.Nsp, s.Manufacturer, s.Zone,
               s.InstallationDate, s.DtId, ROW_NUMBER() OVER (ORDER BY s.MeterId) AS Seq
        INTO #src
        FROM (SELECT x.*, ROW_NUMBER() OVER (PARTITION BY x.MeterId ORDER BY x.RowNo DESC) AS rn
              FROM ' + @StageTable + N' x) s
        WHERE s.rn = 1;

        BEGIN TRANSACTION;
            INSERT INTO sm.Zone (Name)
            SELECT DISTINCT s.Zone FROM #src s
            WHERE NOT EXISTS (SELECT 1 FROM sm.Zone d WITH (UPDLOCK, HOLDLOCK) WHERE d.Name = s.Zone);
            INSERT INTO sm.Nsp (Name)
            SELECT DISTINCT s.Nsp FROM #src s
            WHERE NOT EXISTS (SELECT 1 FROM sm.Nsp d WITH (UPDLOCK, HOLDLOCK) WHERE d.Name = s.Nsp);
            INSERT INTO sm.Manufacturer (Name)
            SELECT DISTINCT s.Manufacturer FROM #src s
            WHERE NOT EXISTS (SELECT 1 FROM sm.Manufacturer d WITH (UPDLOCK, HOLDLOCK) WHERE d.Name = s.Manufacturer);
        COMMIT TRANSACTION;

        SELECT s.Seq, s.MeterId, s.BuildingId, s.Latitude, s.Longitude, s.Address, z.ZoneId, n.NspId,
               f.ManufacturerId, s.InstallationDate, s.DtId
        INTO #res
        FROM #src s
        JOIN sm.Zone z ON z.Name = s.Zone
        JOIN sm.Nsp n ON n.Name = s.Nsp
        JOIN sm.Manufacturer f ON f.Name = s.Manufacturer;
        CREATE UNIQUE CLUSTERED INDEX CX_res ON #res (Seq);

        DECLARE @batch bigint = 200000, @from bigint = 1, @max bigint = (SELECT ISNULL(MAX(Seq), 0) FROM #res);
        SELECT @insOut = 0, @updOut = 0;
        WHILE @from <= @max
        BEGIN
            BEGIN TRANSACTION;
                UPDATE m
                   SET BuildingId = s.BuildingId, Latitude = s.Latitude, Longitude = s.Longitude, Address = s.Address,
                       ZoneId = s.ZoneId, NspId = s.NspId, ManufacturerId = s.ManufacturerId,
                       InstallationDate = s.InstallationDate, DtId = s.DtId, UpdatedAt = SYSUTCDATETIME()
                  FROM sm.Meter m
                  JOIN #res s ON s.MeterId = m.MeterId
                 WHERE s.Seq BETWEEN @from AND @from + @batch - 1
                   AND EXISTS (SELECT s.BuildingId, s.Latitude, s.Longitude, s.Address, s.ZoneId, s.NspId,
                                      s.ManufacturerId, s.InstallationDate, s.DtId
                               EXCEPT
                               SELECT m.BuildingId, m.Latitude, m.Longitude, m.Address, m.ZoneId, m.NspId,
                                      m.ManufacturerId, m.InstallationDate, m.DtId);
                SET @updOut += @@ROWCOUNT;

                INSERT INTO sm.Meter (MeterId, BuildingId, Latitude, Longitude, Address, ZoneId, NspId,
                                      ManufacturerId, InstallationDate, DtId)
                SELECT s.MeterId, s.BuildingId, s.Latitude, s.Longitude, s.Address, s.ZoneId, s.NspId,
                       s.ManufacturerId, s.InstallationDate, s.DtId
                FROM #res s
                WHERE s.Seq BETWEEN @from AND @from + @batch - 1
                  AND NOT EXISTS (SELECT 1 FROM sm.Meter m WITH (UPDLOCK, HOLDLOCK) WHERE m.MeterId = s.MeterId);
                SET @insOut += @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @from += @batch;
        END';

    EXEC sys.sp_executesql @sql, N'@insOut bigint OUTPUT, @updOut bigint OUTPUT',
                           @insOut = @Inserted OUTPUT, @updOut = @Updated OUTPUT;
END
GO

-- Distinct days contained in an MDM staging table (the job merges one day at a time).
CREATE OR ALTER PROCEDURE sm.usp_StageDays
    @StageTable nvarchar(300)
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    IF sm.fn_IsStageTable(@StageTable, 'mdm') = 0 THROW 50032, N'usp_StageDays: invalid staging table.', 1;
    DECLARE @sql nvarchar(max) = N'SELECT LogDate, COUNT_BIG(*) AS [Rows] FROM ' + @StageTable + N' GROUP BY LogDate ORDER BY LogDate;';
    EXEC sys.sp_executesql @sql;
END
GO

/* ------------------------------------------------------------------------------------
   MDM log merge for ONE day. Duplicate rows for the same meter/day: success wins (the link
   succeeded at least once). Unknown MeterIds are skipped here and reported by the job.
   A brand-new day takes the bulk path (TABLOCK -> compressed columnstore row groups).     */
CREATE OR ALTER PROCEDURE sm.usp_MergeMdmStageDay
    @StageTable nvarchar(300),
    @LogDate    date,
    @Inserted   bigint OUTPUT,
    @Updated    bigint OUTPUT
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF sm.fn_IsStageTable(@StageTable, 'mdm') = 0 THROW 50033, N'usp_MergeMdmStageDay: invalid staging table.', 1;

    EXEC sm.usp_EnsureCommLogPartitions @LogDate, @LogDate;

    DECLARE @sql nvarchar(max) = N'
        SELECT m.MeterSk, CAST(MAX(CAST(s.IsCommunicating AS tinyint)) AS bit) AS IsCommunicating
        INTO #d
        FROM ' + @StageTable + N' s
        JOIN sm.Meter m ON m.MeterId = s.MeterId
        WHERE s.LogDate = @d
        GROUP BY m.MeterSk;
        CREATE UNIQUE CLUSTERED INDEX CX_d ON #d (MeterSk);
        SELECT @insOut = 0, @updOut = 0;

        IF EXISTS (SELECT 1 FROM sm.CommLog WHERE LogDate = @d)
        BEGIN
            BEGIN TRANSACTION;
                UPDATE c SET IsCommunicating = d.IsCommunicating
                  FROM sm.CommLog c
                  JOIN #d d ON d.MeterSk = c.MeterSk
                 WHERE c.LogDate = @d AND c.IsCommunicating <> d.IsCommunicating;
                SET @updOut = @@ROWCOUNT;
                INSERT INTO sm.CommLog (LogDate, MeterSk, IsCommunicating)
                SELECT @d, d.MeterSk, d.IsCommunicating
                FROM #d d
                WHERE NOT EXISTS (SELECT 1 FROM sm.CommLog c WHERE c.LogDate = @d AND c.MeterSk = d.MeterSk);
                SET @insOut = @@ROWCOUNT;
            COMMIT TRANSACTION;
        END
        ELSE
        BEGIN
            INSERT INTO sm.CommLog WITH (TABLOCK) (LogDate, MeterSk, IsCommunicating)
            SELECT @d, d.MeterSk, d.IsCommunicating FROM #d d;
            SET @insOut = @@ROWCOUNT;
        END';

    EXEC sys.sp_executesql @sql, N'@d date, @insOut bigint OUTPUT, @updOut bigint OUTPUT',
                           @d = @LogDate, @insOut = @Inserted OUTPUT, @updOut = @Updated OUTPUT;
    EXEC sm.usp_RecomputeDayStats @LogDate;
END
GO

-- Rows whose MeterId is not in the CIS master (reported as row-level validation errors).
CREATE OR ALTER PROCEDURE sm.usp_StageUnknownMeters
    @StageTable nvarchar(300),
    @Limit      int = 10000
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    IF sm.fn_IsStageTable(@StageTable, 'mdm') = 0 THROW 50034, N'usp_StageUnknownMeters: invalid staging table.', 1;
    DECLARE @sql nvarchar(max) = N'
        SELECT COUNT_BIG(*) AS UnknownRows FROM ' + @StageTable + N' s
        WHERE NOT EXISTS (SELECT 1 FROM sm.Meter m WHERE m.MeterId = s.MeterId);
        SELECT TOP (@limit) s.RowNo, s.MeterId FROM ' + @StageTable + N' s
        WHERE NOT EXISTS (SELECT 1 FROM sm.Meter m WHERE m.MeterId = s.MeterId)
        ORDER BY s.RowNo;';
    EXEC sys.sp_executesql @sql, N'@limit int', @limit = @Limit;
END
GO
