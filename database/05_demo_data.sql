/* =====================================================================================
   05_demo_data.sql : synthetic fleet for development, demos and load tests.

     EXEC sm.usp_GenerateDemoData @Meters = 300000;     -- 300K meters x 30 days ~= 9M log rows
     EXEC sm.usp_RefreshAnalytics;

   Scenarios baked in so every analytical tool has something to find:
     * 12 utility zones across Delhi / Mumbai / Bengaluru / Hyderabad
     * ~72% tower sites with a BuildingId, ~28% house clusters WITHOUT BuildingId (grid fallback)
     * NSP-specific coverage holes in ~12% of sites      -> SIM / NSP switch opportunities
     * 8 geographic outage pockets (some NSP-specific)  -> failure cluster finder
     * dead sites (~1.2%), dead meters (~2%), intermittent meters (~5.5%)
     * ~4% of sites are brand-new installation lots inside the window
   NOTE: only runs against an EMPTY sm.Meter table.
   ===================================================================================== */
USE [SmartMeterDb];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
CREATE OR ALTER PROCEDURE sm.usp_GenerateDemoData
    @Meters int  = 300000,
    @Days   int  = 30,
    @End    date = NULL
WITH EXECUTE AS OWNER   -- DDL (partitions / staging / generation swap) runs as owner; callers need EXECUTE only
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @End IS NULL SET @End = DATEADD(day, -1, CAST(SYSUTCDATETIME() AS date));
    IF @Days NOT BETWEEN 7 AND 31 THROW 50020, N'@Days must be between 7 and 31.', 1;
    IF @Meters < 1 THROW 50022, N'@Meters must be positive.', 1;
    IF EXISTS (SELECT 1 FROM sm.Meter) THROW 50021, N'sm.Meter is not empty - demo data is only generated into an empty database.', 1;

    DECLARE @Start date = DATEADD(day, -(@Days - 1), @End),
            @Sites int  = GREATEST(1, @Meters / 22),
            @t0 datetime2(3) = SYSUTCDATETIME(),
            @msg nvarchar(400);

    -- ------------------------------------------------------------ dimensions
    INSERT INTO sm.Nsp (Name)
    SELECT v.Name FROM (VALUES (N'Jio'), (N'Airtel'), (N'Vodafone'), (N'BSNL')) v (Name)
    WHERE NOT EXISTS (SELECT 1 FROM sm.Nsp n WHERE n.Name = v.Name);

    CREATE TABLE #Mfr (Ord int PRIMARY KEY, Name nvarchar(100), ManufacturerId smallint);
    INSERT INTO #Mfr (Ord, Name) VALUES (1, N'Apex Meters'), (2, N'Voltaris'), (3, N'GridSense'),
                                        (4, N'Nova Energy'), (5, N'Orion Metering'), (6, N'Kestrel Power');
    INSERT INTO sm.Manufacturer (Name)
    SELECT f.Name FROM #Mfr f WHERE NOT EXISTS (SELECT 1 FROM sm.Manufacturer m WHERE m.Name = f.Name) ORDER BY f.Ord;
    UPDATE f SET ManufacturerId = m.ManufacturerId FROM #Mfr f JOIN sm.Manufacturer m ON m.Name = f.Name;

    CREATE TABLE #Zone (Ord int PRIMARY KEY, Name nvarchar(100), Lon float, Lat float, SigmaKm float,
                        Weight float, Cum float NULL, ZoneId smallint NULL);
    INSERT INTO #Zone (Ord, Name, Lon, Lat, SigmaKm, Weight) VALUES
        ( 1, N'Delhi North',       77.205, 28.715, 4.5, 1.2),
        ( 2, N'Delhi South',       77.215, 28.530, 5.0, 1.3),
        ( 3, N'Delhi East',        77.300, 28.630, 3.5, 0.9),
        ( 4, N'Delhi West',        77.080, 28.650, 4.0, 1.0),
        ( 5, N'Mumbai Western',    72.865, 19.120, 2.5, 1.1),
        ( 6, N'Mumbai Island',     72.840, 18.990, 1.6, 0.7),
        ( 7, N'Thane',             72.985, 19.210, 2.8, 0.7),
        ( 8, N'Bengaluru North',   77.595, 13.045, 4.0, 0.8),
        ( 9, N'Bengaluru South',   77.585, 12.905, 4.0, 0.9),
        (10, N'Bengaluru East',    77.700, 12.970, 3.5, 0.7),
        (11, N'Hyderabad Central', 78.475, 17.390, 3.5, 0.8),
        (12, N'Cyberabad',         78.380, 17.450, 3.5, 0.7);
    UPDATE z SET Cum = c.Cum
    FROM #Zone z JOIN (SELECT Ord, SUM(Weight) OVER (ORDER BY Ord) / SUM(Weight) OVER () AS Cum FROM #Zone) c ON c.Ord = z.Ord;
    INSERT INTO sm.Zone (Name)
    SELECT z.Name FROM #Zone z WHERE NOT EXISTS (SELECT 1 FROM sm.Zone s WHERE s.Name = z.Name) ORDER BY z.Ord;
    UPDATE z SET ZoneId = s.ZoneId FROM #Zone z JOIN sm.Zone s ON s.Name = z.Name;

    -- ------------------------------------------------------------ sites (random draws materialised first)
    SELECT g.value AS SiteNo,
           RAND(CHECKSUM(NEWID()))                       AS Rz,
           RAND(CHECKSUM(NEWID())) * 0.999998 + 0.000001 AS U1,
           RAND(CHECKSUM(NEWID()))                       AS U2,
           RAND(CHECKSUM(NEWID()))                       AS RType,
           RAND(CHECKSUM(NEWID())) * 0.999998 + 0.000001 AS RSize,
           RAND(CHECKSUM(NEWID()))                       AS RNsp,
           RAND(CHECKSUM(NEWID()))                       AS RMfr,
           RAND(CHECKSUM(NEWID()))                       AS RInst,
           RAND(CHECKSUM(NEWID()))                       AS RFlag,
           RAND(CHECKSUM(NEWID()))                       AS RCov,
           RAND(CHECKSUM(NEWID()))                       AS RCovNsp
    INTO #SiteRand
    FROM GENERATE_SERIES(1, @Sites) AS g;

    SELECT r.SiteNo, z.ZoneId, z.Name AS ZoneName,
           z.Lat + (SQRT(-2 * LOG(r.U1)) * COS(2 * PI() * r.U2) * z.SigmaKm) / 111.0 AS Lat,
           z.Lon + (SQRT(-2 * LOG(r.U1)) * SIN(2 * PI() * r.U2) * z.SigmaKm) / (111.0 * COS(RADIANS(z.Lat))) AS Lon,
           CAST(IIF(r.RType < 0.72, 1, 0) AS bit) AS IsTower,
           CASE WHEN r.RType < 0.72 THEN GREATEST(4, LEAST(600, CAST(CEILING(-LOG(r.RSize) * 28) AS int)))
                ELSE GREATEST(1, LEAST(40, CAST(CEILING(-LOG(r.RSize) * 7) AS int))) END AS MeterCount,
           CASE WHEN r.RNsp < 0.40 THEN N'Jio' WHEN r.RNsp < 0.70 THEN N'Airtel'
                WHEN r.RNsp < 0.90 THEN N'Vodafone' ELSE N'BSNL' END AS DomNsp,
           CAST(FLOOR(r.RMfr * 6) + 1 AS int) AS DomMfr,
           CASE WHEN r.RInst < 0.04 THEN DATEADD(day, -CAST(FLOOR(r.RInst / 0.04 * (@Days - 3)) AS int), @End)
                ELSE DATEADD(day, -(@Days + CAST(FLOOR(((r.RInst - 0.04) / 0.96) * 1400) AS int)), @End) END AS InstallBase,
           CAST(IIF(r.RFlag < 0.012, 1, 0) AS bit) AS DeadSite,
           CASE WHEN r.RCov < 0.12 THEN
                CASE WHEN r.RCovNsp < 0.25 THEN N'Jio' WHEN r.RCovNsp < 0.50 THEN N'Airtel'
                     WHEN r.RCovNsp < 0.80 THEN N'Vodafone' ELSE N'BSNL' END END AS WeakNsp
    INTO #Site
    FROM #SiteRand r
    CROSS APPLY (SELECT TOP (1) c.* FROM #Zone c WHERE c.Cum >= r.Rz ORDER BY c.Cum) z;

    -- ------------------------------------------------------------ outage pockets
    SELECT p.PocketId, z.Lon + p.DLon AS Lon, z.Lat + p.DLat AS Lat, p.RadiusKm, p.Nsp, p.Factor,
           DATEADD(day, -(p.DaysAgoEnd + p.DaysLen - 1), @End) AS FromDate,
           DATEADD(day, -p.DaysAgoEnd, @End) AS ToDate
    INTO #Pocket
    FROM (VALUES
        (1, N'Delhi South',        0.010, -0.012, 1.4,  6, 0, NULL,        0.10),
        (2, N'Delhi East',        -0.008,  0.006, 1.1,  3, 0, N'Vodafone', 0.05),
        (3, N'Mumbai Western',     0.004,  0.010, 1.2, 10, 0, NULL,        0.20),
        (4, N'Bengaluru South',   -0.015,  0.008, 1.6,  4, 0, N'Airtel',   0.08),
        (5, N'Hyderabad Central',  0.006, -0.004, 1.2, 30, 0, N'BSNL',     0.15),
        (6, N'Thane',              0.000,  0.000, 1.0,  2, 0, NULL,        0.05),
        (7, N'Delhi North',        0.020,  0.015, 1.3, 10, 5, NULL,        0.25),
        (8, N'Cyberabad',         -0.010,  0.010, 1.5,  8, 0, N'Jio',      0.12)
    ) AS p (PocketId, ZoneName, DLon, DLat, RadiusKm, DaysLen, DaysAgoEnd, Nsp, Factor)
    JOIN #Zone z ON z.Name = p.ZoneName;

    -- ------------------------------------------------------------ meters
    SELECT s.SiteNo, u.value AS UnitNo,
           RAND(CHECKSUM(NEWID())) AS Rn,  RAND(CHECKSUM(NEWID())) AS Rn2, RAND(CHECKSUM(NEWID())) AS Rm,
           RAND(CHECKSUM(NEWID())) AS Rj1, RAND(CHECKSUM(NEWID())) AS Rj2, RAND(CHECKSUM(NEWID())) AS Ri,
           RAND(CHECKSUM(NEWID())) AS Rrel, RAND(CHECKSUM(NEWID())) AS Rrel2
    INTO #MeterRand
    FROM #Site s
    CROSS APPLY GENERATE_SERIES(1, s.MeterCount) AS u;

    ;WITH n AS (
        SELECT r.*, s.ZoneId, s.ZoneName, s.Lat AS SLat, s.Lon AS SLon, s.IsTower, s.DomNsp, s.DomMfr,
               s.InstallBase, s.DeadSite, s.WeakNsp,
               ROW_NUMBER() OVER (ORDER BY r.SiteNo, r.UnitNo) AS N
        FROM #MeterRand r
        JOIN #Site s ON s.SiteNo = r.SiteNo
    )
    SELECT n.N,
           CAST(CONCAT('SM', RIGHT(CONCAT('000000000', n.N), 9)) AS varchar(32)) AS MeterId,
           CAST(CASE WHEN n.IsTower = 1 THEN CONCAT('BLD-', RIGHT(CONCAT('0000000', n.SiteNo), 7)) END AS varchar(64)) AS BuildingId,
           n.SLat + (n.Rj1 - 0.5) * IIF(n.IsTower = 1, 0.00025, 0.0032) AS Lat,
           n.SLon + (n.Rj2 - 0.5) * IIF(n.IsTower = 1, 0.00025, 0.0032) AS Lon,
           CAST(CASE WHEN n.IsTower = 1
                     THEN CONCAT(N'Flat ', 101 + ((n.UnitNo - 1) % 40) + 100 * (((n.UnitNo - 1) / 40) % 30),
                                 N', Tower ', CHAR(65 + ((n.UnitNo - 1) / 1200) % 26), N', Residency ', n.SiteNo, N', ', n.ZoneName)
                     ELSE CONCAT(N'House ', n.UnitNo, N', Lane ', 1 + n.SiteNo % 97, N', Sector ', 1 + n.SiteNo % 60, N', ', n.ZoneName)
                END AS nvarchar(400)) AS Address,
           n.ZoneId, n.SiteNo, n.DeadSite, n.WeakNsp, n.Rrel, n.Rrel2,
           CASE WHEN n.Rn < 0.68 THEN n.DomNsp
                ELSE CASE WHEN n.Rn2 < 0.40 THEN N'Jio' WHEN n.Rn2 < 0.70 THEN N'Airtel'
                          WHEN n.Rn2 < 0.90 THEN N'Vodafone' ELSE N'BSNL' END END AS Nsp,
           CASE WHEN n.Rm < 0.85 THEN n.DomMfr ELSE 1 + CAST(FLOOR(n.Rm * 1000) AS int) % 6 END AS Mfr,
           LEAST(@End, DATEADD(day, CAST(FLOOR(n.Ri * 9) AS int) - 4, n.InstallBase)) AS InstallationDate
    INTO #M1
    FROM n
    WHERE n.N <= @Meters;

    SELECT m.*,
           CASE WHEN m.DeadSite = 1 OR m.Rrel < 0.02 THEN 0.0
                WHEN m.WeakNsp IS NOT NULL AND m.Nsp = m.WeakNsp THEN 0.12
                WHEN m.Rrel < 0.075 THEN 0.30 + 0.45 * m.Rrel2
                ELSE (CASE m.Nsp WHEN N'Jio' THEN 0.998 WHEN N'Airtel' THEN 0.996
                                 WHEN N'Vodafone' THEN 0.985 ELSE 0.965 END) * IIF(m.Mfr = 5, 0.97, 1.0)
           END AS POk,
           pk.PocketId, pk.Factor AS PocketFactor, pk.FromDate AS PocketFrom, pk.ToDate AS PocketTo,
           CAST(NULL AS int) AS MeterSk
    INTO #M
    FROM #M1 m
    OUTER APPLY (SELECT TOP (1) p.* FROM #Pocket p
                 WHERE (p.Nsp IS NULL OR p.Nsp = m.Nsp)
                   AND SQRT(SQUARE((m.Lon - p.Lon) * 111.0 * COS(RADIANS(p.Lat))) + SQUARE((m.Lat - p.Lat) * 111.0)) <= p.RadiusKm
                 ORDER BY p.PocketId) pk;
    CREATE UNIQUE CLUSTERED INDEX CX_M ON #M (MeterId);

    INSERT INTO sm.Meter (MeterId, BuildingId, Latitude, Longitude, Address, ZoneId, NspId, ManufacturerId, InstallationDate, DtId)
    SELECT m.MeterId, m.BuildingId, ROUND(m.Lat, 7), ROUND(m.Lon, 7), m.Address, m.ZoneId, n.NspId, f.ManufacturerId,
           m.InstallationDate, CONCAT('DT-', m.ZoneId, '-', RIGHT(CONCAT('000000', m.SiteNo / 6), 6))
    FROM #M m
    JOIN sm.Nsp n ON n.Name = m.Nsp
    JOIN #Mfr f ON f.Ord = m.Mfr
    ORDER BY m.N;

    UPDATE m SET MeterSk = s.MeterSk FROM #M m JOIN sm.Meter s ON s.MeterId = m.MeterId;
    SET @msg = CONCAT(N'demo: ', @Meters, N' meters in ', @Sites, N' sites generated in ', DATEDIFF(second, @t0, SYSUTCDATETIME()), N' s');
    RAISERROR (@msg, 0, 1) WITH NOWAIT;

    -- ------------------------------------------------------------ daily communication logs
    EXEC sm.usp_EnsureCommLogPartitions @Start, @End;
    DECLARE @d date = @Start;
    WHILE @d <= @End
    BEGIN
        INSERT INTO sm.CommLog WITH (TABLOCK) (LogDate, MeterSk, IsCommunicating)
        SELECT @d, m.MeterSk,
               CAST(IIF(RAND(CHECKSUM(NEWID()))
                        < m.POk * IIF(m.PocketId IS NOT NULL AND @d BETWEEN m.PocketFrom AND m.PocketTo, m.PocketFactor, 1.0)
                                * IIF(@d = DATEADD(day, -17, @End), 0.96, 1.0),          -- MDM head-end hiccup day
                        1, 0) AS bit)
        FROM #M m
        WHERE m.InstallationDate <= @d
          AND RAND(CHECKSUM(NEWID())) > 0.004;                                            -- missing records
        EXEC sm.usp_RecomputeDayStats @d;
        SET @d = DATEADD(day, 1, @d);
    END

    SET @msg = CONCAT(N'demo: logs ', CONVERT(char(10), @Start, 23), N' .. ', CONVERT(char(10), @End, 23),
                      N' generated in ', DATEDIFF(second, @t0, SYSUTCDATETIME()), N' s');
    RAISERROR (@msg, 0, 1) WITH NOWAIT;
END
GO
