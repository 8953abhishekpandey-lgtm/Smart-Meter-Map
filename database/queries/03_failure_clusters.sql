/* Failure Cluster Finder (pure T-SQL reference implementation).
   1. "Hot" buildings = failure rate on the selected day above @MinFailureRate.
   2. Grid-based connected components (8-neighbour cells of @EpsMeters) by iterative label
      propagation ~ DBSCAN(minPts = 1). The API runs an exact DBSCAN in C# on the same input.
   3. Keep clusters with >= @MinMeters meters; NSP breakdown per cluster.                      */
USE [SmartMeterDb];
SET NOCOUNT ON;
DECLARE @MinMeters int = 500, @MinFailureRate float = 0.25, @EpsMeters float = 400;

DROP TABLE IF EXISTS #hot, #cell, #edge;
SELECT s.BuildingKey, MIN(s.Lon) AS Lon, MIN(s.Lat) AS Lat,
       SUM(s.MeterCount) AS Meters, SUM(s.E30) AS Expected, SUM(s.C30) AS Comm
INTO #hot
FROM sm.BuildingSummary s
WHERE s.Lon IS NOT NULL
GROUP BY s.BuildingKey
HAVING SUM(s.E30) > 0 AND (SUM(s.E30) - SUM(s.C30)) * 1.0 / NULLIF(SUM(s.E30), 0) > @MinFailureRate;

-- equirectangular metres (good to < 1% at city scale)
ALTER TABLE #hot ADD Cx int, Cy int;
UPDATE #hot SET Cx = CAST(FLOOR(Lon * 111320.0 * COS(RADIANS(Lat)) / @EpsMeters) AS int),
                Cy = CAST(FLOOR(Lat * 110540.0 / @EpsMeters) AS int);

SELECT Cx, Cy, ROW_NUMBER() OVER (ORDER BY Cx, Cy) AS Id, ROW_NUMBER() OVER (ORDER BY Cx, Cy) AS Label
INTO #cell FROM (SELECT DISTINCT Cx, Cy FROM #hot) c;
CREATE UNIQUE CLUSTERED INDEX CX_cell ON #cell (Cx, Cy);
CREATE UNIQUE INDEX UX_cell_Id ON #cell (Id) INCLUDE (Label);

-- adjacency (8-neighbourhood) computed once; each propagation round is then one hash join
SELECT a.Id AS A, b.Id AS B
INTO #edge
FROM #cell a
JOIN #cell b ON b.Cx BETWEEN a.Cx - 1 AND a.Cx + 1 AND b.Cy BETWEEN a.Cy - 1 AND a.Cy + 1 AND b.Id <> a.Id;

DECLARE @changed int = 1;
WHILE @changed > 0
BEGIN
    UPDATE c SET Label = m.MinLabel
    FROM #cell c
    JOIN (SELECT e.A, MIN(x.Label) AS MinLabel FROM #edge e JOIN #cell x ON x.Id = e.B GROUP BY e.A) m ON m.A = c.Id
    WHERE m.MinLabel < c.Label;
    SET @changed = @@ROWCOUNT;
END;

WITH cl AS (
    SELECT c.Label AS ClusterId, h.*
    FROM #hot h JOIN #cell c ON c.Cx = h.Cx AND c.Cy = h.Cy
), agg AS (
    SELECT ClusterId, COUNT(*) AS Buildings, SUM(Meters) AS Meters, SUM(Expected) AS Expected, SUM(Comm) AS Comm,
           SUM(Lon * Meters) / SUM(Meters) AS Lon, SUM(Lat * Meters) / SUM(Meters) AS Lat
    FROM cl GROUP BY ClusterId HAVING SUM(Meters) >= @MinMeters
)
SELECT TOP (25) a.ClusterId, a.Buildings, a.Meters, a.Expected - a.Comm AS Failing,
       CAST(100.0 * (a.Expected - a.Comm) / NULLIF(a.Expected, 0) AS decimal(5, 1)) AS FailurePct,
       CAST(a.Lon AS decimal(9, 5)) AS Lon, CAST(a.Lat AS decimal(9, 5)) AS Lat,
       (SELECT STRING_AGG(CONCAT(x.Nsp, N' ', x.Failing, N'/', x.Expected), N', ') WITHIN GROUP (ORDER BY x.Failing DESC)
        FROM (SELECT n.Name AS Nsp, SUM(s.E30) - SUM(s.C30) AS Failing, SUM(s.E30) AS Expected
              FROM cl JOIN sm.BuildingSummary s ON s.BuildingKey = cl.BuildingKey JOIN sm.Nsp n ON n.NspId = s.NspId
              WHERE cl.ClusterId = a.ClusterId GROUP BY n.Name) x) AS NspFailingOfExpected
FROM agg a
ORDER BY Failing DESC;
