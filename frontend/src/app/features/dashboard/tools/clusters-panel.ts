import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { FailureCluster, FailureClustersDto } from '../../../core/api/models';
import { SmartMeterApi } from '../../../core/api/smart-meter-api';
import { FilterStore } from '../../../core/state/filter.store';
import { MapCommands } from '../../../core/state/map-commands';
import { compact, fmt, pct } from '../../../core/ui/format';
import { BandBadge } from '../../../shared/band-badge';

/** Failure Cluster Finder: thresholds -> ranked high-failure pockets; click zooms the map for field visits. */
@Component({
  selector: 'sm-clusters-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BandBadge],
  template: `
    <h3>Failure cluster finder</h3>
    <p class="hint">Groups high-failure buildings that lie within the distance of each other (DBSCAN) and keeps pockets above the meter threshold.</p>
    <form class="form" (submit)="$event.preventDefault(); run()">
      <label>Min meters in pocket <input type="number" min="1" [value]="minMeters()" (change)="minMeters.set(+$any($event.target).value)" /></label>
      <label>Failure rate above (%) <input type="number" min="0" max="100" [value]="minFailurePct()" (change)="minFailurePct.set(+$any($event.target).value)" /></label>
      <label>Neighbour distance (m) <input type="number" min="10" max="20000" [value]="eps()" (change)="eps.set(+$any($event.target).value)" /></label>
      <label>Scope
        <select [value]="scope()" (change)="scope.set($any($event.target).value)">
          <option value="fleet">Whole filtered fleet</option><option value="view">Current map view</option>
        </select>
      </label>
      <div class="actions">
        <button type="submit" class="primary" [disabled]="running()">{{ running() ? 'Searching…' : 'Find clusters' }}</button>
        @if (result()) { <button type="button" (click)="clear()">Clear</button> }
      </div>
    </form>
    @if (result(); as r) {
      <p class="meta">{{ fmt(r.clustersFound) }} pockets from {{ fmt(r.hotBuildings) }} high-failure buildings · {{ r.elapsedMs }} ms</p>
      <ol class="list">
        @for (c of r.clusters; track c.id) {
          <li class="item" [class.on]="commands.activeCluster() === c.id" (click)="focus(c)" tabindex="0" (keydown.enter)="focus(c)">
            <div class="row1"><b>#{{ c.rank }}</b> <span>{{ fmt(c.meters) }} meters · {{ c.buildings }} buildings</span><sm-band [band]="c.band" [failurePct]="c.failurePct" /></div>
            <div class="row2"><b>{{ fmt(c.failing) }}</b> failing ({{ pct(c.failurePct) }})</div>
            <div class="nsps">
              @for (n of c.nsps; track n.nspId) { <span class="nsp" [title]="fmt(n.failing) + ' of ' + fmt(n.expected) + ' failing'">{{ n.nsp }} {{ pct(n.failurePct, 0) }}</span> }
            </div>
          </li>
        } @empty { <li class="empty">No pockets match these thresholds.</li> }
      </ol>
    }
  `,
  styleUrl: './tools.scss',
})
export class ClustersPanel {
  readonly buildingSelected = output<{ key: string; lon: number; lat: number }>();
  private readonly api = inject(SmartMeterApi);
  private readonly store = inject(FilterStore);
  protected readonly commands = inject(MapCommands);
  protected readonly fmt = fmt;
  protected readonly pct = pct;
  protected readonly compact = compact;

  protected readonly minMeters = signal(500);
  protected readonly minFailurePct = signal(25);
  protected readonly eps = signal(400);
  protected readonly scope = signal<'fleet' | 'view'>('fleet');
  protected readonly running = signal(false);
  protected readonly result = signal<FailureClustersDto | null>(null);
  protected readonly count = computed(() => this.result()?.clusters.length ?? 0);

  protected run(): void {
    this.running.set(true);
    this.api.failureClusters(this.store.query(), {
      minMeters: this.minMeters(), minFailurePct: this.minFailurePct(), epsMeters: this.eps(), minPoints: 1, limit: 50,
      bbox: this.scope() === 'view' ? this.commands.viewport() : null,
    }).subscribe({
      next: (r) => {
        this.result.set(r);
        this.commands.clusters.set(r.clusters);
        this.commands.activeCluster.set(null);
        this.running.set(false);
      },
      error: () => this.running.set(false),
    });
  }

  protected focus(c: FailureCluster): void {
    this.commands.activeCluster.set(c.id);
    this.commands.fitExtent(c.extent);
  }

  protected clear(): void {
    this.result.set(null);
    this.commands.clusters.set([]);
    this.commands.activeCluster.set(null);
  }
}
