/* KPI bar + health chip counts + 30-day fleet trend, answered from sm.KpiCube (~1K rows),
   never from the 465M-row log table. Day column = E/C<kk>, kk = DATEDIFF(day, WindowStart, @Day) + 1. */
USE [SmartMeterDb];
SET NOCOUNT ON;
DECLARE @Day date = NULL;             -- NULL = latest day in the window
DECLARE @ZoneIds nvarchar(max) = N''; -- e.g. N'1,2'
DECLARE @k int, @Start date, @sql nvarchar(max);
SELECT @Start = WindowStart, @k = DATEDIFF(day, WindowStart, COALESCE(@Day, WindowEnd)) + 1 FROM sm.AnalyticsState;
DECLARE @kk nchar(2) = RIGHT(CONCAT(N'0', @k), 2);

SET @sql = N'
SELECT SUM(q.MeterCount)                                                   AS TotalMeters,
       SUM(q.E' + @kk + N')                                                AS ExpectedOnDay,
       SUM(q.C' + @kk + N')                                                AS CommunicatingOnDay,
       SUM(q.E' + @kk + N') - SUM(q.C' + @kk + N')                         AS FailingOnDay,
       CAST(100.0 * SUM(q.C' + @kk + N') / NULLIF(SUM(q.E' + @kk + N'), 0) AS decimal(5, 2)) AS SuccessPctOnDay,
       CAST(100.0 * SUM(q.CommTotal) / NULLIF(SUM(q.EligTotal), 0) AS decimal(5, 2))         AS AvgDailySuccessPctWindow,
       SUM(IIF(q.HealthClass = 2, q.MeterCount, 0))                        AS AllDayComm,
       SUM(IIF(q.ActiveLast7 = 1, q.MeterCount, 0))                        AS Last7DaysComm,
       SUM(IIF(q.HealthClass = 0, q.MeterCount, 0))                        AS Dead,
       SUM(IIF(q.HealthClass = 1, q.MeterCount, 0))                        AS Intermittent
FROM sm.KpiCube q
WHERE (@zones = N'''' OR q.ZoneId IN (SELECT CAST(value AS smallint) FROM STRING_SPLIT(@zones, N'','')));';
EXEC sys.sp_executesql @sql, N'@zones nvarchar(max)', @zones = @ZoneIds;

-- 30-day trend: one SUM per day column, then UNPIVOT-free CROSS APPLY VALUES
SELECT DATEADD(day, d.k - 1, @Start) AS LogDate, d.E AS Expected, d.C AS Communicating,
       CAST(100.0 * d.C / NULLIF(d.E, 0) AS decimal(5, 2)) AS SuccessPct
FROM (SELECT SUM(E01) E01, SUM(C01) C01, SUM(E02) E02, SUM(C02) C02, SUM(E03) E03, SUM(C03) C03, SUM(E04) E04, SUM(C04) C04,
             SUM(E05) E05, SUM(C05) C05, SUM(E06) E06, SUM(C06) C06, SUM(E07) E07, SUM(C07) C07, SUM(E08) E08, SUM(C08) C08,
             SUM(E09) E09, SUM(C09) C09, SUM(E10) E10, SUM(C10) C10, SUM(E11) E11, SUM(C11) C11, SUM(E12) E12, SUM(C12) C12,
             SUM(E13) E13, SUM(C13) C13, SUM(E14) E14, SUM(C14) C14, SUM(E15) E15, SUM(C15) C15, SUM(E16) E16, SUM(C16) C16,
             SUM(E17) E17, SUM(C17) C17, SUM(E18) E18, SUM(C18) C18, SUM(E19) E19, SUM(C19) C19, SUM(E20) E20, SUM(C20) C20,
             SUM(E21) E21, SUM(C21) C21, SUM(E22) E22, SUM(C22) C22, SUM(E23) E23, SUM(C23) C23, SUM(E24) E24, SUM(C24) C24,
             SUM(E25) E25, SUM(C25) C25, SUM(E26) E26, SUM(C26) C26, SUM(E27) E27, SUM(C27) C27, SUM(E28) E28, SUM(C28) C28,
             SUM(E29) E29, SUM(C29) C29, SUM(E30) E30, SUM(C30) C30, SUM(E31) E31, SUM(C31) C31
      FROM sm.KpiCube) t
CROSS APPLY (VALUES (1, t.E01, t.C01), (2, t.E02, t.C02), (3, t.E03, t.C03), (4, t.E04, t.C04), (5, t.E05, t.C05),
                    (6, t.E06, t.C06), (7, t.E07, t.C07), (8, t.E08, t.C08), (9, t.E09, t.C09), (10, t.E10, t.C10),
                    (11, t.E11, t.C11), (12, t.E12, t.C12), (13, t.E13, t.C13), (14, t.E14, t.C14), (15, t.E15, t.C15),
                    (16, t.E16, t.C16), (17, t.E17, t.C17), (18, t.E18, t.C18), (19, t.E19, t.C19), (20, t.E20, t.C20),
                    (21, t.E21, t.C21), (22, t.E22, t.C22), (23, t.E23, t.C23), (24, t.E24, t.C24), (25, t.E25, t.C25),
                    (26, t.E26, t.C26), (27, t.E27, t.C27), (28, t.E28, t.C28), (29, t.E29, t.C29), (30, t.E30, t.C30),
                    (31, t.E31, t.C31)) d (k, E, C)
WHERE d.k <= (SELECT WindowDays FROM sm.AnalyticsState)
ORDER BY d.k;
