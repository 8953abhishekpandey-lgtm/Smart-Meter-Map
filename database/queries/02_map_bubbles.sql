/* Viewport bubbles. The API picks the source by zoom:
     zoom <= 14 -> sm.GridSummary at the matching pyramid level (server-side clustering)
     zoom >= 15 -> sm.BuildingSummary (one bubble per building / virtual building)
   Bubble size = meter count; colour = failure rate on the selected day
   (<= 20% green, 21-79% amber, >= 80% red).                                                   */
USE [SmartMeterDb];
SET NOCOUNT ON;
DECLARE @MinX float = 76.8, @MinY float = 28.4, @MaxX float = 77.5, @MaxY float = 28.9, @Level tinyint = 3;
DECLARE @Cell float = (SELECT CellDeg FROM sm.GridLevel WHERE Level = @Level);
-- (a) clustered cells (latest day = E30/C30 for a 30-day window)
SELECT TOP (5000)
       g.CellX, g.CellY,
       SUM(g.SumLon) / SUM(g.MeterCount)                                       AS Lon,
       SUM(g.SumLat) / SUM(g.MeterCount)                                       AS Lat,
       SUM(g.MeterCount)                                                       AS Meters,
       SUM(g.E30)                                                              AS Expected,
       SUM(g.C30)                                                              AS Communicating,
       CAST(100.0 * (SUM(g.E30) - SUM(g.C30)) / NULLIF(SUM(g.E30), 0) AS decimal(5, 1)) AS FailurePct,
       IIF(MIN(g.MinBuildingKey) = MAX(g.MaxBuildingKey), MIN(g.MinBuildingKey), NULL) AS SingleBuilding
FROM sm.GridSummary g
WHERE g.Level = @Level
  AND g.CellX BETWEEN FLOOR(@MinX / @Cell) AND FLOOR(@MaxX / @Cell)
  AND g.CellY BETWEEN FLOOR(@MinY / @Cell) AND FLOOR(@MaxY / @Cell)
GROUP BY g.CellX, g.CellY
ORDER BY Meters DESC;

-- (b) building bubbles for a street-level viewport
SELECT TOP (5000)
       s.BuildingKey, s.Lon, s.Lat,
       SUM(s.MeterCount) AS Meters, SUM(s.E30) AS Expected, SUM(s.C30) AS Communicating,
       CAST(100.0 * (SUM(s.E30) - SUM(s.C30)) / NULLIF(SUM(s.E30), 0) AS decimal(5, 1)) AS FailurePct
FROM sm.BuildingSummary s
WHERE s.Lat BETWEEN 28.50 AND 28.55 AND s.Lon BETWEEN 77.18 AND 77.24
GROUP BY s.BuildingKey, s.Lon, s.Lat
ORDER BY Meters DESC;
