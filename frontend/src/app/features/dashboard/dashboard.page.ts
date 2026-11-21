import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { FilterStore } from '../../core/state/filter.store';
import { MapCommands } from '../../core/state/map-commands';
import { MetaStore } from '../../core/state/meta.store';
import { BuildingDetail } from '../details/building-detail';
import { MeterDetail } from '../details/meter-detail';
import { FilterBar } from './filter-bar';
import { KpiBar } from './kpi-bar';
import { GisMap } from './map/gis-map';
import { ClustersPanel } from './tools/clusters-panel';
import { LassoPanel } from './tools/lasso-panel';
import { SimSwitchPanel } from './tools/sim-switch-panel';
import { TrendPanel } from './tools/trend-panel';

type ToolTab = 'trend' | 'clusters' | 'sim' | 'lasso';
type Drawer = { kind: 'building'; key: string } | { kind: 'meter'; id: string } | null;

@Component({
  selector: 'sm-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FilterBar, KpiBar, GisMap, TrendPanel, ClustersPanel, SimSwitchPanel, LassoPanel, BuildingDetail, MeterDetail, RouterLink],
  templateUrl: './dashboard.page.html',
  styleUrl: './dashboard.scss',
})
export class DashboardPage {
  private readonly api = inject(SmartMeterApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly commands = inject(MapCommands);

  protected readonly toolTab = signal<ToolTab>('trend');
  protected readonly toolsOpen = signal(true);
  protected readonly drawer = signal<Drawer>(null);

  protected readonly kpis = rxResource({
    params: () => ({ q: this.store.query(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.kpis(params.q),
  });
  protected readonly kpiValue = computed(() => (this.kpis.hasValue() ? this.kpis.value() : undefined));

  constructor() {
    const destroyRef = inject(DestroyRef);
    // URL -> state. Filters are read once on entry (the store is global, so plain navigation keeps the
    // current filters); deep links (?building=, ?meter=, ?focus=minx,miny,maxx,maxy) are followed live.
    const initial = this.route.snapshot.queryParams;
    const filterKeys = ['zone', 'nsp', 'mfr', 'installedFrom', 'installedTo', 'health', 'date', 'metric'];
    if (filterKeys.some((k) => initial[k] !== undefined)) untracked(() => this.store.fromParams(initial));
    const sub = this.route.queryParams.subscribe((p) => untracked(() => this.followDeepLink(p)));
    destroyRef.onDestroy(() => sub.unsubscribe());

    // state -> URL (shareable views)
    effect(() => {
      const params = { ...this.store.toParams(), ...this.drawerParams() };
      untracked(() => void this.router.navigate([], { queryParams: { ...params, focus: null, lon: null, lat: null }, replaceUrl: true }));
    });

    // a finished lasso jumps to its results
    effect(() => {
      if (this.commands.polygon()) untracked(() => { this.toolTab.set('lasso'); this.toolsOpen.set(true); });
    });
  }

  private followDeepLink(p: Record<string, string>): void {
    const current = this.drawer();
    if (p['meter']) {
      if (!(current?.kind === 'meter' && current.id === p['meter'])) this.openMeter(p['meter']);
    } else if (p['building']) {
      if (!(current?.kind === 'building' && current.key === p['building']))
        this.openBuilding(p['building'], p['lon'] ? +p['lon'] : undefined, p['lat'] ? +p['lat'] : undefined);
    }
    if (p['focus']) {
      const e = String(p['focus']).split(',').map(Number);
      if (e.length === 4 && e.every(Number.isFinite)) this.commands.fitExtent(e);
    }
  }

  private readonly drawerParams = computed(() => {
    const d = this.drawer();
    return { building: d?.kind === 'building' ? d.key : null, meter: d?.kind === 'meter' ? d.id : null };
  });

  protected onBuilding(e: { key: string; lon: number; lat: number }): void {
    this.openBuilding(e.key, e.lon, e.lat);
  }

  protected onCluster(id: string): void {
    this.commands.activeCluster.set(id);
    this.toolTab.set('clusters');
    this.toolsOpen.set(true);
  }

  protected focusBuilding(e: { key: string; lon: number; lat: number }): void {
    this.commands.flyTo(e.lon, e.lat, 17);
    this.openBuilding(e.key, e.lon, e.lat);
  }

  protected openBuilding(key: string, lon?: number | null, lat?: number | null): void {
    this.commands.meterPin.set(null);
    this.commands.selectedBuilding.set({ key, lon, lat });
    if (this.drawer()?.kind !== 'building' || (this.drawer() as { key: string }).key !== key) this.drawer.set({ kind: 'building', key });
    if (lon == null || lat == null) {
      this.api.building(key, null).subscribe((b) => {
        if (b.lon != null && b.lat != null) {
          this.commands.selectedBuilding.set({ key, lon: b.lon, lat: b.lat });
          this.commands.flyTo(b.lon, b.lat, 17);
        }
      });
    }
  }

  /** Meter search / deep link: fly to the meter and open its card; global filters are untouched. */
  protected openMeter(id: string): void {
    if (this.drawer()?.kind === 'meter' && (this.drawer() as { id: string }).id === id) return;
    this.drawer.set({ kind: 'meter', id });
    this.api.meter(id).subscribe((m) => {
      if (m.lon != null && m.lat != null) {
        this.commands.meterPin.set({ meterId: m.meterId, lon: m.lon, lat: m.lat });
        this.commands.selectedBuilding.set(null);
        this.commands.flyTo(m.lon, m.lat, 18);
      }
    });
  }

  protected closeDrawer(): void {
    this.drawer.set(null);
    this.commands.selectedBuilding.set(null);
    this.commands.meterPin.set(null);
  }

  protected buildingKeyOf(d: Drawer): string { return d?.kind === 'building' ? d.key : ''; }
  protected meterIdOf(d: Drawer): string { return d?.kind === 'meter' ? d.id : ''; }
}
