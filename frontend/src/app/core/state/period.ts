import { Injectable, computed, inject } from '@angular/core';
import { longDate } from '../ui/format';
import { FilterStore } from './filter.store';
import { MetaStore } from './meta.store';

export interface Period { from: string; to: string; days: number; full: boolean; }

/** The days the 'window' metric averages: the chosen From..To, clamped to the analytics window. */
@Injectable({ providedIn: 'root' })
export class PeriodStore {
  private readonly store = inject(FilterStore);
  private readonly meta = inject(MetaStore);

  readonly period = computed<Period | null>(() => {
    const w = this.meta.window();
    if (!w) return null;
    const clamp = (d: string) => (d < w.start ? w.start : d > w.end ? w.end : d);
    const from = clamp(this.store.rangeFrom() ?? w.start);
    const to = clamp(this.store.rangeTo() ?? w.end);
    const days = Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000) + 1;
    return { from, to, days, full: from === w.start && to === w.end };
  });

  /** "18 Nov 2026 – 20 Nov 2026 · 3 days" */
  readonly label = computed(() => {
    const p = this.period();
    return p ? `${longDate(p.from)} – ${longDate(p.to)} · ${p.days} day${p.days === 1 ? '' : 's'}` : '';
  });
}
