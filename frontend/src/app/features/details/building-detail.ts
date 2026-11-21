import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { rxResource, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { debounceTime } from 'rxjs';
import { Breakdown, MeterSort, MeterStatusFilter } from '../../core/api/models';
import { SmartMeterApi, saveBlob } from '../../core/api/smart-meter-api';
import { FilterStore } from '../../core/state/filter.store';
import { MetaStore } from '../../core/state/meta.store';
import { BAND_COLORS, HEALTH_LABELS, compact, fmt, longDate, pct, shortDate } from '../../core/ui/format';
import { BandBadge } from '../../shared/band-badge';
import { consumptionOptions, successTrendOptions } from '../../shared/charts';
import { EChart } from '../../shared/echart';
import { Pager } from '../../shared/pager';
import { TimelineStrip } from '../../shared/timeline-strip';

type Tab = 'overview' | 'trend' | 'consumption' | 'meters';

/** Building drill-down (used in the dashboard drawer and the explorer building page). */
@Component({
  selector: 'sm-building-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BandBadge, EChart, Pager, TimelineStrip],
  templateUrl: './building-detail.html',
  styleUrl: './details.scss',
})
export class BuildingDetail {
  readonly buildingKey = input.required<string>();
  readonly meterSelected = output<string>();

  private readonly api = inject(SmartMeterApi);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;
  protected readonly compact = compact;
  protected readonly pct = pct;
  protected readonly shortDate = shortDate;
  protected readonly longDate = longDate;
  protected readonly healthLabels = HEALTH_LABELS;
  protected readonly bandColors = BAND_COLORS;
  protected readonly Math = Math;

  protected readonly tab = signal<Tab>('overview');
  protected readonly search = signal('');
  private readonly searchDebounced = toSignal(toObservable(this.search).pipe(debounceTime(300)), { initialValue: '' });
  protected readonly status = signal<MeterStatusFilter>('all');
  protected readonly sort = signal<MeterSort>('meterId');
  protected readonly desc = signal(false);
  protected readonly page = signal(1);
  protected readonly pageSize = 25;
  protected readonly exporting = signal(false);

  protected readonly detail = rxResource({
    params: () => ({ key: this.buildingKey(), date: this.store.date(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.building(params.key, params.date),
  });

  protected readonly meters = rxResource({
    params: () => this.tab() === 'meters'
      ? { key: this.buildingKey(), date: this.store.date(), search: this.searchDebounced(), status: this.status(), sort: this.sort(),
          desc: this.desc(), page: this.page(), v: this.meta.dataVersion() }
      : undefined,
    stream: ({ params }) => this.api.buildingMeters(params.key, { ...params, pageSize: this.pageSize }),
  });

  protected readonly consumption = rxResource({
    params: () => (this.tab() === 'consumption' ? { key: this.buildingKey(), v: this.meta.dataVersion() } : undefined),
    stream: ({ params }) => this.api.consumption(params.key),
  });

  protected readonly d = computed(() => (this.detail.hasValue() ? this.detail.value() : undefined));
  protected readonly trendOptions = computed(() => successTrendOptions(this.d()?.trend ?? [], this.store.date()));
  protected readonly consumptionChart = computed(() => {
    const c = this.consumption.hasValue() ? this.consumption.value() : undefined;
    return c?.available ? consumptionOptions(c.daily) : null;
  });

  protected setTab(t: Tab): void { this.tab.set(t); }
  protected onSearch(v: string): void { this.search.set(v); this.page.set(1); }
  protected onStatus(v: string): void { this.status.set(v as MeterStatusFilter); this.page.set(1); }
  protected onSort(v: string): void { this.sort.set(v as MeterSort); this.page.set(1); }
  protected toggleDir(): void { this.desc.update((d) => !d); }

  protected pickDay(index: number): void {
    const p = this.d()?.trend[index];
    if (p) this.store.date.set(p.date);
  }

  protected barWidth(b: Breakdown): number { return Math.min(100, Math.max(0, b.failurePct)); }
  protected barColor(b: Breakdown): string {
    const p = Math.round(b.failurePct);
    return p <= 20 ? BAND_COLORS.green : p >= 80 ? BAND_COLORS.red : BAND_COLORS.amber;
  }

  protected export(): void {
    this.exporting.set(true);
    this.api.buildingExport(this.buildingKey(), { date: this.store.date(), search: this.searchDebounced(), status: this.status() }).subscribe({
      next: (blob) => saveBlob(blob, `meters_${this.buildingKey().replace(/[^A-Za-z0-9-]/g, '_')}.csv`),
      complete: () => this.exporting.set(false),
      error: () => this.exporting.set(false),
    });
  }
}
