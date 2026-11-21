import { HealthBand, HealthClass } from '../api/models';

let numberFormat = new Intl.NumberFormat('en-IN');
let compactFormat = new Intl.NumberFormat('en-IN', { notation: 'compact', maximumFractionDigits: 1 });

export function setLocale(locale: string): void {
  numberFormat = new Intl.NumberFormat(locale);
  compactFormat = new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 });
}

export const fmt = (n: number | null | undefined): string => (n === null || n === undefined ? '—' : numberFormat.format(n));
export const compact = (n: number | null | undefined): string => (n === null || n === undefined ? '—' : compactFormat.format(n));
export const pct = (n: number | null | undefined, digits = 1): string =>
  n === null || n === undefined ? '—' : `${n.toFixed(digits)}%`;

export function shortDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso + (iso.length === 10 ? 'T00:00:00' : ''));
  return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' });
}

export function longDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso + (iso.length === 10 ? 'T00:00:00' : ''));
  return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
}

/** Status palette (fixed, never themed) - always paired with a text label. */
export const BAND_COLORS: Record<HealthBand, string> = {
  green: '#0ca30c',
  amber: '#fab219',
  red: '#d03b3b',
  none: '#898781',
};

export const BAND_LABELS: Record<HealthBand, string> = {
  green: 'Healthy · ≤20% failing',
  amber: 'Degraded · 21–79% failing',
  red: 'Critical · ≥80% failing',
  none: 'No expected meters',
};

export const HEALTH_LABELS: Record<HealthClass, string> = {
  allDay: 'All-day',
  intermittent: 'Intermittent',
  dead: 'Dead',
  notInstalled: 'Not installed',
};

export const SERIES_BLUE = '#2a78d6';
export const SERIES_BLUE_DARK = '#1c5cab';
export const INK = { primary: '#0b0b0b', secondary: '#52514e', muted: '#898781', grid: '#e1e0d9', axis: '#c3c2b7', surface: '#fcfcfb' };
