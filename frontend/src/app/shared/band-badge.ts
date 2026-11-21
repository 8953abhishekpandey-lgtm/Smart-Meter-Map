import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HealthBand } from '../core/api/models';
import { BAND_COLORS } from '../core/ui/format';

const SHORT: Record<HealthBand, string> = { green: 'Healthy', amber: 'Degraded', red: 'Critical', none: 'n/a' };
const ICON: Record<HealthBand, string> = { green: '●', amber: '▲', red: '■', none: '○' };

/** Status pill: colour + shape icon + text, so state never relies on colour alone. */
@Component({
  selector: 'sm-band',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="band" [title]="title()"><span class="icon" [style.color]="color()" aria-hidden="true">{{ icon() }}</span>{{ label() }}</span>`,
  styles: [`.band { display: inline-flex; align-items: center; gap: 5px; font-size: 12px; font-weight: 600; color: var(--ink-secondary); white-space: nowrap; }
            .icon { font-size: 11px; }`],
})
export class BandBadge {
  readonly band = input.required<HealthBand>();
  readonly failurePct = input<number | null>(null);
  protected readonly color = computed(() => BAND_COLORS[this.band()]);
  protected readonly icon = computed(() => ICON[this.band()]);
  protected readonly label = computed(() => SHORT[this.band()]);
  protected readonly title = computed(() => (this.failurePct() !== null ? `${this.failurePct()!.toFixed(1)}% failing` : SHORT[this.band()]));
}
