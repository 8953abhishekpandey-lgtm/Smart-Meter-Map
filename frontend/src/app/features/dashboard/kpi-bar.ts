import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { HealthFilter, KpiDto } from '../../core/api/models';
import { FilterStore } from '../../core/state/filter.store';
import { MetaStore } from '../../core/state/meta.store';
import { PeriodStore } from '../../core/state/period';
import { fmt, longDate, pct } from '../../core/ui/format';

/** KPI stat tiles + health quick-filter chips. */
@Component({
  selector: 'sm-kpi-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="kpis" [class.loading]="loading()">
      <div class="kpi">
        <span class="k">Total meters</span>
        <span class="v">{{ fmt(k()?.totalMeters) }}</span>
        <span class="s">{{ fmt(k()?.expectedMeters) }} {{ metricWindow() ? 'expected per day (avg)' : 'expected on ' + longDate(k()?.date) }}</span>
      </div>
      <div class="kpi">
        <span class="k">Communication success</span>
        <span class="v">{{ fmt(k()?.successCount) }} <small>{{ pct(k()?.successPct) }}</small></span>
        <span class="s">{{ metricWindow() ? 'average day, ' + periods.label() : 'on the selected day' }}</span>
      </div>
      <div class="kpi">
        <span class="k">Communication failure</span>
        <span class="v">{{ fmt(k()?.failureCount) }} <small>{{ pct(k()?.failurePct) }}</small></span>
        <span class="s">{{ metricWindow() ? 'average day, meters not communicating' : 'meters not communicating' }}</span>
      </div>
      <div class="kpi">
        <span class="k">{{ metricWindow() && !periods.period()?.full ? 'Range average' : (meta.window()?.days ?? 30) + '-day average' }}</span>
        <span class="v">{{ pct(k()?.windowSuccessPct) }}</span>
        <span class="s">{{ metricWindow() ? periods.label() : longDate(meta.window()?.start) + ' – ' + longDate(meta.window()?.end) }}</span>
      </div>
    </div>
    <div class="chips" role="radiogroup" aria-label="Health quick filter">
      @for (c of chips(); track c.key) {
        <button type="button" role="radio" class="chip" [class.on]="store.health() === c.key" [attr.aria-checked]="store.health() === c.key"
                (click)="toggle(c.key)" [title]="c.help">
          {{ c.label }} @if (c.count !== null) { <span class="count">{{ fmt(c.count) }}</span> }
        </button>
      }
      @if (k()?.source === 'meter-level') { <span class="source" title="Exact installation-day ranges are answered from meter-level data">meter-level query</span> }
    </div>
  `,
  styleUrl: './dashboard.scss',
})
export class KpiBar {
  readonly kpis = input<KpiDto | undefined>();
  readonly loading = input(false);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;
  protected readonly pct = pct;
  protected readonly longDate = longDate;
  protected readonly k = computed(() => this.kpis());
  protected readonly metricWindow = computed(() => this.k()?.metric === 'window');
  protected readonly periods = inject(PeriodStore);

  protected readonly chips = computed(() => {
    const h = this.k()?.health;
    return [
      { key: 'all' as HealthFilter, label: 'All meters', count: this.k()?.fleetMeters ?? null, help: 'No health filter' },
      { key: 'allDay' as HealthFilter, label: 'All-day comm', count: h?.allDay ?? null, help: 'Communicated on every expected day of the window' },
      { key: 'last7Days' as HealthFilter, label: 'Last 7 days comm', count: h?.last7Days ?? null, help: 'Communicated at least once in the last 7 days' },
      { key: 'todayFail' as HealthFilter, label: 'Fail on selected day', count: h?.todayFail ?? null, help: 'Not communicating on the selected date' },
      { key: 'intermittent' as HealthFilter, label: 'Intermittent', count: h?.intermittent ?? null, help: 'Partial communication history' },
      { key: 'dead' as HealthFilter, label: 'Dead meters', count: h?.dead ?? null, help: 'Zero communication across the whole window' },
    ];
  });

  protected toggle(key: HealthFilter): void {
    this.store.health.set(this.store.health() === key ? 'all' : key);
  }
}
