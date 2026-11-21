import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { MeterDetail } from '../details/meter-detail';
import { Crumbs } from './crumbs';

/** Level 4: single meter page (Fleet > Zone > Building > Meter). */
@Component({
  selector: 'sm-meter-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MeterDetail, Crumbs],
  template: `
    <div class="wrap narrow">
      <sm-crumbs [items]="crumbs()" />
      <sm-meter-detail [meterId]="meterId()" (openBuilding)="openBuilding($event.key)" (locate)="locate()" />
    </div>
  `,
  styleUrl: './explorer.scss',
})
export class MeterPage {
  readonly meterId = input.required<string>();
  private readonly api = inject(SmartMeterApi);
  private readonly router = inject(Router);
  private readonly meter = rxResource({ params: () => this.meterId(), stream: ({ params }) => this.api.meter(params) });
  private readonly m = computed(() => (this.meter.hasValue() ? this.meter.value() : undefined));
  protected readonly crumbs = computed(() => {
    const x = this.m();
    return [
      { label: 'Fleet', link: ['/explore'] },
      ...(x ? [{ label: x.zone, link: ['/explore/zones', x.zoneId] }, { label: x.buildingName ?? x.buildingKey, link: ['/explore/buildings', x.buildingKey] }] : []),
      { label: this.meterId() },
    ];
  });
  protected openBuilding(key: string): void { void this.router.navigate(['/explore/buildings', key]); }
  protected locate(): void { void this.router.navigate(['/dashboard'], { queryParams: { meter: this.meterId() } }); }
}
