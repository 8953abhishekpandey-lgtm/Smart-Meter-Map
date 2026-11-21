import { Injectable, signal } from '@angular/core';
import type { BasemapDef, BasemapKey } from '../../features/dashboard/map/basemaps';

/** Runtime configuration loaded from /config.json (mounted per environment, no rebuild needed). */
export interface AppConfig {
  /** Base URL of the API ('' = same origin: the API serves this app). */
  apiBaseUrl: string;
  /** Optional basemap overrides, e.g. a self-hosted tile server on an air-gapped network:
   *  { "streets": { "url": "https://tiles.example.local/osm/{z}/{x}/{y}.png", "attribution": "...", "maxZoom": 19 } } */
  basemaps?: Partial<Record<BasemapKey, Partial<BasemapDef>>>;
  defaultCenter: [number, number];
  defaultZoom: number;
  locale: string;
}

const DEFAULTS: AppConfig = {
  apiBaseUrl: '',
  defaultCenter: [78.9, 21.5],
  defaultZoom: 5,
  locale: 'en-IN',
};

@Injectable({ providedIn: 'root' })
export class AppConfigService {
  private readonly state = signal<AppConfig>(DEFAULTS);
  readonly config = this.state.asReadonly();

  get value(): AppConfig {
    return this.state();
  }

  async load(): Promise<void> {
    try {
      const response = await fetch('config.json', { cache: 'no-store' });
      if (response.ok) this.state.set({ ...DEFAULTS, ...(await response.json()) });
    } catch {
      /* keep defaults */
    }
  }
}
