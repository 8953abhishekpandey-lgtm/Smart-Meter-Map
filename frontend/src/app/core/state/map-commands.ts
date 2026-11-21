import { Injectable, signal } from '@angular/core';
import { FailureCluster } from '../api/models';

export type MapFocus =
  | { kind: 'point'; lon: number; lat: number; zoom: number; nonce: number }
  | { kind: 'extent'; extent: number[]; nonce: number };

/** Imperative-ish commands from panels (clusters, SIM switch, search, explorer) to the map, as signals. */
@Injectable({ providedIn: 'root' })
export class MapCommands {
  private seq = 0;
  readonly focus = signal<MapFocus | null>(null);
  readonly clusters = signal<FailureCluster[]>([]);
  readonly activeCluster = signal<string | null>(null);
  readonly selectedBuilding = signal<{ key: string; lon?: number | null; lat?: number | null } | null>(null);
  readonly meterPin = signal<{ meterId: string; lon: number; lat: number } | null>(null);
  readonly drawRequest = signal<{ tool: 'polygon' | 'lasso' | 'rectangle' | 'clear'; nonce: number } | null>(null);
  /** Current map viewport (WGS84 bbox) - lets analytics panels scope to "current view". */
  readonly viewport = signal<number[] | null>(null);
  /** Last polygon drawn on the map (WGS84 outer ring). */
  readonly polygon = signal<number[][] | null>(null);

  flyTo(lon: number, lat: number, zoom = 17): void { this.focus.set({ kind: 'point', lon, lat, zoom, nonce: ++this.seq }); }
  fitExtent(extent: number[]): void { this.focus.set({ kind: 'extent', extent, nonce: ++this.seq }); }
  draw(tool: 'polygon' | 'lasso' | 'rectangle' | 'clear'): void { this.drawRequest.set({ tool, nonce: ++this.seq }); }
}
