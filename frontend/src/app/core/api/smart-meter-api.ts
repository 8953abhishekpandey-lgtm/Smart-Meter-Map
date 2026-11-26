import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AppConfigService } from '../config/app-config';
import {
  BubblesDto, BuildingDetailDto, BuildingListItem, BuildingMeterDto, BuildingSort, ConsumptionSummaryDto, FailureClustersDto,
  FilterOptionsDto, FleetQuery, IngestionJobDto, IngestionKind, WatchFolderStatusDto, KpiDto, MetaDto, MeterDetailDto, MeterSearchItem, MeterSort,
  MeterStatusFilter, MetricMode, PagedResult, PolygonStatsDto, SimSwitchDto, TrendDto, UploadSessionDto, ZoneSummaryDto,
} from './models';

/** Serialises the global filters; multi-selects become repeated keys (zone=1&zone=4). */
export function fleetParams(q: FleetQuery, extra: Record<string, string | number | boolean | null | undefined> = {}): HttpParams {
  let p = new HttpParams();
  for (const z of q.zones) p = p.append('zone', z);
  for (const n of q.nsps) p = p.append('nsp', n);
  for (const m of q.mfrs) p = p.append('mfr', m);
  if (q.installedFrom) p = p.set('installedFrom', q.installedFrom);
  if (q.installedTo) p = p.set('installedTo', q.installedTo);
  if (q.health !== 'all') p = p.set('health', q.health);
  if (q.date) p = p.set('date', q.date);
  if (q.metric !== 'day') p = p.set('metric', q.metric);
  if (q.metric === 'window' && q.from) p = p.set('from', q.from);
  if (q.metric === 'window' && q.to) p = p.set('to', q.to);
  for (const [k, v] of Object.entries(extra)) if (v !== null && v !== undefined && v !== '') p = p.set(k, String(v));
  return p;
}

@Injectable({ providedIn: 'root' })
export class SmartMeterApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(AppConfigService);

  url(path: string): string {
    return `${this.config.value.apiBaseUrl}/api/v1${path}`;
  }

  meta(): Observable<MetaDto> { return this.http.get<MetaDto>(this.url('/meta')); }

  kpis(q: FleetQuery): Observable<KpiDto> { return this.http.get<KpiDto>(this.url('/dashboard/kpis'), { params: fleetParams(q) }); }

  trend(q: FleetQuery): Observable<TrendDto> { return this.http.get<TrendDto>(this.url('/dashboard/trend'), { params: fleetParams(q) }); }

  filterOptions(q: FleetQuery): Observable<FilterOptionsDto> {
    return this.http.get<FilterOptionsDto>(this.url('/dashboard/filter-options'), { params: fleetParams(q) });
  }

  bubbles(q: FleetQuery, bbox: number[], zoom: number): Observable<BubblesDto> {
    return this.http.get<BubblesDto>(this.url('/map/bubbles'), {
      params: fleetParams(q, { bbox: bbox.map((v) => v.toFixed(6)).join(','), zoom }),
    });
  }

  zones(q: FleetQuery): Observable<ZoneSummaryDto[]> { return this.http.get<ZoneSummaryDto[]>(this.url('/zones'), { params: fleetParams(q) }); }

  zoneBuildings(zoneId: number, q: FleetQuery, o: { search?: string; sort: BuildingSort; page: number; pageSize: number }): Observable<PagedResult<BuildingListItem>> {
    return this.http.get<PagedResult<BuildingListItem>>(this.url(`/zones/${zoneId}/buildings`), {
      params: fleetParams(q, { search: o.search, sort: o.sort, page: o.page, pageSize: o.pageSize }),
    });
  }

  /** One day, or (metric 'window') the average day of from..to. */
  building(key: string, date: string | null, range?: { metric: MetricMode; from: string | null; to: string | null }): Observable<BuildingDetailDto> {
    const params: Record<string, string> = {};
    if (date) params['date'] = date;
    if (range?.metric === 'window') {
      params['metric'] = 'window';
      if (range.from) params['from'] = range.from;
      if (range.to) params['to'] = range.to;
    }
    return this.http.get<BuildingDetailDto>(this.url(`/buildings/${encodeURIComponent(key)}`), { params });
  }

  buildingMeters(key: string, o: { date: string | null; search?: string; status: MeterStatusFilter; sort: MeterSort; desc: boolean; page: number; pageSize: number }):
    Observable<PagedResult<BuildingMeterDto>> {
    let params = new HttpParams().set('status', o.status).set('sort', o.sort).set('desc', o.desc).set('page', o.page).set('pageSize', o.pageSize);
    if (o.date) params = params.set('date', o.date);
    if (o.search) params = params.set('search', o.search);
    return this.http.get<PagedResult<BuildingMeterDto>>(this.url(`/buildings/${encodeURIComponent(key)}/meters`), { params });
  }

  buildingExport(key: string, o: { date: string | null; search?: string; status: MeterStatusFilter }): Observable<Blob> {
    let params = new HttpParams().set('status', o.status);
    if (o.date) params = params.set('date', o.date);
    if (o.search) params = params.set('search', o.search);
    return this.http.get(this.url(`/buildings/${encodeURIComponent(key)}/meters/export`), { params, responseType: 'blob' });
  }

  consumption(key: string): Observable<ConsumptionSummaryDto> {
    return this.http.get<ConsumptionSummaryDto>(this.url(`/buildings/${encodeURIComponent(key)}/consumption`));
  }

  searchMeters(q: string, limit = 10): Observable<MeterSearchItem[]> {
    return this.http.get<MeterSearchItem[]>(this.url('/meters/search'), { params: { q, limit } });
  }

  meter(id: string): Observable<MeterDetailDto> { return this.http.get<MeterDetailDto>(this.url(`/meters/${encodeURIComponent(id)}`)); }

  failureClusters(q: FleetQuery, body: { minMeters: number; minFailurePct: number; epsMeters: number; minPoints: number; bbox?: number[] | null; limit: number }):
    Observable<FailureClustersDto> {
    return this.http.post<FailureClustersDto>(this.url('/analytics/failure-clusters'), body, { params: fleetParams(q) });
  }

  polygon(q: FleetQuery, ring: number[][]): Observable<PolygonStatsDto> {
    return this.http.post<PolygonStatsDto>(this.url('/analytics/polygon'), { geometry: { type: 'Polygon', coordinates: [ring] } }, { params: fleetParams(q) });
  }

  simSwitch(q: FleetQuery, o: { minStrongSuccessPct: number; minWeakFailurePct: number; minMetersPerNsp: number; basis: MetricMode; page: number; pageSize: number }):
    Observable<PagedResult<SimSwitchDto>> {
    return this.http.get<PagedResult<SimSwitchDto>>(this.url('/analytics/sim-switch'), { params: fleetParams(q, { ...o }) });
  }

  jobs(take = 50): Observable<IngestionJobDto[]> { return this.http.get<IngestionJobDto[]>(this.url('/ingestion/jobs'), { params: { take } }); }

  createUpload(kind: IngestionKind, fileName: string, fileSize: number): Observable<UploadSessionDto> {
    return this.http.post<UploadSessionDto>(this.url('/ingestion/uploads'), { kind, fileName, fileSize });
  }

  uploadChunk(jobId: string, index: number, chunk: Blob): Observable<unknown> {
    return this.http.put(this.url(`/ingestion/uploads/${jobId}/chunks/${index}`), chunk, { headers: { 'Content-Type': 'application/octet-stream' } });
  }

  completeUpload(jobId: string): Observable<IngestionJobDto> { return this.http.post<IngestionJobDto>(this.url(`/ingestion/uploads/${jobId}/complete`), {}); }

  jobErrors(jobId: string): Observable<Blob> { return this.http.get(this.url(`/ingestion/jobs/${jobId}/errors`), { responseType: 'blob' }); }

  template(kind: IngestionKind): Observable<Blob> { return this.http.get(this.url(`/ingestion/templates/${kind}`), { responseType: 'blob' }); }

  watchFolder(): Observable<WatchFolderStatusDto> { return this.http.get<WatchFolderStatusDto>(this.url('/ingestion/watch-folder')); }

  scanWatchFolder(): Observable<{ jobId: string }> { return this.http.post<{ jobId: string }>(this.url('/ingestion/watch-folder/scan'), {}); }

  refreshAnalytics(): Observable<{ jobId: string }> { return this.http.post<{ jobId: string }>(this.url('/admin/refresh'), {}); }
}

/** Saves a Blob (fetched with the bearer token) as a file. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}
