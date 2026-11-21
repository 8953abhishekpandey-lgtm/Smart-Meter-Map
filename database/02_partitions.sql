/* =====================================================================================
   02_partitions.sql : daily partition maintenance for sm.CommLog (sliding window).
   ===================================================================================== */
USE [SmartMeterDb];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* Create missing daily boundaries for [@From, @To + 1]. RANGE RIGHT => the partition for day d is
   [d, d+1). Creating the boundary To+1 up front keeps the right-most partition EMPTY, which matters
   because SQL Server only allows SPLIT of empty partitions on columnstore tables.                */
CREATE OR ALTER PROCEDURE sm.usp_EnsureCommLogPartitions
    @From date,
    @To   date
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @From IS NULL OR @To IS NULL RETURN 0;
    IF @To < @From THROW 50001, N'usp_EnsureCommLogPartitions: @To is earlier than @From.', 1;
    IF DATEDIFF(day, @From, @To) > 400 THROW 50002, N'usp_EnsureCommLogPartitions: refusing to create more than 400 partitions at once.', 1;

    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'sm.CommLog.partitions', @LockMode = N'Exclusive',
                               @LockOwner = N'Session', @LockTimeout = 120000;
    IF @lock < 0 THROW 50003, N'usp_EnsureCommLogPartitions: could not acquire the partition maintenance lock.', 1;

    BEGIN TRY
        DECLARE @d date = @From, @created int = 0;
        WHILE @d <= DATEADD(day, 1, @To)
        BEGIN
            IF NOT EXISTS (SELECT 1
                           FROM sys.partition_range_values rv
                           JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                           WHERE pf.name = N'PF_CommLogDate' AND CAST(rv.value AS date) = @d)
            BEGIN
                ALTER PARTITION SCHEME PS_CommLogDate NEXT USED [PRIMARY];
                ALTER PARTITION FUNCTION PF_CommLogDate() SPLIT RANGE (@d);
                SET @created += 1;
            END
            SET @d = DATEADD(day, 1, @d);
        END
        EXEC sp_releaseapplock @Resource = N'sm.CommLog.partitions', @LockOwner = N'Session';
        RETURN @created;
    END TRY
    BEGIN CATCH
        IF APPLOCK_MODE(N'public', N'sm.CommLog.partitions', N'Session') <> N'NoLock'
            EXEC sp_releaseapplock @Resource = N'sm.CommLog.partitions', @LockOwner = N'Session';
        THROW;
    END CATCH
END
GO

/* Sliding-window retention: TRUNCATE whole day partitions older than RetentionDays (metadata
   operation, minimal log) and MERGE their now-empty boundaries away.                           */
CREATE OR ALTER PROCEDURE sm.usp_ApplyCommLogRetention
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @maxDate date = (SELECT MAX(LogDate) FROM sm.CommLogDayStats);
    DECLARE @keep smallint = (SELECT RetentionDays FROM sm.AnalyticsState);
    IF @maxDate IS NULL RETURN 0;
    DECLARE @cutoff date = DATEADD(day, -(@keep - 1), @maxDate), @dropped int = 0, @b1 date, @b2 date;

    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'sm.CommLog.partitions', @LockMode = N'Exclusive',
                               @LockOwner = N'Session', @LockTimeout = 120000;
    IF @lock < 0 THROW 50004, N'usp_ApplyCommLogRetention: could not acquire the partition maintenance lock.', 1;

    BEGIN TRY
        WHILE 1 = 1
        BEGIN
            SELECT @b1 = NULL, @b2 = NULL;
            SELECT @b1 = MIN(CASE WHEN rn = 1 THEN v END), @b2 = MIN(CASE WHEN rn = 2 THEN v END)
            FROM (SELECT CAST(rv.value AS date) AS v, ROW_NUMBER() OVER (ORDER BY CAST(rv.value AS date)) AS rn
                  FROM sys.partition_range_values rv
                  JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                  WHERE pf.name = N'PF_CommLogDate') x
            WHERE rn <= 2;
            -- partition 1 = (-inf, b1) and partition 2 = [b1, b2) must both end on/before the cutoff
            IF @b1 IS NULL OR @b2 IS NULL OR @b1 >= @cutoff OR @b2 > @cutoff BREAK;
            TRUNCATE TABLE sm.CommLog WITH (PARTITIONS (1 TO 2));
            ALTER PARTITION FUNCTION PF_CommLogDate() MERGE RANGE (@b1);
            SET @dropped += 1;
        END
        DELETE FROM sm.CommLogDayStats WHERE LogDate < @cutoff;
        EXEC sp_releaseapplock @Resource = N'sm.CommLog.partitions', @LockOwner = N'Session';
        RETURN @dropped;
    END TRY
    BEGIN CATCH
        IF APPLOCK_MODE(N'public', N'sm.CommLog.partitions', N'Session') <> N'NoLock'
            EXEC sp_releaseapplock @Resource = N'sm.CommLog.partitions', @LockOwner = N'Session';
        THROW;
    END CATCH
END
GO

-- Recompute load statistics for one day (called after each MDM merge for the affected days).
CREATE OR ALTER PROCEDURE sm.usp_RecomputeDayStats
    @LogDate date
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows bigint, @comm bigint;
    SELECT @rows = COUNT_BIG(*), @comm = ISNULL(SUM(CAST(IsCommunicating AS bigint)), 0)
    FROM sm.CommLog
    WHERE LogDate = @LogDate;

    IF @rows = 0
        DELETE FROM sm.CommLogDayStats WHERE LogDate = @LogDate;
    ELSE
    BEGIN
        UPDATE sm.CommLogDayStats
           SET [RowCount] = @rows, CommunicatingCount = @comm, LoadedAt = SYSUTCDATETIME()
         WHERE LogDate = @LogDate;
        IF @@ROWCOUNT = 0
            INSERT INTO sm.CommLogDayStats (LogDate, [RowCount], CommunicatingCount) VALUES (@LogDate, @rows, @comm);
    END
END
GO

-- Partition inventory for ops dashboards / troubleshooting.
CREATE OR ALTER VIEW sm.vCommLogPartitions
AS
SELECT p.partition_number                                   AS PartitionNumber,
       CAST(prv.value AS date)                               AS LogDate,
       SUM(p.rows)                                           AS [Rows],
       s.CommunicatingCount,
       s.LoadedAt
FROM sys.partitions p
JOIN sys.indexes i  ON i.object_id = p.object_id AND i.index_id = p.index_id
LEFT JOIN sys.partition_range_values prv
       ON prv.function_id = (SELECT function_id FROM sys.partition_functions WHERE name = N'PF_CommLogDate')
      AND prv.boundary_id = p.partition_number - 1
LEFT JOIN sm.CommLogDayStats s ON s.LogDate = CAST(prv.value AS date)
WHERE p.object_id = OBJECT_ID(N'sm.CommLog') AND i.index_id = 1
GROUP BY p.partition_number, prv.value, s.CommunicatingCount, s.LoadedAt;
GO
