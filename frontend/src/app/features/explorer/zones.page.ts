import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { FilterStore } from '../../core/state/filter.store';
import { MetaStore } from '../../core/state/meta.store';
import { fmt, longDate, pct } from '../../core/ui/format';
import { BandBadge } from '../../shared/band-badge';
import { Crumbs } from './crumbs';

/** Level 1: every zone (area) with its communication health. */
@Component({
  selector: 'sm-zones-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BandBadge, Crumbs],
  template: `
    <div class="wrap">
      <sm-crumbs [items]="[{ label: 'Fleet' }]" />
      <div class="title-row">
        <h1>Zones</h1>
        <span class="sub">{{ store.metric() === 'window' ? (meta.window()?.days ?? 30) + '-day average' : 'Selected day ' + longDate(store.date() ?? meta.window()?.end) }}
          @if (store.activeFilterCount() > 0) { · {{ store.activeFilterCount() }} filter(s) active }</span>
      </div>
      @if (zones.isLoading() && !list().length) { <p class="muted">Loading zones…</p> }
      <div class="cards">
        @for (z of list(); track z.zoneId) {
          <article class="card">
            <header><h2><a [routerLink]="['zones', z.zoneId]">{{ z.name }}</a></h2><sm-band [band]="z.band" [failurePct]="z.failurePct" /></header>
            <div class="nums">
              <div><span class="k">Meters</span><b>{{ fmt(z.meters) }}</b></div>
              <div><span class="k">Buildings</span><b>{{ fmt(z.buildings) }}</b></div>
              <div><span class="k">Success</span><b>{{ pct(z.successPct) }}</b></div>
              <div><span class="k">Failing</span><b>{{ fmt(z.failure) }}</b></div>
            </div>
            <div class="health">All-day {{ fmt(z.health.allDay) }} · intermittent {{ fmt(z.health.intermittent) }} · dead {{ fmt(z.health.dead) }}</div>
            <footer>
              <a [routerLink]="['zones', z.zoneId]">Buildings →</a>
              @if (z.extent) { <a [routerLink]="['/dashboard']" [queryParams]="{ focus: z.extent.join(',') }">Show on map</a> }
            </footer>
          </article>
        }
      </div>
    </div>
  `,
  styleUrl: './explorer.scss',
})
export class ZonesPage {
  private readonly api = inject(SmartMeterApi);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;
  protected readonly pct = pct;
  protected readonly longDate = longDate;
  protected readonly zones = rxResource({
    params: () => ({ q: this.store.query(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.zones(params.q),
  });
  protected readonly list = computed(() => (this.zones.hasValue() ? this.zones.value() : []));
}
