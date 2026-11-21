import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { FilterStore } from '../../core/state/filter.store';
import { BuildingDetail } from '../details/building-detail';
import { Crumbs } from './crumbs';

/** Level 3: full building page. */
@Component({
  selector: 'sm-building-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BuildingDetail, Crumbs, RouterLink],
  template: `
    <div class="wrap narrow">
      <sm-crumbs [items]="crumbs()" />
      @if (b(); as x) {
        @if (x.lon != null) { <a class="map-link" [routerLink]="['/dashboard']" [queryParams]="{ building: x.buildingKey, lon: x.lon, lat: x.lat }">Show on map ↗</a> }
      }
      <sm-building-detail [buildingKey]="buildingKey()" (meterSelected)="openMeter($event)" />
    </div>
  `,
  styleUrl: './explorer.scss',
})
export class BuildingPage {
  readonly buildingKey = input.required<string>();
  private readonly api = inject(SmartMeterApi);
  private readonly router = inject(Router);
  private readonly store = inject(FilterStore);
  private readonly building = rxResource({
    params: () => ({ key: this.buildingKey(), date: this.store.date() }),
    stream: ({ params }) => this.api.building(params.key, params.date),
  });
  protected readonly b = computed(() => (this.building.hasValue() ? this.building.value() : undefined));
  protected readonly crumbs = computed(() => {
    const x = this.b();
    return [
      { label: 'Fleet', link: ['/explore'] },
      ...(x ? [{ label: x.zone, link: ['/explore/zones', x.zoneId] }] : []),
      { label: x?.name ?? this.buildingKey() },
    ];
  });
  protected openMeter(id: string): void { void this.router.navigate(['/explore/meters', id]); }
}
