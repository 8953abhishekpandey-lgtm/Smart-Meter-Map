import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { SmartMeterApi } from '../../core/api/smart-meter-api';
import { MetaStore } from '../../core/state/meta.store';
import { HEALTH_LABELS, longDate, pct, shortDate } from '../../core/ui/format';

/** Single meter card: attributes + 30-day daily communication timeline. */
@Component({
  selector: 'sm-meter-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './meter-detail.html',
  styleUrl: './details.scss',
})
export class MeterDetail {
  readonly meterId = input.required<string>();
  readonly openBuilding = output<{ key: string; lon?: number | null; lat?: number | null }>();
  readonly locate = output<{ lon: number; lat: number }>();

  private readonly api = inject(SmartMeterApi);
  private readonly meta = inject(MetaStore);
  protected readonly healthLabels = HEALTH_LABELS;
  protected readonly shortDate = shortDate;
  protected readonly longDate = longDate;
  protected readonly pct = pct;

  protected readonly meter = rxResource({
    params: () => ({ id: this.meterId(), v: this.meta.dataVersion() }),
    stream: ({ params }) => this.api.meter(params.id),
  });
  protected readonly m = computed(() => (this.meter.hasValue() ? this.meter.value() : undefined));
  protected readonly counts = computed(() => {
    const t = this.m()?.timeline ?? [];
    return {
      ok: t.filter((d) => d.status === 'communicating').length,
      failed: t.filter((d) => d.status === 'failed').length,
      missing: t.filter((d) => d.status === 'no-record').length,
    };
  });
}
