import { Injectable, signal } from '@angular/core';

/** Runtime configuration loaded from /config.json (mounted per environment, no rebuild needed). */
export interface AppConfig {
  /** Base URL of the API ('' = same origin, behind the nginx reverse proxy). */
  apiBaseUrl: string;
  /** Optional ArcGIS API key: enables the vector basemap styles service. Without it, public tile basemaps are used. */
  arcgisApiKey?: string;
  /** Where the ArcGIS Maps SDK assets are served from (self-hosted copy by default, works air-gapped). */
  arcgisAssetsPath: string;
  /** Optional self-hosted ArcGIS label fonts (air-gapped networks); empty = Esri CDN. */
  arcgisFontsUrl?: string;
  defaultCenter: [number, number];
  defaultZoom: number;
  locale: string;
}

const DEFAULTS: AppConfig = {
  apiBaseUrl: '',
  arcgisApiKey: '',
  arcgisAssetsPath: '/assets/arcgis',
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
