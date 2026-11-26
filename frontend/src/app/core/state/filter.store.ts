import { Injectable, computed, signal } from '@angular/core';
import { Params } from '@angular/router';
import { FleetQuery, HealthFilter, MetricMode } from '../api/models';

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** Global, cross-page filter state (Signals). Every data view derives its request from `query`. */
@Injectable({ providedIn: 'root' })
export class FilterStore {
  readonly zones = signal<number[]>([]);
  readonly nsps = signal<number[]>([]);
  readonly mfrs = signal<number[]>([]);
  readonly installedFrom = signal<string | null>(null);
  readonly installedTo = signal<string | null>(null);
  readonly health = signal<HealthFilter>('all');
  /** Selected day (null = newest day of the window). */
  readonly date = signal<string | null>(null);
  readonly metric = signal<MetricMode>('day');
  /** Date range for the 'window' metric (null = the whole analytics window). */
  readonly rangeFrom = signal<string | null>(null);
  readonly rangeTo = signal<string | null>(null);

  readonly query = computed<FleetQuery>(
    () => ({
      zones: [...this.zones()].sort((a, b) => a - b),
      nsps: [...this.nsps()].sort((a, b) => a - b),
      mfrs: [...this.mfrs()].sort((a, b) => a - b),
      installedFrom: this.installedFrom(),
      installedTo: this.installedTo(),
      health: this.health(),
      date: this.date(),
      metric: this.metric(),
      from: this.metric() === 'window' ? this.rangeFrom() : null,
      to: this.metric() === 'window' ? this.rangeTo() : null,
    }),
    { equal: same },
  );

  /** Same query without the health chip (chips always show counts for every class). */
  readonly dimensionQuery = computed<FleetQuery>(() => ({ ...this.query(), health: 'all' }), { equal: same });

  readonly activeFilterCount = computed(() => {
    const q = this.query();
    return (q.zones.length ? 1 : 0) + (q.nsps.length ? 1 : 0) + (q.mfrs.length ? 1 : 0)
      + (q.installedFrom || q.installedTo ? 1 : 0) + (q.health !== 'all' ? 1 : 0);
  });

  reset(): void {
    this.zones.set([]);
    this.nsps.set([]);
    this.mfrs.set([]);
    this.installedFrom.set(null);
    this.installedTo.set(null);
    this.health.set('all');
  }

  toParams(): Params {
    const q = this.query();
    return {
      zone: q.zones.length ? q.zones : null,
      nsp: q.nsps.length ? q.nsps : null,
      mfr: q.mfrs.length ? q.mfrs : null,
      installedFrom: q.installedFrom,
      installedTo: q.installedTo,
      health: q.health === 'all' ? null : q.health,
      date: q.date,
      metric: q.metric === 'day' ? null : q.metric,
      from: q.from,
      to: q.to,
    };
  }

  fromParams(p: Params): void {
    const ids = (v: unknown) => (v === undefined || v === null ? [] : (Array.isArray(v) ? v : [v]).map(Number).filter(Number.isFinite));
    this.zones.set(ids(p['zone']));
    this.nsps.set(ids(p['nsp']));
    this.mfrs.set(ids(p['mfr']));
    this.installedFrom.set(p['installedFrom'] ?? null);
    this.installedTo.set(p['installedTo'] ?? null);
    this.health.set((p['health'] as HealthFilter) ?? 'all');
    this.date.set(p['date'] ?? null);
    this.metric.set((p['metric'] as MetricMode) ?? 'day');
    this.rangeFrom.set(p['from'] ?? null);
    this.rangeTo.set(p['to'] ?? null);
  }
}
