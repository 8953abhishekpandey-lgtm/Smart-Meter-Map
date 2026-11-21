import Basemap from '@arcgis/core/Basemap';
import TileLayer from '@arcgis/core/layers/TileLayer';

export type BasemapKey = 'streets' | 'satellite' | 'dark-gray' | 'light-gray';

export const BASEMAP_OPTIONS: { key: BasemapKey; label: string }[] = [
  { key: 'streets', label: 'Streets' },
  { key: 'satellite', label: 'Satellite' },
  { key: 'dark-gray', label: 'Dark Gray' },
  { key: 'light-gray', label: 'Light Gray' },
];

// Vector basemap styles (require an ArcGIS API key / Location Platform account).
const STYLE_IDS: Record<BasemapKey, string> = {
  streets: 'arcgis/streets',
  satellite: 'arcgis/imagery',
  'dark-gray': 'arcgis/dark-gray',
  'light-gray': 'arcgis/light-gray',
};

// Keyless public map tile services (used when no API key is configured).
const TILE_SERVICES: Record<BasemapKey, string> = {
  streets: 'https://services.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer',
  satellite: 'https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer',
  'dark-gray': 'https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer',
  'light-gray': 'https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Light_Gray_Base/MapServer',
};

export function createBasemap(key: BasemapKey, hasApiKey: boolean): Basemap | string {
  if (hasApiKey) return STYLE_IDS[key];
  return new Basemap({
    id: `sm-${key}`,
    title: BASEMAP_OPTIONS.find((b) => b.key === key)?.label,
    baseLayers: [new TileLayer({ url: TILE_SERVICES[key] })],
  });
}
