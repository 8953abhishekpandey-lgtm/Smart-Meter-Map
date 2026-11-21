import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Breakdown } from '../../../core/api/models';
import { SmartMeterApi } from '../../../core/api/smart-meter-api';
import { FilterStore } from '../../../core/state/filter.store';
import { MapCommands } from '../../../core/state/map-commands';
import { MetaStore } from '../../../core/state/meta.store';
import { BAND_COLORS, fmt, pct } from '../../../core/ui/format';
import { BandBadge } from '../../../shared/band-badge';

/** Custom polygon / lasso selection: exact meter-level containment, independent of building ids. */
@Component({
  selector: 'sm-lasso-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BandBadge],
  template: `
    <h3>Custom area selection</h3>
    <p class="hint">Draw any boundary on the map to analyse every meter inside it - across buildings, grid cells and zones.</p>
    <div class="actions">
      <button type="button" (click)="commands.draw('lasso')">Lasso</button>
      <button type="button" (click)="commands.draw('polygon')">Polygon</button>
      <button type="button" (click)="commands.draw('rectangle')">Box</button>
      <button type="button" (click)="commands.draw('clear')" [disabled]="!commands.polygon()">Clear</button>
    </div>
    @if (stats.isLoading()) { <p class="meta">Running spatial containment query…</p> }
    @if (s(); as r) {
      <div class="summary">
        <div><span class="k">Meters inside</span><b>{{ fmt(r.meters) }}</b></div>
        <div><span class="k">Buildings</span><b>{{ fmt(r.buildings) }}</b></div>
        <div><span class="k">Area</span><b>{{ r.areaKm2.toFixed(2) }} km²</b></div>
        <div><span class="k">Communicating</span><b>{{ fmt(r.success) }} ({{ pct(r.successPct) }})</b></div>
        <div><span class="k">Failing</span><b>{{ fmt(r.failure) }} ({{ pct(r.failurePct) }})</b></div>
        <div><span class="k">Status</span><sm-band [band]="r.band" [failurePct]="r.failurePct" /></div>
      </div>
      <p class="meta">All-day {{ fmt(r.health.allDay) }} · last 7 days {{ fmt(r.health.last7Days) }} · intermittent {{ fmt(r.health.intermittent) }} · dead {{ fmt(r.health.dead) }}
        @if (r.approximate) { · <b>approximate (building centroids, very large area)</b> }</p>
      <h4>NSP failure breakdown</h4>
      <table class="mini">
        <thead><tr><th>NSP</th><th class="num">Meters</th><th>Failing</th></tr></thead>
        <tbody>
          @for (n of r.byNsp; track n.id) {
            <tr><td>{{ n.name }}</td><td class="num">{{ fmt(n.meters) }}</td>
              <td><span class="bar"><span [style.width.%]="n.failurePct" [style.background]="color(n)"></span></span>{{ fmt(n.failure) }} ({{ pct(n.failurePct, 0) }})</td></tr>
          }
        </tbody>
      </table>
      <h4>By manufacturer</h4>
      <table class="mini">
        <tbody>
          @for (n of r.byManufacturer; track n.id) {
            <tr><td>{{ n.name }}</td><td class="num">{{ fmt(n.meters) }}</td><td class="num">{{ pct(n.failurePct, 0) }} failing</td></tr>
          }
        </tbody>
      </table>
    } @else if (!commands.polygon()) {
      <p class="empty">No area drawn yet.</p>
    }
  `,
  styleUrl: './tools.scss',
})
export class LassoPanel {
  private readonly api = inject(SmartMeterApi);
  private readonly store = inject(FilterStore);
  private readonly meta = inject(MetaStore);
  protected readonly commands = inject(MapCommands);
  protected readonly fmt = fmt;
  protected readonly pct = pct;

  protected readonly stats = rxResource({
    params: () => {
      const ring = this.commands.polygon();
      return ring ? { ring, q: this.store.query(), v: this.meta.dataVersion() } : undefined;
    },
    stream: ({ params }) => this.api.polygon(params.q, params.ring),
  });
  protected readonly s = computed(() => (this.stats.hasValue() && this.commands.polygon() ? this.stats.value() : undefined));

  protected color(b: Breakdown): string {
    const p = Math.round(b.failurePct);
    return p <= 20 ? BAND_COLORS.green : p >= 80 ? BAND_COLORS.red : BAND_COLORS.amber;
  }
}
