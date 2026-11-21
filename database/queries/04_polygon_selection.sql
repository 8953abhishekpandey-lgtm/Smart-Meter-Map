/* Lasso / polygon selection: exact meter-level containment (independent of Building IDs), one pass with
   GROUPING SETS for totals + health + NSP + make. sm.MeterHealth carries each meter's own Lat/Lon, so a
   batch-mode columnstore range scan on the polygon's envelope finds the candidates and only those get the exact
   point-in-polygon test (much faster than the spatial index for city-sized lassos).                         */
USE [SmartMeterDb];
SET NOCOUNT ON;
DECLARE @Wkt nvarchar(max) = N'POLYGON((77.19 28.50, 77.25 28.50, 77.25 28.55, 77.19 28.55, 77.19 28.50))';
DECLARE @Day date = NULL;
DECLARE @Poly geometry = geometry::STGeomFromText(@Wkt, 4326).MakeValid();
DECLARE @Env geometry = @Poly.STEnvelope();
DECLARE @MinX float = @Env.STPointN(1).STX, @MinY float = @Env.STPointN(1).STY,
        @MaxX float = @Env.STPointN(3).STX, @MaxY float = @Env.STPointN(3).STY;
DECLARE @Bit int = (SELECT DATEDIFF(day, COALESCE(@Day, WindowEnd), WindowEnd) FROM sm.AnalyticsState);  -- bit 0 = WindowEnd

SELECT CASE WHEN GROUPING(h.NspId) = 0 THEN 'nsp' WHEN GROUPING(h.ManufacturerId) = 0 THEN 'manufacturer'
            WHEN GROUPING(h.HealthClass) = 0 THEN 'health' ELSE 'total' END AS Breakdown,
       CASE WHEN GROUPING(h.NspId) = 0 THEN MAX(n.Name)
            WHEN GROUPING(h.ManufacturerId) = 0 THEN MAX(f.Name)
            WHEN GROUPING(h.HealthClass) = 0 THEN CHOOSE(h.HealthClass + 1, N'Dead', N'Intermittent', N'All-day', N'Not installed')
            ELSE N'All meters' END                     AS Item,
       COUNT(*)                                        AS Meters,
       SUM(RIGHT_SHIFT(h.EligMask, @Bit) & 1)          AS Expected,
       SUM(RIGHT_SHIFT(h.CommMask, @Bit) & 1)          AS Communicating,
       COUNT(DISTINCT h.BuildingKey)                   AS Buildings
FROM sm.MeterHealth h
LEFT JOIN sm.Nsp n ON n.NspId = h.NspId
LEFT JOIN sm.Manufacturer f ON f.ManufacturerId = h.ManufacturerId
WHERE h.Lat BETWEEN @MinY AND @MaxY AND h.Lon BETWEEN @MinX AND @MaxX
  AND geometry::Point(h.Lon, h.Lat, 4326).STIntersects(@Poly) = 1
GROUP BY GROUPING SETS ((), (h.NspId), (h.ManufacturerId), (h.HealthClass))
ORDER BY 1, 3 DESC;
