/* =====================================================================================
   01_schema.sql : schemas, dimensions, CIS master, partitioned MDM log, platform state.
   Idempotent - safe to re-run.
   ===================================================================================== */
USE [SmartMeterDb];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF SCHEMA_ID(N'sm')    IS NULL EXEC (N'CREATE SCHEMA sm AUTHORIZATION dbo');     -- platform objects
IF SCHEMA_ID(N'stage') IS NULL EXEC (N'CREATE SCHEMA stage AUTHORIZATION dbo');  -- per-job bulk-copy staging heaps
GO

/* ---------------------------------------------------------------- dimensions
   Small-int surrogate keys keep the 15M-row fact and summary tables narrow and make
   GROUP BY cheap. Names are unique under the (case-insensitive) database collation.   */
IF OBJECT_ID(N'sm.Zone') IS NULL
CREATE TABLE sm.Zone (
    ZoneId smallint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Zone PRIMARY KEY,
    Name   nvarchar(100) NOT NULL CONSTRAINT UQ_Zone_Name UNIQUE
);
IF OBJECT_ID(N'sm.Nsp') IS NULL
CREATE TABLE sm.Nsp (
    NspId smallint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Nsp PRIMARY KEY,
    Name  nvarchar(100) NOT NULL CONSTRAINT UQ_Nsp_Name UNIQUE
);
IF OBJECT_ID(N'sm.Manufacturer') IS NULL
CREATE TABLE sm.Manufacturer (
    ManufacturerId smallint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Manufacturer PRIMARY KEY,
    Name           nvarchar(100) NOT NULL CONSTRAINT UQ_Manufacturer_Name UNIQUE
);
GO

/* ---------------------------------------------------------------- CIS customer master
   * MeterSk     : compact int surrogate used by the 465M-row log table.
   * Geom        : persisted geometry Point (SRID 4326) + spatial index for GIS work.
   * BuildingKey : grouping key for map bubbles. Real BuildingId when present; otherwise a
                   ~33 m grid cell ('G:<y>:<x>') so non-building regions still cluster into
                   "virtual buildings"; 'U:unlocated' when there are no coordinates either.   */
IF OBJECT_ID(N'sm.Meter') IS NULL
CREATE TABLE sm.Meter (
    MeterSk          int IDENTITY(1, 1) NOT NULL,
    MeterId          varchar(32)    NOT NULL,
    BuildingId       varchar(64)    NULL,
    Latitude         float          NULL,
    Longitude        float          NULL,
    Address          nvarchar(400)  NULL,
    ZoneId           smallint       NOT NULL,
    NspId            smallint       NOT NULL,
    ManufacturerId   smallint       NOT NULL,
    InstallationDate date           NULL,
    DtId             varchar(64)    NULL,          -- Distribution Transformer (future loss mapping)
    UpdatedAt        datetime2(0)   NOT NULL CONSTRAINT DF_Meter_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    BuildingKey AS CAST(COALESCE('B:' + BuildingId,
                                 CASE WHEN Latitude IS NOT NULL AND Longitude IS NOT NULL
                                      THEN CONCAT('G:', CAST(FLOOR(Latitude / 0.0003) AS int), ':',
                                                        CAST(FLOOR(Longitude / 0.0003) AS int)) END,
                                 'U:unlocated') AS varchar(72)) PERSISTED NOT NULL,
    Geom AS (CASE WHEN Latitude IS NOT NULL AND Longitude IS NOT NULL
                  THEN geometry::Point(Longitude, Latitude, 4326) END) PERSISTED,
    CONSTRAINT PK_Meter PRIMARY KEY CLUSTERED (MeterSk) WITH (DATA_COMPRESSION = PAGE),
    CONSTRAINT UQ_Meter_MeterId UNIQUE NONCLUSTERED (MeterId) WITH (DATA_COMPRESSION = PAGE),
    CONSTRAINT FK_Meter_Zone         FOREIGN KEY (ZoneId)         REFERENCES sm.Zone (ZoneId),
    CONSTRAINT FK_Meter_Nsp          FOREIGN KEY (NspId)          REFERENCES sm.Nsp (NspId),
    CONSTRAINT FK_Meter_Manufacturer FOREIGN KEY (ManufacturerId) REFERENCES sm.Manufacturer (ManufacturerId),
    CONSTRAINT CK_Meter_Latitude  CHECK (Latitude  BETWEEN -90  AND 90),
    CONSTRAINT CK_Meter_Longitude CHECK (Longitude BETWEEN -180 AND 180),
    CONSTRAINT CK_Meter_LatLonPair CHECK ((Latitude IS NULL AND Longitude IS NULL) OR (Latitude IS NOT NULL AND Longitude IS NOT NULL))
);
GO
-- Composite filter index (Zone, NSP, Manufacturer, InstallationDate, BuildingId)
IF INDEXPROPERTY(OBJECT_ID(N'sm.Meter'), N'IX_Meter_Filters', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX IX_Meter_Filters ON sm.Meter (ZoneId, NspId, ManufacturerId, InstallationDate, BuildingKey)
        WITH (DATA_COMPRESSION = PAGE);
IF INDEXPROPERTY(OBJECT_ID(N'sm.Meter'), N'IX_Meter_BuildingKey', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX IX_Meter_BuildingKey ON sm.Meter (BuildingKey) INCLUDE (MeterId, Address)
        WITH (DATA_COMPRESSION = PAGE);
IF INDEXPROPERTY(OBJECT_ID(N'sm.Meter'), N'IX_Meter_DtId', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX IX_Meter_DtId ON sm.Meter (DtId) WHERE DtId IS NOT NULL;
-- Spatial index (lasso / polygon containment, ad-hoc GIS)
IF INDEXPROPERTY(OBJECT_ID(N'sm.Meter'), N'SIX_Meter_Geom', 'IndexID') IS NULL
    CREATE SPATIAL INDEX SIX_Meter_Geom ON sm.Meter (Geom)
        USING GEOMETRY_AUTO_GRID WITH (BOUNDING_BOX = (-180, -90, 180, 90), CELLS_PER_OBJECT = 16);
GO

/* ---------------------------------------------------------------- MDM communication log
   Rolling 30/31 days: 15M meters x 31 days ~= 465M rows.
   * RANGE RIGHT partition function on LogDate, one partition per day (boundaries are added by
     sm.usp_EnsureCommLogPartitions); retention = TRUNCATE ... WITH (PARTITIONS) + MERGE RANGE.
   * Clustered COLUMNSTORE: ~1-2 bytes/row compressed and batch-mode scans for the refresh.
   * Unique (MeterSk, LogDate) B-tree for the single-meter 30-day timeline and upserts.
   No FK to sm.Meter on purpose: ingestion resolves MeterSk through a join, so integrity is
   guaranteed by construction and bulk loads avoid 15M per-row FK probes.                     */
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'PF_CommLogDate')
    CREATE PARTITION FUNCTION PF_CommLogDate (date) AS RANGE RIGHT FOR VALUES ();
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'PS_CommLogDate')
    CREATE PARTITION SCHEME PS_CommLogDate AS PARTITION PF_CommLogDate ALL TO ([PRIMARY]);
GO
IF OBJECT_ID(N'sm.CommLog') IS NULL
BEGIN
    CREATE TABLE sm.CommLog (
        LogDate         date NOT NULL,
        MeterSk         int  NOT NULL,
        IsCommunicating bit  NOT NULL
    ) ON PS_CommLogDate (LogDate);
    CREATE CLUSTERED COLUMNSTORE INDEX CCI_CommLog ON sm.CommLog ON PS_CommLogDate (LogDate);
    CREATE UNIQUE NONCLUSTERED INDEX UX_CommLog_Meter_Date ON sm.CommLog (MeterSk, LogDate)
        INCLUDE (IsCommunicating) WITH (DATA_COMPRESSION = PAGE) ON PS_CommLogDate (LogDate);
END
GO

-- Per-day load statistics (maintained by ingestion): drives window detection + data quality.
IF OBJECT_ID(N'sm.CommLogDayStats') IS NULL
CREATE TABLE sm.CommLogDayStats (
    LogDate            date      NOT NULL CONSTRAINT PK_CommLogDayStats PRIMARY KEY,
    [RowCount]         bigint    NOT NULL,
    CommunicatingCount bigint    NOT NULL,
    LoadedAt           datetime2(0) NOT NULL CONSTRAINT DF_CommLogDayStats_LoadedAt DEFAULT (SYSUTCDATETIME())
);
GO

/* ---------------------------------------------------------------- platform state (singleton)
   DataVersion is bumped by every analytics refresh and is part of every API cache key, so
   cached entries for an older generation are never served (they simply expire).           */
IF OBJECT_ID(N'sm.AnalyticsState') IS NULL
BEGIN
    CREATE TABLE sm.AnalyticsState (
        Singleton        bit          NOT NULL CONSTRAINT PK_AnalyticsState PRIMARY KEY
                                      CONSTRAINT DF_AnalyticsState_Singleton DEFAULT (1)
                                      CONSTRAINT CK_AnalyticsState_Singleton CHECK (Singleton = 1),
        ConfigWindowDays tinyint      NOT NULL CONSTRAINT DF_AnalyticsState_WindowDays DEFAULT (30)
                                      CONSTRAINT CK_AnalyticsState_WindowDays CHECK (ConfigWindowDays BETWEEN 7 AND 31),
        ConfigAsOfDate   date         NULL,           -- pin the window end; NULL = latest loaded day
        RetentionDays    smallint     NOT NULL CONSTRAINT DF_AnalyticsState_Retention DEFAULT (45)
                                      CONSTRAINT CK_AnalyticsState_Retention CHECK (RetentionDays >= 31),
        WindowDays       tinyint      NULL,
        WindowStart      date         NULL,
        WindowEnd        date         NULL,
        DataVersion      bigint       NOT NULL CONSTRAINT DF_AnalyticsState_DataVersion DEFAULT (0),
        RefreshStartedAt datetime2(3) NULL,
        RefreshedAt      datetime2(3) NULL,
        RefreshMs        int          NULL,
        Stats            nvarchar(max) NULL CONSTRAINT CK_AnalyticsState_StatsJson CHECK (Stats IS NULL OR ISJSON(Stats) = 1)
    );
    INSERT INTO sm.AnalyticsState (Singleton) VALUES (1);
END
GO

-- Map resolution pyramid used for server-side clustering. Zoom >= 15 renders buildings.
IF OBJECT_ID(N'sm.GridLevel') IS NULL
BEGIN
    CREATE TABLE sm.GridLevel (
        Level   tinyint NOT NULL CONSTRAINT PK_GridLevel PRIMARY KEY,
        CellDeg float   NOT NULL CONSTRAINT CK_GridLevel_CellDeg CHECK (CellDeg > 0),
        MinZoom tinyint NOT NULL,
        MaxZoom tinyint NOT NULL
    );
    INSERT INTO sm.GridLevel (Level, CellDeg, MinZoom, MaxZoom) VALUES
        (1, 1.0,     0,  6),
        (2, 0.25,    7,  8),
        (3, 0.05,    9, 10),
        (4, 0.0125, 11, 12),
        (5, 0.003,  13, 14);
END
GO

/* ---------------------------------------------------------------- ingestion tracking */
IF OBJECT_ID(N'sm.IngestionJob') IS NULL
CREATE TABLE sm.IngestionJob (
    JobId        uniqueidentifier NOT NULL CONSTRAINT PK_IngestionJob PRIMARY KEY NONCLUSTERED,
    Kind         varchar(8)    NOT NULL CONSTRAINT CK_IngestionJob_Kind CHECK (Kind IN ('cis', 'mdm')),
    FileName     nvarchar(260) NOT NULL,
    FileSize     bigint        NOT NULL,
    Status       varchar(16)   NOT NULL CONSTRAINT DF_IngestionJob_Status DEFAULT ('uploading')
                 CONSTRAINT CK_IngestionJob_Status CHECK (Status IN ('uploading','queued','parsing','merging','completed','failed')),
    Message      nvarchar(2000) NULL,
    ProgressPct  decimal(5, 2) NOT NULL CONSTRAINT DF_IngestionJob_Progress DEFAULT (0),
    RowsRead     bigint NOT NULL CONSTRAINT DF_IngestionJob_RowsRead DEFAULT (0),
    RowsValid    bigint NOT NULL CONSTRAINT DF_IngestionJob_RowsValid DEFAULT (0),
    RowsRejected bigint NOT NULL CONSTRAINT DF_IngestionJob_RowsRejected DEFAULT (0),
    RowsInserted bigint NOT NULL CONSTRAINT DF_IngestionJob_RowsInserted DEFAULT (0),
    RowsUpdated  bigint NOT NULL CONSTRAINT DF_IngestionJob_RowsUpdated DEFAULT (0),
    CreatedBy    nvarchar(256) NULL,
    CreatedAt    datetime2(3)  NOT NULL CONSTRAINT DF_IngestionJob_CreatedAt DEFAULT (SYSUTCDATETIME()),
    StartedAt    datetime2(3)  NULL,
    FinishedAt   datetime2(3)  NULL
);
IF INDEXPROPERTY(OBJECT_ID(N'sm.IngestionJob'), N'CIX_IngestionJob_CreatedAt', 'IndexID') IS NULL
    CREATE CLUSTERED INDEX CIX_IngestionJob_CreatedAt ON sm.IngestionJob (CreatedAt DESC);
IF OBJECT_ID(N'sm.IngestionError') IS NULL
CREATE TABLE sm.IngestionError (
    JobId      uniqueidentifier NOT NULL,
    RowNo      bigint           NOT NULL,
    ColumnName nvarchar(128)    NULL,
    RawValue   nvarchar(400)    NULL,
    Message    nvarchar(400)    NOT NULL,
    INDEX CIX_IngestionError_Job CLUSTERED (JobId, RowNo)
);
GO
