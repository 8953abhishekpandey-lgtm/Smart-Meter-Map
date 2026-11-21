/* =====================================================================================
   03_analytics.sql : pre-aggregation layer + refresh procedure.

   sm.MeterHealth      1 row / meter   : 31-bit communication + eligibility masks over the rolling
                                         window and the 30-day health class (clustered columnstore).
   sm.Building         1 row / building: centroid, dominant zone, NSP count.
   sm.BuildingSummary  building x zone x NSP x manufacturer x health x last7, with per-day
                       counters E01..E31 / C01..C31   -> building bubbles, drill-down, analytics tools.
   sm.GridSummary      5-level spatial pyramid of the same cube -> zoomed-out map bubbles.
   sm.KpiCube          zone x NSP x manufacturer x health x last7 -> KPI bar, chips, fleet trend.
   sm.ZoneExtent       zone bounding boxes for "fly to zone".

   Conventions (window of N = WindowDays days ending at WindowEnd):
     mask bit i      <-> day (WindowEnd - i)               bit 0 = most recent day
     column Ekk/Ckk  <-> day (WindowStart + kk - 1)        kk = 01..N (chronological); kk > N is 0
     E = meters EXPECTED to communicate that day (installed / eligible), C = meters that DID.
     HealthClass     : 0 Dead (0 comm days), 1 Intermittent, 2 All-day, 3 Not yet installed.
     Eligibility     : a meter is expected from its InstallationDate (or any day it actually
                       communicated), so new lots are never reported as failing before they existed.

   Generation swap: everything is built as sm.<Table>__next while readers keep using the live
   generation; the swap is a few sp_rename calls in one short transaction with LOCK_TIMEOUT + retry.
   ===================================================================================== */
USE [SmartMeterDb];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE sm.usp_RefreshAnalytics
    @WindowEnd date = NULL
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'sm.RefreshAnalytics', @LockMode = N'Exclusive',
                               @LockOwner = N'Session', @LockTimeout = 0;
    IF @lock < 0
    BEGIN
        RAISERROR (N'usp_RefreshAnalytics: another refresh is running - skipped.', 10, 1) WITH NOWAIT;
        RETURN 1;
    END

    BEGIN TRY
        DECLARE @t0 datetime2(3) = SYSUTCDATETIME(), @Days int, @End date, @Start date, @Full int;
        DECLARE @sql nvarchar(max), @maskCols nvarchar(max), @sumCols nvarchar(max), @stats nvarchar(max);

        -- ------------------------------------------------------------ window
        SELECT @Days = ConfigWindowDays, @End = COALESCE(@WindowEnd, ConfigAsOfDate) FROM sm.AnalyticsState;
        IF @End IS NULL SELECT @End = MAX(LogDate) FROM sm.CommLogDayStats WHERE [RowCount] > 0;
        SET @End   = COALESCE(@End, DATEADD(day, -1, CAST(SYSUTCDATETIME() AS date)));
        SET @Start = DATEADD(day, -(@Days - 1), @End);
        SET @Full  = CAST(LEFT_SHIFT(CAST(1 AS bigint), @Days) - 1 AS int);      -- fits int for <= 31 days

        -- per-day column expressions (kk = 01..31; days beyond the window are constant 0)
        SELECT @maskCols = STRING_AGG(CAST(
                   CASE WHEN g.value <= @Days
                        THEN CONCAT(N'SUM(RIGHT_SHIFT(h.EligMask, ', @Days - g.value, N') & 1) AS E', RIGHT(CONCAT(N'0', g.value), 2),
                                    N', SUM(RIGHT_SHIFT(h.CommMask, ', @Days - g.value, N') & 1) AS C', RIGHT(CONCAT(N'0', g.value), 2))
                        ELSE CONCAT(N'0 AS E', RIGHT(CONCAT(N'0', g.value), 2), N', 0 AS C', RIGHT(CONCAT(N'0', g.value), 2))
                   END AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY g.value),
               @sumCols = STRING_AGG(CAST(
                   CONCAT(N'SUM(s.E', RIGHT(CONCAT(N'0', g.value), 2), N') AS E', RIGHT(CONCAT(N'0', g.value), 2),
                          N', SUM(s.C', RIGHT(CONCAT(N'0', g.value), 2), N') AS C', RIGHT(CONCAT(N'0', g.value), 2))
                   AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY g.value)
        FROM GENERATE_SERIES(1, 31) AS g;

        DROP TABLE IF EXISTS sm.MeterHealth__next, sm.Building__next, sm.BuildingSummary__next,
                             sm.GridSummary__next, sm.KpiCube__next, sm.ZoneExtent__next,
                             sm.MeterHealth__old, sm.Building__old, sm.BuildingSummary__old,
                             sm.GridSummary__old, sm.KpiCube__old, sm.ZoneExtent__old;

        -- ------------------------------------------------------------ 1. MeterHealth
        -- One batch-mode pass over <= 31 daily columnstore partitions (partition elimination on LogDate).
        SELECT CAST(m.MeterSk AS int)                                   AS MeterSk,
               m.MeterId, m.BuildingKey, m.ZoneId, m.NspId, m.ManufacturerId, m.InstallationDate,
               m.Longitude                                              AS Lon,
               m.Latitude                                               AS Lat,
               c.CommMask,
               e.EligMask,
               CAST(BIT_COUNT(e.EligMask) AS tinyint)                   AS EligDays,
               CAST(BIT_COUNT(c.CommMask) AS tinyint)                   AS CommDays,
               l.LastCommDate,
               CAST(CASE WHEN BIT_COUNT(e.EligMask) = 0 THEN 3
                         WHEN BIT_COUNT(c.CommMask) = 0 THEN 0
                         WHEN BIT_COUNT(c.CommMask) >= BIT_COUNT(e.EligMask) THEN 2
                         ELSE 1 END AS tinyint)                         AS HealthClass,
               CAST(IIF(c.CommMask & 127 <> 0, 1, 0) AS bit)            AS ActiveLast7
        INTO sm.MeterHealth__next
        FROM sm.Meter m
        LEFT JOIN (SELECT MeterSk,
                          SUM(CASE WHEN IsCommunicating = 1 THEN LEFT_SHIFT(1, DATEDIFF(day, LogDate, @End)) ELSE 0 END) AS CommMask,
                          MAX(CASE WHEN IsCommunicating = 1 THEN LogDate END) AS LastCommDate
                   FROM sm.CommLog
                   WHERE LogDate BETWEEN @Start AND @End
                   GROUP BY MeterSk) l ON l.MeterSk = m.MeterSk
        CROSS APPLY (SELECT ISNULL(l.CommMask, 0) AS CommMask) c
        CROSS APPLY (SELECT (CASE WHEN m.InstallationDate IS NULL OR m.InstallationDate <= @Start THEN @Full
                                  WHEN m.InstallationDate > @End THEN 0
                                  ELSE CAST(LEFT_SHIFT(CAST(1 AS bigint), DATEDIFF(day, m.InstallationDate, @End) + 1) - 1 AS int)
                             END) | c.CommMask AS EligMask) e;

        CREATE CLUSTERED COLUMNSTORE INDEX CCI_MeterHealth ON sm.MeterHealth__next ORDER (InstallationDate);
        CREATE UNIQUE NONCLUSTERED INDEX UX_MeterHealth_MeterSk ON sm.MeterHealth__next (MeterSk);
        CREATE NONCLUSTERED INDEX IX_MeterHealth_Building ON sm.MeterHealth__next (BuildingKey)
            INCLUDE (MeterId, NspId, ManufacturerId, InstallationDate, HealthClass, ActiveLast7,
                     EligDays, CommDays, LastCommDate, EligMask, CommMask);
        -- Covering index for status aggregations by filter combination.
        CREATE NONCLUSTERED INDEX IX_MeterHealth_Filters ON sm.MeterHealth__next (ZoneId, NspId, ManufacturerId, InstallationDate)
            INCLUDE (HealthClass, ActiveLast7, EligMask, CommMask);

        -- ------------------------------------------------------------ 2. Building
        ;WITH zc AS (
            SELECT BuildingKey, ZoneId,
                   ROW_NUMBER() OVER (PARTITION BY BuildingKey ORDER BY COUNT_BIG(*) DESC, ZoneId) AS rn
            FROM sm.Meter
            GROUP BY BuildingKey, ZoneId
        ), agg AS (
            SELECT BuildingKey, MAX(BuildingId) AS BuildingId, MIN(Address) AS Address,
                   COUNT(*) AS MeterCount, COUNT(DISTINCT NspId) AS NspCount,
                   AVG(Longitude) AS Lon, AVG(Latitude) AS Lat
            FROM sm.Meter
            GROUP BY BuildingKey
        )
        SELECT a.BuildingKey, a.BuildingId,
               CAST(IIF(a.BuildingId IS NULL, 1, 0) AS bit) AS IsVirtual,
               CAST(COALESCE(a.BuildingId,
                             CASE WHEN a.Lat IS NOT NULL
                                  THEN CONCAT(N'Locality ', CAST(CAST(a.Lat AS decimal(9, 5)) AS varchar(12)), N', ',
                                                            CAST(CAST(a.Lon AS decimal(9, 5)) AS varchar(12)))
                                  ELSE N'Unlocated meters' END) AS nvarchar(100)) AS Name,
               zc.ZoneId, a.Address, a.MeterCount, CAST(a.NspCount AS tinyint) AS NspCount, a.Lon, a.Lat
        INTO sm.Building__next
        FROM agg a
        JOIN zc ON zc.BuildingKey = a.BuildingKey AND zc.rn = 1;

        CREATE UNIQUE CLUSTERED INDEX UCX_Building ON sm.Building__next (BuildingKey) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_Building_Zone ON sm.Building__next (ZoneId) INCLUDE (Name, Address, MeterCount, Lon, Lat);

        -- ------------------------------------------------------------ 3. BuildingSummary
        SET @sql = N'
        SELECT a.*, b.Lon, b.Lat
        INTO sm.BuildingSummary__next
        FROM (
            SELECT h.BuildingKey, h.ZoneId, h.NspId, h.ManufacturerId, h.HealthClass, h.ActiveLast7,
                   COUNT(*) AS MeterCount,
                   SUM(CAST(h.EligDays AS int)) AS EligTotal,
                   SUM(CAST(h.CommDays AS int)) AS CommTotal,
                   ' + @maskCols + N'
            FROM sm.MeterHealth__next h
            GROUP BY h.BuildingKey, h.ZoneId, h.NspId, h.ManufacturerId, h.HealthClass, h.ActiveLast7
        ) a
        JOIN sm.Building__next b ON b.BuildingKey = a.BuildingKey;';
        EXEC sys.sp_executesql @sql;
        CREATE CLUSTERED COLUMNSTORE INDEX CCI_BuildingSummary ON sm.BuildingSummary__next ORDER (Lat);
        CREATE NONCLUSTERED INDEX IX_BuildingSummary_Building ON sm.BuildingSummary__next (BuildingKey);

        -- ------------------------------------------------------------ 4. GridSummary (map pyramid)
        SET @sql = N'
        SELECT g.Level,
               CAST(FLOOR(s.Lon / g.CellDeg) AS int) AS CellX,
               CAST(FLOOR(s.Lat / g.CellDeg) AS int) AS CellY,
               s.ZoneId, s.NspId, s.ManufacturerId, s.HealthClass, s.ActiveLast7,
               SUM(s.MeterCount)               AS MeterCount,
               SUM(s.Lon * s.MeterCount)       AS SumLon,
               SUM(s.Lat * s.MeterCount)       AS SumLat,
               MIN(s.BuildingKey)              AS MinBuildingKey,
               MAX(s.BuildingKey)              AS MaxBuildingKey,
               SUM(CAST(s.EligTotal AS bigint)) AS EligTotal,
               SUM(CAST(s.CommTotal AS bigint)) AS CommTotal,
               ' + @sumCols + N'
        INTO sm.GridSummary__next
        FROM sm.BuildingSummary__next s
        CROSS JOIN sm.GridLevel g
        WHERE s.Lon IS NOT NULL
        GROUP BY g.Level, CAST(FLOOR(s.Lon / g.CellDeg) AS int), CAST(FLOOR(s.Lat / g.CellDeg) AS int),
                 s.ZoneId, s.NspId, s.ManufacturerId, s.HealthClass, s.ActiveLast7;';
        EXEC sys.sp_executesql @sql;
        CREATE CLUSTERED COLUMNSTORE INDEX CCI_GridSummary ON sm.GridSummary__next ORDER (Level, CellY);

        -- ------------------------------------------------------------ 5. KpiCube
        SET @sql = N'
        SELECT s.ZoneId, s.NspId, s.ManufacturerId, s.HealthClass, s.ActiveLast7,
               SUM(s.MeterCount) AS MeterCount,
               SUM(CAST(s.EligTotal AS bigint)) AS EligTotal,
               SUM(CAST(s.CommTotal AS bigint)) AS CommTotal,
               ' + @sumCols + N'
        INTO sm.KpiCube__next
        FROM sm.BuildingSummary__next s
        GROUP BY s.ZoneId, s.NspId, s.ManufacturerId, s.HealthClass, s.ActiveLast7;';
        EXEC sys.sp_executesql @sql;
        CREATE CLUSTERED INDEX CX_KpiCube ON sm.KpiCube__next (ZoneId, NspId, ManufacturerId);

        -- ------------------------------------------------------------ 6. ZoneExtent
        SELECT ZoneId, COUNT(*) AS BuildingCount, SUM(MeterCount) AS LocatedMeterCount,
               MIN(Lon) AS MinLon, MIN(Lat) AS MinLat, MAX(Lon) AS MaxLon, MAX(Lat) AS MaxLat
        INTO sm.ZoneExtent__next
        FROM sm.Building__next
        WHERE Lon IS NOT NULL
        GROUP BY ZoneId;
        CREATE UNIQUE CLUSTERED INDEX UCX_ZoneExtent ON sm.ZoneExtent__next (ZoneId);

        SET @stats = (SELECT (SELECT COUNT_BIG(*) FROM sm.MeterHealth__next)     AS meters,
                             (SELECT COUNT_BIG(*) FROM sm.Building__next)        AS buildings,
                             (SELECT COUNT_BIG(*) FROM sm.BuildingSummary__next) AS buildingSummaryRows,
                             (SELECT COUNT_BIG(*) FROM sm.GridSummary__next)     AS gridRows,
                             (SELECT COUNT_BIG(*) FROM sm.KpiCube__next)         AS kpiCubeRows
                      FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

        -- ------------------------------------------------------------ swap (short, retried)
        DECLARE @attempt int = 0, @swapped bit = 0, @tbl sysname;
        DECLARE @tables TABLE (Ord int, Name sysname);
        INSERT INTO @tables VALUES (1, N'MeterHealth'), (2, N'Building'), (3, N'BuildingSummary'),
                                   (4, N'GridSummary'), (5, N'KpiCube'), (6, N'ZoneExtent');
        SET LOCK_TIMEOUT 3000;
        WHILE @swapped = 0 AND @attempt < 20
        BEGIN
            SET @attempt += 1;
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE swap_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @tables ORDER BY Ord;
                OPEN swap_cursor;
                FETCH NEXT FROM swap_cursor INTO @tbl;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    DECLARE @cur nvarchar(300) = N'sm.' + QUOTENAME(@tbl), @nxt nvarchar(300) = N'sm.' + QUOTENAME(@tbl + N'__next');
                    DECLARE @old sysname = @tbl + N'__old';
                    IF OBJECT_ID(@cur, N'U') IS NOT NULL EXEC sys.sp_rename @cur, @old, N'OBJECT';
                    EXEC sys.sp_rename @nxt, @tbl, N'OBJECT';
                    FETCH NEXT FROM swap_cursor INTO @tbl;
                END
                CLOSE swap_cursor;
                DEALLOCATE swap_cursor;

                UPDATE sm.AnalyticsState
                   SET WindowDays = @Days, WindowStart = @Start, WindowEnd = @End,
                       DataVersion = DataVersion + 1,
                       RefreshStartedAt = @t0, RefreshedAt = SYSUTCDATETIME(),
                       RefreshMs = DATEDIFF(millisecond, @t0, SYSUTCDATETIME()),
                       Stats = @stats;
                COMMIT TRANSACTION;
                SET @swapped = 1;
            END TRY
            BEGIN CATCH
                IF CURSOR_STATUS('local', 'swap_cursor') >= -1 DEALLOCATE swap_cursor;
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                IF ERROR_NUMBER() <> 1222 THROW;           -- 1222 = lock request time out
                RAISERROR (N'usp_RefreshAnalytics: swap attempt %d waited too long for readers, retrying.', 10, 1, @attempt) WITH NOWAIT;
                WAITFOR DELAY '00:00:01';
            END CATCH
        END
        IF @swapped = 0 THROW 50011, N'usp_RefreshAnalytics: could not acquire schema locks to swap the new generation.', 1;

        -- Old generation: best effort (a long-running reader may still hold it; next refresh retries).
        BEGIN TRY
            DROP TABLE IF EXISTS sm.MeterHealth__old, sm.Building__old, sm.BuildingSummary__old,
                                 sm.GridSummary__old, sm.KpiCube__old, sm.ZoneExtent__old;
        END TRY
        BEGIN CATCH
            RAISERROR (N'usp_RefreshAnalytics: old generation still in use; it will be dropped by the next refresh.', 10, 1) WITH NOWAIT;
        END CATCH
        SET LOCK_TIMEOUT -1;

        EXEC sp_releaseapplock @Resource = N'sm.RefreshAnalytics', @LockOwner = N'Session';
        RETURN 0;
    END TRY
    BEGIN CATCH
        SET LOCK_TIMEOUT -1;
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        IF APPLOCK_MODE(N'public', N'sm.RefreshAnalytics', N'Session') <> N'NoLock'
            EXEC sp_releaseapplock @Resource = N'sm.RefreshAnalytics', @LockOwner = N'Session';
        THROW;
    END CATCH
END
GO
