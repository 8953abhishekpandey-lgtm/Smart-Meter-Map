import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, input, isDevMode, output, signal, untracked, viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import OlMap from 'ol/Map';
import View from 'ol/View';
import Feature from 'ol/Feature';
import Point from 'ol/geom/Point';
import Polygon from 'ol/geom/Polygon';
import TileLayer from 'ol/layer/Tile';
import VectorLayer from 'ol/layer/Vector';
import VectorSource from 'ol/source/Vector';
import Draw, { createBox } from 'ol/interaction/Draw';
import { defaults as defaultControls, ScaleLine } from 'ol/control';
import { fromLonLat, toLonLat, transformExtent } from 'ol/proj';
import { Circle as CircleStyle, Fill, RegularShape, Stroke, Style, Text } from 'ol/style';
import type { FeatureLike } from 'ol/Feature';
import { catchError, debounceTime, filter, of, switchMap } from 'rxjs';
import { BubbleDto, BubblesDto, FleetQuery, HealthBand } from '../../../core/api/models';
import { SmartMeterApi } from '../../../core/api/smart-meter-api';
import { AppConfigService } from '../../../core/config/app-config';
import { MapCommands, MapFocus } from '../../../core/state/map-commands';
import { BAND_COLORS, BAND_LABELS, compact, fmt } from '../../../core/ui/format';
import { BasemapKey, basemapDefs, createTileSource } from './basemaps';

const BANDS: HealthBand[] = ['green', 'amber', 'red'];

function rgba(hex: string, a: number): string {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
}

/**
 * OpenLayers (open-source, BSD-2) map. It never receives raw meters: on every move it requests
 * server-side aggregated bubbles (grid pyramid when zoomed out, one bubble per building at street level).
 */
@Component({
  selector: 'sm-gis-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gis-map.html',
  styleUrl: './gis-map.scss',
})
export class GisMap {
  readonly query = input.required<FleetQuery>();
  readonly dataVersion = input(0);
  readonly buildingSelected = output<{ key: string; lon: number; lat: number }>();
  readonly clusterSelected = output<string>();

  private readonly api = inject(SmartMeterApi);
  private readonly config = inject(AppConfigService);
  protected readonly commands = inject(MapCommands);
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('mapHost');

  private readonly basemapDefs = basemapDefs(this.config.value.basemaps);
  protected readonly basemaps = (Object.keys(this.basemapDefs) as BasemapKey[]).map((key) => ({ key, label: this.basemapDefs[key].label }));
  protected readonly bands = BANDS.map((b) => ({ band: b, color: BAND_COLORS[b], label: BAND_LABELS[b] }));
  protected readonly basemapKey = signal<BasemapKey>('streets');
  protected readonly loading = signal(false);
  protected readonly drawing = signal(false);
  protected readonly tooltip = signal<{ x: number; y: number; b: BubbleDto } | null>(null);
  protected readonly info = signal<{ mode: string; level?: number | null; count: number; truncated: boolean; zoom: number } | null>(null);
  protected readonly fmt = fmt;

  private readonly viewReady = signal(false);
  private readonly viewport = signal<{ bbox: number[]; zoom: number } | null>(null);
  private map?: OlMap;
  private baseLayer?: TileLayer<ReturnType<typeof createTileSource>>;
  private baseFilter = '';
  private readonly bubbleSource = new VectorSource<Feature<Point>>();
  private readonly highlightSource = new VectorSource();
  private readonly sketchSource = new VectorSource();
  private bubbleLayer?: VectorLayer<VectorSource<Feature<Point>>>;
  private highlightLayer?: VectorLayer<VectorSource>;
  private draw?: Draw;
  private current: BubbleDto[] = [];
  private radius: number[] = [];
  private readonly styleCache = new Map<string, Style>();

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => this.init(destroyRef));

    // viewport x filters x data version -> bubbles (switchMap cancels superseded requests)
    const request = computed(() => {
      const vp = this.viewport();
      return vp ? { vp, q: this.query(), v: this.dataVersion() } : null;
    }, { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) });
    toObservable(request).pipe(
      filter((r): r is NonNullable<typeof r> => r !== null),
      debounceTime(120),
      switchMap((r) => {
        this.loading.set(true);
        return this.api.bubbles(r.q, r.vp.bbox, r.vp.zoom).pipe(catchError(() => of(null)));
      }),
      takeUntilDestroyed(),
    ).subscribe((res) => {
      this.loading.set(false);
      if (res) this.render(res);
    });

    effect(() => {
      const f = this.commands.focus();
      if (f && this.viewReady()) untracked(() => this.applyFocus(f));
    });
    effect(() => {
      this.commands.clusters();
      this.commands.activeCluster();
      this.commands.selectedBuilding();
      this.commands.meterPin();
      if (this.viewReady()) untracked(() => this.drawHighlights());
    });
    effect(() => {
      const d = this.commands.drawRequest();
      if (d && this.viewReady()) untracked(() => this.startDraw(d.tool));
    });
    effect(() => {
      const key = this.basemapKey();
      if (this.viewReady()) untracked(() => this.baseLayer?.setSource(createTileSource(this.basemapDefs[key])));
      this.baseFilter = this.basemapDefs[key].filter ?? '';
      this.map?.render();
    });
  }

  protected setBasemap(key: BasemapKey): void { this.basemapKey.set(key); }
  protected drawTool(tool: 'polygon' | 'lasso' | 'rectangle' | 'clear'): void { this.commands.draw(tool); }
  protected home(): void {
    const c = this.config.value;
    this.map?.getView().animate({ center: fromLonLat(c.defaultCenter), zoom: c.defaultZoom, duration: 600 });
  }

  // ------------------------------------------------------------------ setup
  private init(destroyRef: DestroyRef): void {
    const cfg = this.config.value;
    // own className -> own canvas, so the basemap CSS filter (gray variants) never touches the bubbles
    this.baseLayer = new TileLayer({ source: createTileSource(this.basemapDefs[this.basemapKey()]), preload: 1, className: 'sm-basemap' });
    this.baseLayer.on('prerender', (e) => {
      const canvas = (e.context as CanvasRenderingContext2D | undefined)?.canvas;
      if (canvas && canvas.style.filter !== this.baseFilter) canvas.style.filter = this.baseFilter;
    });
    this.bubbleLayer = new VectorLayer({
      source: this.bubbleSource,
      style: (f) => this.bubbleStyle(f),
      // draw large bubbles first so small ones on top stay clickable
      renderOrder: (a, b) => (this.current[b.get('idx')]?.meters ?? 0) - (this.current[a.get('idx')]?.meters ?? 0),
      // labels are drawn above every circle and hidden where they would collide (adjacent grid cells overlap)
      declutter: true,
      updateWhileAnimating: false,
      updateWhileInteracting: false,
    });
    this.highlightLayer = new VectorLayer({ source: this.highlightSource, style: (f) => f.get('style') as Style });
    const sketchLayer = new VectorLayer({
      source: this.sketchSource,
      style: new Style({ fill: new Fill({ color: rgba('#2a78d6', 0.1) }), stroke: new Stroke({ color: '#2a78d6', width: 2 }) }),
    });

    const map = new OlMap({
      target: this.host().nativeElement,
      layers: [this.baseLayer, this.bubbleLayer, this.highlightLayer, sketchLayer],
      view: new View({ center: fromLonLat(cfg.defaultCenter), zoom: cfg.defaultZoom, minZoom: 3, maxZoom: 20, constrainResolution: false }),
      controls: defaultControls({ attributionOptions: { collapsible: true, collapsed: false } }).extend([new ScaleLine({ units: 'metric' })]),
    });

    this.map = map;
    map.on('moveend', () => this.emitViewport());
    map.on('pointermove', (e) => {
      if (e.dragging || this.drawing()) return;
      this.onHover(e.pixel);
    });
    map.getViewport()?.addEventListener('mouseleave', () => this.tooltip.set(null));
    map.on('singleclick', (e) => this.onClick(e.pixel));

    const ro = new ResizeObserver(() => this.map?.updateSize());
    ro.observe(this.host().nativeElement);
    destroyRef.onDestroy(() => {
      ro.disconnect();
      this.map?.setTarget(undefined);
    });
    if (isDevMode()) (window as unknown as Record<string, unknown>)['__smMap'] = this;   // e2e / debugging hook
    this.viewReady.set(true);
    this.emitViewport();
  }

  private emitViewport(): void {
    const map = this.map;
    const size = map?.getSize();
    if (!map || !size) return;
    const view = map.getView();
    const ext = transformExtent(view.calculateExtent(size), 'EPSG:3857', 'EPSG:4326');
    let [xmin, ymin, xmax, ymax] = ext;
    if (xmax - xmin >= 360 || xmin > xmax) [xmin, xmax] = [-180, 180];
    const bbox = [Math.max(-180, xmin), Math.max(-85, ymin), Math.min(180, xmax), Math.min(85, ymax)];
    const zoom = Math.max(0, Math.min(22, Math.round(view.getZoom() ?? 5)));
    this.viewport.set({ bbox, zoom });
    this.commands.viewport.set(bbox);
  }

  // ------------------------------------------------------------------ rendering
  private render(res: BubblesDto): void {
    const values = res.bubbles.map((b) => b.meters);
    const min = values.length ? Math.min(...values) : 1;
    const max = values.length ? Math.max(...values) : 1;
    const buildings = res.mode === 'building';
    const [rMin, rMax] = buildings ? [4, 17] : [5, 26];
    // area-proportional sizing
    const scale = (m: number) => (max <= min ? (rMin + rMax) / 2 : rMin + (rMax - rMin) * Math.sqrt((m - min) / (max - min)));

    this.current = res.bubbles;
    this.radius = res.bubbles.map((b) => Math.round(scale(b.meters)));
    this.bubbleSource.clear(true);
    this.bubbleSource.addFeatures(res.bubbles.map((b, idx) => {
      const f = new Feature({ geometry: new Point(fromLonLat([b.lon, b.lat])) });
      f.set('idx', idx);
      return f;
    }));
    this.info.set({ mode: res.mode, level: res.level, count: res.bubbles.length, truncated: res.truncated, zoom: res.zoom });
  }

  private bubbleStyle(f: FeatureLike): Style {
    const idx = f.get('idx') as number;
    const b = this.current[idx];
    if (!b) return new Style();
    const r = this.radius[idx];
    const label = r >= 12 ? compact(b.meters) : '';
    const key = `${b.band}|${r}|${label}`;
    let style = this.styleCache.get(key);
    if (!style) {
      const color = BAND_COLORS[b.band];
      style = new Style({
        image: new CircleStyle({
          radius: r, fill: new Fill({ color: rgba(color, 0.85) }), stroke: new Stroke({ color: 'rgba(255,255,255,0.95)', width: 1.5 }),
          declutterMode: 'none',      // circles are always drawn; only their labels take part in decluttering
        }),
        text: label
          ? new Text({ text: label, font: '600 10px system-ui, sans-serif', fill: new Fill({ color: b.band === 'amber' ? '#0b0b0b' : '#ffffff' }) })
          : undefined,
      });
      if (this.styleCache.size > 2000) this.styleCache.clear();
      this.styleCache.set(key, style);
    }
    return style;
  }

  private drawHighlights(): void {
    const src = this.highlightSource;
    src.clear(true);
    const red = BAND_COLORS.red;
    const active = this.commands.activeCluster();
    for (const c of this.commands.clusters()) {
      const isActive = c.id === active;
      const f = c.hull.length >= 4
        ? new Feature({ geometry: new Polygon([c.hull.map((p) => fromLonLat(p))]) })
        : new Feature({ geometry: new Point(fromLonLat([c.lon, c.lat])) });
      f.set('clusterId', c.id);
      f.set('style', c.hull.length >= 4
        ? new Style({
            fill: new Fill({ color: rgba(red, isActive ? 0.16 : 0.06) }),
            stroke: new Stroke({ color: red, width: isActive ? 2.5 : 1.25, lineDash: isActive ? undefined : [6, 4] }),
          })
        : new Style({ image: new CircleStyle({ radius: isActive ? 17 : 13, fill: new Fill({ color: rgba(red, 0.06) }), stroke: new Stroke({ color: red, width: isActive ? 2.5 : 1.5 }) }) }));
      src.addFeature(f);
    }
    const b = this.commands.selectedBuilding();
    if (b?.lon != null && b.lat != null) {
      const f = new Feature({ geometry: new Point(fromLonLat([b.lon, b.lat])) });
      f.set('style', new Style({ image: new CircleStyle({ radius: 19, stroke: new Stroke({ color: 'rgba(11,11,11,0.9)', width: 2.5 }) }) }));
      src.addFeature(f);
    }
    const pin = this.commands.meterPin();
    if (pin) {
      const f = new Feature({ geometry: new Point(fromLonLat([pin.lon, pin.lat])) });
      f.set('style', new Style({
        image: new RegularShape({ points: 4, radius: 10, angle: 0, fill: new Fill({ color: '#1c5cab' }), stroke: new Stroke({ color: '#ffffff', width: 2 }) }),
      }));
      src.addFeature(f);
    }
  }

  // ------------------------------------------------------------------ interaction
  private bubbleAt(pixel: number[]): BubbleDto | undefined {
    const f = this.map?.forEachFeatureAtPixel(pixel, (feat) => feat, { layerFilter: (l) => l === this.bubbleLayer, hitTolerance: 2 });
    return f ? this.current[f.get('idx') as number] : undefined;
  }

  private onHover(pixel: number[]): void {
    const b = this.bubbleAt(pixel);
    this.tooltip.set(b ? { x: pixel[0], y: pixel[1], b } : null);
    this.host().nativeElement.style.cursor = b ? 'pointer' : '';
  }

  private onClick(pixel: number[]): void {
    if (this.drawing() || !this.map) return;
    const b = this.bubbleAt(pixel);
    if (b) {
      if (b.kind === 'building' || b.buildingKey) {
        this.buildingSelected.emit({ key: b.buildingKey ?? b.id, lon: b.lon, lat: b.lat });
      } else {
        const view = this.map.getView();
        view.animate({ center: fromLonLat([b.lon, b.lat]), zoom: Math.min(20, Math.round(view.getZoom() ?? 5) + 2), duration: 500 });
      }
      return;
    }
    const cluster = this.map.forEachFeatureAtPixel(pixel, (feat) => feat, { layerFilter: (l) => l === this.highlightLayer });
    const id = cluster?.get('clusterId') as string | undefined;
    if (id) this.clusterSelected.emit(id);
  }

  private applyFocus(f: MapFocus): void {
    const view = this.map?.getView();
    if (!view) return;
    if (f.kind === 'point') {
      view.animate({ center: fromLonLat([f.lon, f.lat]), zoom: f.zoom, duration: 800 });
    } else {
      view.fit(transformExtent(f.extent, 'EPSG:4326', 'EPSG:3857'), { padding: [60, 60, 60, 60], duration: 800, maxZoom: 17 });
    }
  }

  private startDraw(tool: 'polygon' | 'lasso' | 'rectangle' | 'clear'): void {
    if (!this.map) return;
    if (this.draw) {
      this.map.removeInteraction(this.draw);
      this.draw = undefined;
    }
    this.sketchSource.clear();
    if (tool === 'clear') {
      this.drawing.set(false);
      this.commands.polygon.set(null);
      return;
    }
    this.tooltip.set(null);
    this.drawing.set(true);
    this.draw = tool === 'rectangle'
      ? new Draw({ source: this.sketchSource, type: 'Circle', geometryFunction: createBox(), freehand: true })
      : new Draw({ source: this.sketchSource, type: 'Polygon', freehand: tool === 'lasso' });
    this.draw.on('drawend', (e) => {
      const geom = e.feature.getGeometry() as Polygon;
      const ring = geom.getCoordinates()[0].map((c) => toLonLat(c).map((v) => Number(v.toFixed(7))));
      this.drawing.set(false);
      this.commands.polygon.set(ring);
      // finish the interaction after the current event loop (OpenLayers is still dispatching drawend)
      setTimeout(() => {
        if (this.draw) this.map?.removeInteraction(this.draw);
        this.draw = undefined;
      });
    });
    this.map.addInteraction(this.draw);
  }
}
