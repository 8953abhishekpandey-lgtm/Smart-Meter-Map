import XYZ from 'ol/source/XYZ';

export type BasemapKey = 'streets' | 'satellite' | 'dark-gray' | 'light-gray';

/**
 * A raster XYZ tile basemap. Every field can be overridden in config.json -> "basemaps" (e.g. point all four at the
 * company's own tile server). `filter` is a CSS filter applied to the basemap canvas only (bubbles are unaffected).
 */
export interface BasemapDef {
  label: string;
  url: string;
  attribution: string;
  maxZoom: number;
  filter?: string;
}

const OSM = '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank">OpenStreetMap</a> contributors';
const OSM_TILES = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';

/**
 * Open, keyless defaults (verified to serve without an API key or watermark). The gray variants are the standard
 * OpenStreetMap tiles rendered through a CSS filter, so they need no extra provider. tile.openstreetmap.org is a
 * community service with a usage policy (https://operations.osmfoundation.org/policies/tiles/): for heavy production
 * traffic point "basemaps" at a self-hosted or commercial OSM tile server.
 */
export const DEFAULT_BASEMAPS: Record<BasemapKey, BasemapDef> = {
  streets: {
    label: 'Streets',
    url: OSM_TILES,
    attribution: OSM,
    maxZoom: 19,
  },
  satellite: {
    label: 'Satellite',
    // Sentinel-2 cloudless 2016 by EOX - CC BY 4.0 (later vintages are non-commercial licensed)
    url: 'https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{z}/{y}/{x}.jpg',
    attribution: '<a href="https://s2maps.eu" target="_blank">Sentinel-2 cloudless</a> by EOX IT Services GmbH (contains modified Copernicus Sentinel data 2016)',
    maxZoom: 15,
  },
  'dark-gray': {
    label: 'Dark Gray',
    url: OSM_TILES,
    attribution: OSM,
    maxZoom: 19,
    filter: 'grayscale(1) invert(1) brightness(0.88) contrast(0.82)',
  },
  'light-gray': {
    label: 'Light Gray',
    url: OSM_TILES,
    attribution: OSM,
    maxZoom: 19,
    filter: 'grayscale(1) brightness(1.06) contrast(0.78)',
  },
};

export function basemapDefs(overrides?: Partial<Record<BasemapKey, Partial<BasemapDef>>>): Record<BasemapKey, BasemapDef> {
  const out = { ...DEFAULT_BASEMAPS };
  for (const key of Object.keys(out) as BasemapKey[]) {
    if (overrides?.[key]) out[key] = { ...out[key], ...overrides[key] };
  }
  return out;
}

export function createTileSource(def: BasemapDef): XYZ {
  return new XYZ({ url: def.url, attributions: def.attribution, maxZoom: def.maxZoom, crossOrigin: 'anonymous', transition: 150 });
}
