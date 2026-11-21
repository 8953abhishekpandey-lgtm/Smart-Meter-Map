/* SIM / NSP switch opportunities: buildings where one NSP is strong (>= @Strong success) and another
   is failing (>= @WeakFailure). Metric = 30-day average daily success (robust for a swap decision). */
USE [SmartMeterDb];
SET NOCOUNT ON;
DECLARE @Strong float = 0.85, @WeakFailure float = 0.70, @MinMetersPerNsp int = 3;

WITH bn AS (
    SELECT s.BuildingKey, s.NspId, SUM(s.MeterCount) AS Meters,
           SUM(CAST(s.EligTotal AS bigint)) AS Expected, SUM(CAST(s.CommTotal AS bigint)) AS Comm
    FROM sm.BuildingSummary s
    JOIN sm.Building b ON b.BuildingKey = s.BuildingKey
    WHERE b.NspCount >= 2
    GROUP BY s.BuildingKey, s.NspId
    HAVING SUM(s.MeterCount) >= @MinMetersPerNsp AND SUM(s.EligTotal) > 0
), strong AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY BuildingKey ORDER BY Comm * 1.0 / NULLIF(Expected, 0) DESC, Meters DESC) AS rn
    FROM bn WHERE Comm * 1.0 / NULLIF(Expected, 0) >= @Strong
), weak AS (
    SELECT * FROM bn WHERE (Expected - Comm) * 1.0 / NULLIF(Expected, 0) >= @WeakFailure
)
SELECT TOP (25) b.BuildingKey, b.Name, z.Name AS Zone,
       ns.Name AS StrongNsp, s.Meters AS StrongMeters, CAST(100.0 * s.Comm / NULLIF(s.Expected, 0) AS decimal(5, 1)) AS StrongSuccessPct,
       nw.Name AS WeakNsp,   w.Meters AS WeakMeters,   CAST(100.0 * (w.Expected - w.Comm) / NULLIF(w.Expected, 0) AS decimal(5, 1)) AS WeakFailurePct
FROM weak w
JOIN strong s ON s.BuildingKey = w.BuildingKey AND s.rn = 1
JOIN sm.Building b ON b.BuildingKey = w.BuildingKey
JOIN sm.Zone z ON z.ZoneId = b.ZoneId
JOIN sm.Nsp ns ON ns.NspId = s.NspId
JOIN sm.Nsp nw ON nw.NspId = w.NspId
ORDER BY w.Meters DESC, b.BuildingKey;
