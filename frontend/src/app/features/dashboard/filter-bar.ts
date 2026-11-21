import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { FilterStore } from '../../core/state/filter.store';
import { MetaStore } from '../../core/state/meta.store';
import { MultiSelect } from '../../shared/multi-select';
import { MeterSearch } from './meter-search';

/** Cascading multi-selects + installation lot + selected date + metric. */
@Component({
  selector: 'sm-filter-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MultiSelect, MeterSearch],
  template: `
    <div class="bar">
      <sm-multi-select label="Zone" [options]="opts()?.zones ?? []" [(selected)]="store.zones" />
      <sm-multi-select label="NSP" [options]="opts()?.nsps ?? []" [(selected)]="store.nsps" />
      <sm-multi-select label="Manufacturer" [options]="opts()?.manufacturers ?? []" [(selected)]="store.mfrs" />

      <div class="field install" [class.active]="store.installedFrom() || store.installedTo()">
        <span class="label">Installed</span>
        <select (change)="preset($any($event.target).value)" aria-label="Installation lot preset" [value]="presetValue()">
          <option value="">Any time</option><option value="30">Last 30 days</option><option value="90">Last 90 days</option>
          <option value="365">Last 12 months</option><option value="custom">Custom…</option>
        </select>
        @if (presetValue() === 'custom' || store.installedFrom() || store.installedTo()) {
          <input type="date" [value]="store.installedFrom() ?? ''" (change)="store.installedFrom.set($any($event.target).value || null)" aria-label="Installed from" />
          <span class="dash">–</span>
          <input type="date" [value]="store.installedTo() ?? ''" (change)="store.installedTo.set($any($event.target).value || null)" aria-label="Installed to" />
        }
      </div>

      <div class="field">
        <span class="label">Date</span>
        <input type="date" [min]="meta.window()?.start" [max]="meta.window()?.end" [value]="store.date() ?? meta.window()?.end ?? ''"
               (change)="onDate($any($event.target).value)" aria-label="Selected date" />
      </div>

      <div class="segmented" role="radiogroup" aria-label="Metric">
        <button type="button" role="radio" [attr.aria-checked]="store.metric() === 'day'" [class.on]="store.metric() === 'day'" (click)="store.metric.set('day')">Selected day</button>
        <button type="button" role="radio" [attr.aria-checked]="store.metric() === 'window'" [class.on]="store.metric() === 'window'" (click)="store.metric.set('window')">{{ meta.window()?.days ?? 30 }}-day avg</button>
      </div>

      @if (store.activeFilterCount() > 0) {
        <button type="button" class="reset" (click)="store.reset()">Reset filters ({{ store.activeFilterCount() }})</button>
      }
      <sm-meter-search class="search" />
    </div>
  `,
  styleUrl: './dashboard.scss',
})
export class FilterBar {
  private readonly api = inject(SmartMeterApi);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);

  /** Each dimension's options are counted under the other dimensions' selections (cascading). */
  private readonly options = rxResource({
    params: () => ({ q: this.store.dimensionQuery(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.filterOptions(params.q),
  });
  protected readonly opts = computed(() => (this.options.hasValue() ? this.options.value() : undefined));

  protected readonly presetValue = computed(() => {
    const from = this.store.installedFrom();
    if (!from && !this.store.installedTo()) return '';
    const end = this.meta.window()?.end;
    if (end && !this.store.installedTo()) {
      const days = Math.round((Date.parse(end) - Date.parse(from!)) / 86_400_000);
      if ([30, 90, 365].includes(days)) return String(days);
    }
    return 'custom';
  });

  protected preset(v: string): void {
    const end = this.meta.window()?.end;
    if (!v) { this.store.installedFrom.set(null); this.store.installedTo.set(null); return; }
    if (v === 'custom' || !end) return;
    const d = new Date(end + 'T00:00:00');
    d.setDate(d.getDate() - Number(v));
    this.store.installedFrom.set(d.toISOString().slice(0, 10));
    this.store.installedTo.set(null);
  }

  protected onDate(v: string): void {
    const w = this.meta.window();
    this.store.date.set(!v || v === w?.end ? null : v);
  }
}
