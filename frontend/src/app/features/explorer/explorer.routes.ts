import { Routes } from '@angular/router';

export const EXPLORER_ROUTES: Routes = [
  { path: '', loadComponent: () => import('./zones.page').then((m) => m.ZonesPage), title: 'Explorer · Zones' },
  { path: 'zones/:zoneId', loadComponent: () => import('./zone-buildings.page').then((m) => m.ZoneBuildingsPage), title: 'Explorer · Buildings' },
  { path: 'buildings/:buildingKey', loadComponent: () => import('./building.page').then((m) => m.BuildingPage), title: 'Explorer · Building' },
  { path: 'meters/:meterId', loadComponent: () => import('./meter.page').then((m) => m.MeterPage), title: 'Explorer · Meter' },
];
