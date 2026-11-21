import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { rxResource, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { debounceTime } from 'rxjs';
import { BuildingListItem, BuildingSort } from '../../core/api/models';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { FilterStore } from '../../core/state/filter.store';
import { MetaStore } from '../../core/state/meta.store';
import { fmt, pct } from '../../core/ui/format';
import { BandBadge } from '../../shared/band-badge';
import { Pager } from '../../shared/pager';
import { Crumbs } from './crumbs';

/** Level 2: buildings (incl. virtual grid-cell buildings) of one zone. */
@Component({
  selector: 'sm-zone-buildings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BandBadge, Pager, Crumbs],
  template: `
    <div class="wrap">
      <sm-crumbs [items]="[{ label: 'Fleet', link: ['/explore'] }, { label: zoneName() }]" />
      <div class="title-row"><h1>{{ zoneName() }}</h1><span class="sub">Buildings and localities</span></div>
      <div class="toolbar">
        <input type="search" placeholder="Search building name or address…" [value]="search()" (input)="search.set($any($event.target).value); page.set(1)" aria-label="Search buildings" />
        <select [value]="sort()" (change)="sort.set($any($event.target).value); page.set(1)" aria-label="Sort">
          <option value="failureRate">Highest failure rate</option><option value="failing">Most failing meters</option>
          <option value="meters">Most meters</option><option value="name">Name</option>
        </select>
      </div>
      @if (data(); as d) {
        <table class="grid">
          <thead><tr><th>Building</th><th>Address</th><th class="num">Meters</th><th class="num">Communicating</th><th class="num">Failing</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (b of d.items; track b.buildingKey) {
              <tr class="click" (click)="open(b)">
                <td><b>{{ b.name }}</b>@if (b.isVirtual) { <span class="virt" title="No BuildingId - grouped by location">locality</span> }</td>
                <td class="addr">{{ b.address }}</td>
                <td class="num">{{ fmt(b.meters) }}</td>
                <td class="num">{{ fmt(b.success) }} / {{ fmt(b.expected) }}</td>
                <td class="num">{{ fmt(b.failure) }} ({{ pct(b.failurePct, 0) }})</td>
                <td><sm-band [band]="b.band" [failurePct]="b.failurePct" /></td>
                <td>@if (b.lon != null) { <a (click)="$event.stopPropagation()" [routerLink]="['/dashboard']" [queryParams]="{ building: b.buildingKey, lon: b.lon, lat: b.lat }">Map</a> }</td>
              </tr>
            } @empty { <tr><td colspan="7" class="muted">No buildings match.</td></tr> }
          </tbody>
        </table>
        <sm-pager [page]="d.page" [pageSize]="d.pageSize" [total]="d.total" (pageChange)="page.set($event)" />
      } @else if (res.isLoading()) { <p class="muted">Loading buildings…</p> }
    </div>
  `,
  styleUrl: './explorer.scss',
})
export class ZoneBuildingsPage {
  readonly zoneId = input.required<string>();
  private readonly api = inject(SmartMeterApi);
  private readonly router = inject(Router);
  protected readonly store = inject(FilterStore);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;
  protected readonly pct = pct;
  protected readonly search = signal('');
  private readonly searchDebounced = toSignal(toObservable(this.search).pipe(debounceTime(300)), { initialValue: '' });
  protected readonly sort = signal<BuildingSort>('failureRate');
  protected readonly page = signal(1);
  protected readonly zoneName = computed(() => this.meta.zoneName(+this.zoneId()));

  protected readonly res = rxResource({
    params: () => ({ zone: +this.zoneId(), q: this.store.query(), search: this.searchDebounced(), sort: this.sort(), page: this.page(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.zoneBuildings(params.zone, params.q, { search: params.search, sort: params.sort, page: params.page, pageSize: 50 }),
  });
  protected readonly data = computed(() => (this.res.hasValue() ? this.res.value() : undefined));

  protected open(b: BuildingListItem): void { void this.router.navigate(['/explore/buildings', b.buildingKey]); }
}
