// DTOs mirroring the SmartMeter.Api contracts (enums are camelCase strings).

export type HealthFilter = 'all' | 'allDay' | 'last7Days' | 'todayFail' | 'dead' | 'intermittent';
export type MetricMode = 'day' | 'window';
export type HealthBand = 'none' | 'green' | 'amber' | 'red';
export type HealthClass = 'dead' | 'intermittent' | 'allDay' | 'notInstalled';
export type MeterStatusFilter = 'all' | 'communicating' | 'failing' | 'allDay' | 'dead' | 'intermittent';
export type MeterSort = 'meterId' | 'commDays' | 'lastComm' | 'nsp' | 'manufacturer' | 'installation';
export type BuildingSort = 'failureRate' | 'meters' | 'name' | 'failing';

export interface FleetQuery {
  zones: number[];
  nsps: number[];
  mfrs: number[];
  installedFrom: string | null;
  installedTo: string | null;
  health: HealthFilter;
  date: string | null;
  metric: MetricMode;
}

export interface DimensionItem { id: number; name: string; }
export interface GridLevelDto { level: number; cellDeg: number; minZoom: number; maxZoom: number; }

export interface MetaDto {
  window: { start: string; end: string; days: number };
  dataVersion: number;
  refreshedAtUtc: string | null;
  refreshMs: number | null;
  zones: DimensionItem[];
  nsps: DimensionItem[];
  manufacturers: DimensionItem[];
  installedMin: string | null;
  installedMax: string | null;
  gridLevels: GridLevelDto[];
  buildingMinZoom: number;
  bands: { greenMaxFailurePct: number; redMinFailurePct: number };
}

export interface HealthCounts { allDay: number; last7Days: number; todayFail: number; dead: number; intermittent: number; }

export interface KpiDto {
  date: string;
  metric: MetricMode;
  totalMeters: number;
  expectedMeters: number;
  successCount: number;
  successPct: number;
  failureCount: number;
  failurePct: number;
  windowSuccessPct?: number | null;
  health: HealthCounts;
  fleetMeters: number;
  source: 'summary' | 'meter-level';
}

export interface TrendPoint { date: string; expected: number; communicating: number; successPct: number; }
export interface TrendDto { points: TrendPoint[]; }

export interface FilterOption { id: number; name: string; meters: number; }
export interface FilterOptionsDto { zones: FilterOption[]; nsps: FilterOption[]; manufacturers: FilterOption[]; }

export interface BubbleDto {
  id: string;
  kind: 'cell' | 'building';
  lon: number;
  lat: number;
  meters: number;
  expected: number;
  communicating: number;
  failing: number;
  failurePct: number;
  band: HealthBand;
  buildingKey?: string | null;
  name?: string | null;
  address?: string | null;
  extent?: number[] | null;
}

export interface BubblesDto {
  zoom: number;
  mode: 'grid' | 'building';
  level?: number | null;
  cellDeg?: number | null;
  date: string;
  metric: MetricMode;
  truncated: boolean;
  bubbles: BubbleDto[];
}

export interface Breakdown {
  id: number;
  name: string;
  meters: number;
  expected: number;
  success: number;
  failure: number;
  successPct: number;
  failurePct: number;
  windowSuccessPct?: number | null;
}

export interface BuildingDetailDto {
  buildingKey: string;
  buildingId?: string | null;
  isVirtual: boolean;
  name: string;
  address?: string | null;
  zoneId: number;
  zone: string;
  lon?: number | null;
  lat?: number | null;
  totalMeters: number;
  date: string;
  expected: number;
  success: number;
  failure: number;
  successPct: number;
  failurePct: number;
  band: HealthBand;
  windowSuccessPct: number;
  health: HealthCounts;
  byNsp: Breakdown[];
  byManufacturer: Breakdown[];
  trend: TrendPoint[];
}

export interface BuildingMeterDto {
  meterId: string;
  address?: string | null;
  nsp: string;
  manufacturer: string;
  installationDate?: string | null;
  health: HealthClass;
  commDays: number;
  eligDays: number;
  lastCommDate?: string | null;
  communicatedOnDay?: boolean | null;
  timeline: string;
}

export interface PagedResult<T> { items: T[]; page: number; pageSize: number; total: number; totalPages: number; }

export interface ConsumptionSummaryDto {
  available: boolean;
  source: string;
  message?: string | null;
  from: string;
  to: string;
  days: number;
  metersInBuilding: number;
  communicatingMeters: number;
  metersWithData: number;
  totalKwh: number;
  buildingDailyAvgKwh: number;
  perMeterDailyAvgKwh: number;
  partial: boolean;
  daily: { date: string; kwh: number; metersReporting: number }[];
  topConsumers: { meterId: string; totalKwh: number; dailyAvgKwh: number }[];
}

export interface MeterSearchItem { meterId: string; address?: string | null; buildingKey: string; lon?: number | null; lat?: number | null; zone: string; }

export interface TimelineDay { date: string; status: 'communicating' | 'failed' | 'no-record' | 'not-installed'; }

export interface MeterDetailDto {
  meterId: string;
  address?: string | null;
  lat?: number | null;
  lon?: number | null;
  buildingKey: string;
  buildingId?: string | null;
  buildingName?: string | null;
  zoneId: number;
  zone: string;
  nspId: number;
  nsp: string;
  manufacturerId: number;
  manufacturer: string;
  installationDate?: string | null;
  dtId?: string | null;
  health?: HealthClass | null;
  commDays: number;
  eligDays: number;
  commPct: number;
  lastCommDate?: string | null;
  timeline: TimelineDay[];
}

export interface NspFailure { nspId: number; nsp: string; meters: number; expected: number; failing: number; failurePct: number; }

export interface FailureCluster {
  rank: number;
  id: string;
  buildings: number;
  meters: number;
  expected: number;
  failing: number;
  failurePct: number;
  band: HealthBand;
  lon: number;
  lat: number;
  extent: number[];
  hull: number[][];
  nsps: NspFailure[];
  topBuildingKeys: string[];
}

export interface FailureClustersDto {
  date: string;
  metric: MetricMode;
  hotBuildings: number;
  clustersFound: number;
  truncated: boolean;
  elapsedMs: number;
  clusters: FailureCluster[];
}

export interface PolygonStatsDto {
  date: string;
  areaKm2: number;
  approximate: boolean;
  meters: number;
  buildings: number;
  expected: number;
  success: number;
  failure: number;
  successPct: number;
  failurePct: number;
  band: HealthBand;
  health: HealthCounts;
  byNsp: Breakdown[];
  byManufacturer: Breakdown[];
}

export interface SimSwitchDto {
  buildingKey: string;
  name: string;
  address?: string | null;
  zone: string;
  lon?: number | null;
  lat?: number | null;
  strongNspId: number;
  strongNsp: string;
  strongMeters: number;
  strongSuccessPct: number;
  weakNspId: number;
  weakNsp: string;
  weakMeters: number;
  weakFailing: number;
  weakFailurePct: number;
}

export interface ZoneSummaryDto {
  zoneId: number;
  name: string;
  meters: number;
  buildings?: number | null;
  expected: number;
  success: number;
  failure: number;
  successPct: number;
  failurePct: number;
  band: HealthBand;
  health: HealthCounts;
  extent?: number[] | null;
}

export interface BuildingListItem {
  buildingKey: string;
  name: string;
  address?: string | null;
  isVirtual: boolean;
  lon?: number | null;
  lat?: number | null;
  meters: number;
  expected: number;
  success: number;
  failure: number;
  failurePct: number;
  band: HealthBand;
  nspCount: number;
}

export type IngestionKind = 'cis' | 'mdm';
export type JobStatus = 'uploading' | 'queued' | 'parsing' | 'merging' | 'completed' | 'failed';

export interface IngestionJobDto {
  jobId: string;
  kind: IngestionKind;
  fileName: string;
  fileSize: number;
  status: JobStatus;
  message?: string | null;
  progressPct: number;
  rowsRead: number;
  rowsValid: number;
  rowsRejected: number;
  rowsInserted: number;
  rowsUpdated: number;
  createdBy?: string | null;
  createdAt: string;
  startedAt?: string | null;
  finishedAt?: string | null;
}

export interface UploadSessionDto { jobId: string; chunkSize: number; chunkCount: number; }

export interface AuthConfigDto { enabled: boolean; mode: 'None' | 'Dev' | 'Oidc'; authority?: string; clientId?: string; scope?: string; audience?: string; }
