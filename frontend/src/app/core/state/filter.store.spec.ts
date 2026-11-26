import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { fleetParams } from '../api/smart-meter-api';
import { FilterStore } from './filter.store';

describe('FilterStore', () => {
  it('round-trips filters through URL params', () => {
    const store = TestBed.inject(FilterStore);
    store.zones.set([4, 1]);
    store.nsps.set([2]);
    store.health.set('dead');
    store.date.set('2026-11-15');
    const params = store.toParams();
    expect(params['zone']).toEqual([1, 4]);
    expect(params['health']).toBe('dead');
    expect(params['metric']).toBeNull();

    const other = new FilterStore();
    other.fromParams({ zone: ['1', '4'], nsp: '2', health: 'dead', date: '2026-11-15' });
    expect(other.query()).toEqual(store.query());
    expect(other.activeFilterCount()).toBe(3);
  });

  it('keeps query identity when values do not change (no redundant requests)', () => {
    const store = new FilterStore();
    const before = store.query();
    store.zones.set([]);
    expect(store.query()).toBe(before);
  });

  it('serialises multi-selects as repeated keys and omits defaults', () => {
    const p = fleetParams({ zones: [1, 2], nsps: [], mfrs: [3], installedFrom: '2026-01-01', installedTo: null, health: 'all', date: null, metric: 'day', from: null, to: null },
      { zoom: 12, bbox: '' });
    expect(p.getAll('zone')).toEqual(['1', '2']);
    expect(p.getAll('mfr')).toEqual(['3']);
    expect(p.has('health')).toBe(false);
    expect(p.has('metric')).toBe(false);
    expect(p.has('bbox')).toBe(false);
    expect(p.get('zoom')).toBe('12');
  });

  it('sends the date range only with the window metric, and round-trips it through the URL', () => {
    const store = new FilterStore();
    store.rangeFrom.set('2026-11-18');
    store.rangeTo.set('2026-11-20');
    expect(store.query().from).toBeNull();                       // day metric: range not part of the query
    store.metric.set('window');
    const p = fleetParams(store.query());
    expect(p.get('metric')).toBe('window');
    expect(p.get('from')).toBe('2026-11-18');
    expect(p.get('to')).toBe('2026-11-20');

    const other = new FilterStore();
    other.fromParams(store.toParams());
    expect(other.query()).toEqual(store.query());
  });
});
