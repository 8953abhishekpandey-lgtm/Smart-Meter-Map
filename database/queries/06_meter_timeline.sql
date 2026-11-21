/* Single meter search + 30-day timeline (seeks on UX_CommLog_Meter_Date, one per daily partition). */
USE [SmartMeterDb];
SET NOCOUNT ON;
DECLARE @MeterId varchar(32) = 'SM000000042';

SELECT TOP (10) MeterId, Address FROM sm.Meter WHERE MeterId LIKE 'SM0000004%' ORDER BY MeterId;   -- prefix search

SELECT m.MeterId, m.Address, m.Latitude, m.Longitude, m.BuildingKey, z.Name AS Zone, n.Name AS Nsp, f.Name AS Manufacturer,
       m.InstallationDate, h.HealthClass, h.CommDays, h.EligDays, h.LastCommDate
FROM sm.Meter m
JOIN sm.Zone z ON z.ZoneId = m.ZoneId
JOIN sm.Nsp n ON n.NspId = m.NspId
JOIN sm.Manufacturer f ON f.ManufacturerId = m.ManufacturerId
LEFT JOIN sm.MeterHealth h ON h.MeterSk = m.MeterSk
WHERE m.MeterId = @MeterId;

SELECT CAST(d.value AS date) AS LogDate, l.IsCommunicating
FROM sm.AnalyticsState s
CROSS APPLY (SELECT DATEADD(day, g.value, s.WindowStart) AS value FROM GENERATE_SERIES(0, s.WindowDays - 1) g) d
LEFT JOIN sm.CommLog l
       ON l.MeterSk = (SELECT MeterSk FROM sm.Meter WHERE MeterId = @MeterId)
      AND l.LogDate = d.value
ORDER BY 1;
