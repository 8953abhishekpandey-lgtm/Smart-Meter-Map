import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { SmartMeterApi } from '../../../core/api/smart-meter-api';
import { FilterStore } from '../../../core/state/filter.store';
import { MetaStore } from '../../../core/state/meta.store';
import { fmt, pct, shortDate } from '../../../core/ui/format';
import { successTrendOptions } from '../../../shared/charts';
import { EChart } from '../../../shared/echart';

@Component({
  selector: 'sm-trend-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EChart],
  template: `
    <h3>Fleet communication success · {{ meta.window()?.days ?? 30 }} days</h3>
    <p class="hint">Filtered fleet. Hover for exact counts; click a day to inspect it on the map and in the KPIs.</p>
    <sm-echart [options]="options()" [height]="210" ariaLabel="Fleet daily communication success" (pointClick)="pick($event.dataIndex)" />
    <h4>Weakest days</h4>
    <table class="mini">
      <tbody>
        @for (p of worst(); track p.date) {
          <tr class="click" (click)="store.date.set(p.date)"><td>{{ shortDate(p.date) }}</td><td class="num">{{ fmt(p.communicating) }} / {{ fmt(p.expected) }}</td><td class="num"><b>{{ pct(p.successPct, 2) }}</b></td></tr>
        }
      </tbody>
    </table>
  `,
  styleUrl: './tools.scss',
})
export class TrendPanel {
  private readonly api = inject(SmartMeterApi);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;
  protected readonly pct = pct;
  protected readonly shortDate = shortDate;

  private readonly trend = rxResource({
    params: () => ({ q: { ...this.store.query(), date: null }, v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.trend(params.q),
  });
  private readonly points = computed(() => (this.trend.hasValue() ? this.trend.value().points : []));
  protected readonly options = computed(() => successTrendOptions(this.points(), this.store.date() ?? this.meta.window()?.end ?? null));
  protected readonly worst = computed(() => [...this.points()].sort((a, b) => a.successPct - b.successPct).slice(0, 5));

  protected pick(i: number): void {
    const p = this.points()[i];
    if (p) this.store.date.set(p.date === this.meta.window()?.end ? null : p.date);
  }
}
