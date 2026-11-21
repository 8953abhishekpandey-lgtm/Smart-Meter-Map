import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MetricMode, SimSwitchDto } from '../../../core/api/models';
import { SmartMeterApi } from '../../../core/api/smart-meter-api';
import { FilterStore } from '../../../core/state/filter.store';
import { MetaStore } from '../../../core/state/meta.store';
import { fmt, pct } from '../../../core/ui/format';
import { Pager } from '../../../shared/pager';

/** Buildings where one NSP is strong and another failing: targeted SIM swaps. */
@Component({
  selector: 'sm-sim-switch-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Pager],
  template: `
    <h3>SIM / NSP switch opportunities</h3>
    <p class="hint">Buildings where a strong NSP and a failing NSP coexist. Swapping the failing SIMs to the strong network should restore communication.</p>
    <form class="form" (submit)="$event.preventDefault()">
      <label>Strong NSP success ≥ (%) <input type="number" min="0" max="100" [value]="strong()" (change)="strong.set(+$any($event.target).value); page.set(1)" /></label>
      <label>Weak NSP failure ≥ (%) <input type="number" min="0" max="100" [value]="weak()" (change)="weak.set(+$any($event.target).value); page.set(1)" /></label>
      <label>Min meters per NSP <input type="number" min="1" [value]="minMeters()" (change)="minMeters.set(+$any($event.target).value); page.set(1)" /></label>
      <label>Basis
        <select [value]="basis()" (change)="basis.set($any($event.target).value); page.set(1)">
          <option value="window">{{ meta.window()?.days ?? 30 }}-day average (recommended)</option><option value="day">Selected day only</option>
        </select>
      </label>
    </form>
    @if (data(); as d) {
      <p class="meta">{{ fmt(d.total) }} buildings · {{ fmt(totalWeak()) }} failing SIMs on this page</p>
      <ul class="list">
        @for (s of d.items; track s.buildingKey + s.weakNspId) {
          <li class="item" (click)="select(s)" tabindex="0" (keydown.enter)="select(s)">
            <div class="row1"><b>{{ s.name }}</b><span class="zone">{{ s.zone }}</span></div>
            <div class="swap">
              <span class="weak">{{ s.weakNsp }} · {{ fmt(s.weakMeters) }} meters · {{ pct(s.weakFailurePct, 0) }} failing</span>
              <span class="arrow" aria-hidden="true">→</span>
              <span class="strong">{{ s.strongNsp }} · {{ fmt(s.strongMeters) }} meters · {{ pct(s.strongSuccessPct, 0) }} ok</span>
            </div>
          </li>
        } @empty { <li class="empty">No switch opportunities at these thresholds.</li> }
      </ul>
      <sm-pager [page]="d.page" [pageSize]="d.pageSize" [total]="d.total" (pageChange)="page.set($event)" />
    } @else if (res.isLoading()) { <p class="meta">Analysing buildings…</p> }
  `,
  styleUrl: './tools.scss',
})
export class SimSwitchPanel {
  readonly buildingSelected = output<{ key: string; lon: number; lat: number }>();
  private readonly api = inject(SmartMeterApi);
  private readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;
  protected readonly pct = pct;

  protected readonly strong = signal(85);
  protected readonly weak = signal(70);
  protected readonly minMeters = signal(3);
  protected readonly basis = signal<MetricMode>('window');
  protected readonly page = signal(1);

  protected readonly res = rxResource({
    params: () => ({ q: this.store.query(), strong: this.strong(), weak: this.weak(), min: this.minMeters(), basis: this.basis(), page: this.page(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.simSwitch(params.q, {
      minStrongSuccessPct: params.strong, minWeakFailurePct: params.weak, minMetersPerNsp: params.min, basis: params.basis, page: params.page, pageSize: 20,
    }),
  });
  protected readonly data = computed(() => (this.res.hasValue() ? this.res.value() : undefined));
  protected readonly totalWeak = computed(() => (this.data()?.items ?? []).reduce((s, i) => s + i.weakFailing, 0));

  protected select(s: SimSwitchDto): void {
    if (s.lon != null && s.lat != null) this.buildingSelected.emit({ key: s.buildingKey, lon: s.lon, lat: s.lat });
  }
}
