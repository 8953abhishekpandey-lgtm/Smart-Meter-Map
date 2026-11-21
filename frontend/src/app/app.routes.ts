import { Routes } from '@angular/router';

/** All feature areas are lazy-loaded (ArcGIS is only downloaded with the dashboard chunk). */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.page').then((m) => m.DashboardPage), title: 'Smart Meter GIS · Dashboard' },
  { path: 'explore', loadChildren: () => import('./features/explorer/explorer.routes').then((m) => m.EXPLORER_ROUTES) },
  { path: 'ingestion', loadComponent: () => import('./features/ingestion/ingestion.page').then((m) => m.IngestionPage), title: 'Smart Meter GIS · Ingestion' },
  { path: '**', redirectTo: 'dashboard' },
];
