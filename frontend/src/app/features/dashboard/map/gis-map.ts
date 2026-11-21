import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, input, isDevMode, output, signal, untracked, viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import esriConfig from '@arcgis/core/config';
import EsriMap from '@arcgis/core/Map';
import Graphic from '@arcgis/core/Graphic';
import MapView from '@arcgis/core/views/MapView';
import FeatureLayer from '@arcgis/core/layers/FeatureLayer';
import GraphicsLayer from '@arcgis/core/layers/GraphicsLayer';
import LabelClass from '@arcgis/core/layers/support/LabelClass';
import Point from '@arcgis/core/geometry/Point';
import Polygon from '@arcgis/core/geometry/Polygon';
import Extent from '@arcgis/core/geometry/Extent';
import SpatialReference from '@arcgis/core/geometry/SpatialReference';
import * as webMercatorUtils from '@arcgis/core/geometry/support/webMercatorUtils';
import * as reactiveUtils from '@arcgis/core/core/reactiveUtils';
import UniqueValueRenderer from '@arcgis/core/renderers/UniqueValueRenderer';
import SizeVariable from '@arcgis/core/renderers/visualVariables/SizeVariable';
import SimpleMarkerSymbol from '@arcgis/core/symbols/SimpleMarkerSymbol';
import SimpleFillSymbol from '@arcgis/core/symbols/SimpleFillSymbol';
import SimpleLineSymbol from '@arcgis/core/symbols/SimpleLineSymbol';
import TextSymbol from '@arcgis/core/symbols/TextSymbol';
import SketchViewModel from '@arcgis/core/widgets/Sketch/SketchViewModel';
import TileInfo from '@arcgis/core/layers/support/TileInfo';
import { catchError, debounceTime, filter, of, switchMap } from 'rxjs';
import { BubbleDto, BubblesDto, FleetQuery, HealthBand } from '../../../core/api/models';
import { SmartMeterApi } from '../../../core/api/smart-meter-api';
import { AppConfigService } from '../../../core/config/app-config';
import { MapCommands, MapFocus } from '../../../core/state/map-commands';
import { BAND_COLORS, BAND_LABELS, compact, fmt } from '../../../core/ui/format';
import { BASEMAP_OPTIONS, BasemapKey, createBasemap } from './basemaps';

const WGS84 = SpatialReference.WGS84;
const BANDS: HealthBand[] = ['green', 'amber', 'red', 'none'];

function rgba(hex: string, a: number): number[] {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255, a];
}

/**
 * ArcGIS Maps SDK map. Never receives raw meters: on every stationary viewport it requests
 * server-side aggregated bubbles (grid pyramid when zoomed out, buildings at street level).
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

  protected readonly basemaps = BASEMAP_OPTIONS;
  protected readonly bands = BANDS.filter((b) => b !== 'none').map((b) => ({ band: b, color: BAND_COLORS[b], label: BAND_LABELS[b] }));
  protected readonly basemapKey = signal<BasemapKey>('streets');
  protected readonly loading = signal(false);
  protected readonly drawing = signal(false);
  protected readonly tooltip = signal<{ x: number; y: number; b: BubbleDto } | null>(null);
  protected readonly info = signal<{ mode: string; level?: number | null; count: number; truncated: boolean; zoom: number } | null>(null);
  protected readonly fmt = fmt;
  protected readonly compact = compact;

  private readonly viewReady = signal(false);
  private readonly viewport = signal<{ bbox: number[]; zoom: number } | null>(null);
  private view?: MapView;
  private map?: EsriMap;
  private bubbleLayer?: FeatureLayer;
  private highlightLayer?: GraphicsLayer;
  private sketchLayer?: GraphicsLayer;
  private sketch?: SketchViewModel;
  private renderChain: Promise<void> = Promise.resolve();
  private current: BubbleDto[] = [];
  /** Labels need font glyphs from the fonts CDN; when it is unreachable (air-gapped) labels would stall the layer view. */
  private labelsEnabled = false;
  private hoverPending = false;

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => this.init(destroyRef));

    // viewport x filters x data version -> bubbles (stale requests are cancelled by switchMap)
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
      if (res) this.renderChain = this.renderChain.then(() => this.render(res)).catch(() => undefined);
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
      if (this.viewReady() && this.map) untracked(() => (this.map!.basemap = createBasemap(key, !!this.config.value.arcgisApiKey) as never));
    });
  }

  protected setBasemap(key: BasemapKey): void { this.basemapKey.set(key); }
  protected draw(tool: 'polygon' | 'lasso' | 'rectangle' | 'clear'): void { this.commands.draw(tool); }
  protected home(): void {
    const c = this.config.value;
    void this.view?.goTo({ center: c.defaultCenter, zoom: c.defaultZoom }, { duration: 600 }).catch(() => undefined);
  }

  // ------------------------------------------------------------------ setup
  private init(destroyRef: DestroyRef): void {
    const cfg = this.config.value;
    esriConfig.assetsPath = cfg.arcgisAssetsPath;
    if (cfg.arcgisApiKey) esriConfig.apiKey = cfg.arcgisApiKey;
    if (cfg.arcgisFontsUrl) esriConfig.fontsUrl = cfg.arcgisFontsUrl;
    void this.probeFonts();

    this.bubbleLayer = new FeatureLayer({
      title: 'Meter communication',
      source: [],
      objectIdField: 'oid',
      geometryType: 'point',
      spatialReference: WGS84,
      fields: [
        { name: 'oid', type: 'oid' },
        { name: 'idx', type: 'integer' },
        { name: 'kind', type: 'string' },
        { name: 'band', type: 'string' },
        { name: 'meters', type: 'double' },
        { name: 'failing', type: 'double' },
        { name: 'failurePct', type: 'double' },
      ],
      outFields: ['*'],
      renderer: this.buildRenderer(1, 100, false),
      popupEnabled: false,
      legendEnabled: false,
    });
    this.highlightLayer = new GraphicsLayer({ title: 'Highlights' });
    this.sketchLayer = new GraphicsLayer({ title: 'Selection' });
    this.map = new EsriMap({ basemap: createBasemap(this.basemapKey(), !!cfg.arcgisApiKey) as never, layers: [this.bubbleLayer, this.highlightLayer, this.sketchLayer] });

    this.view = new MapView({
      container: this.host().nativeElement,
      map: this.map,
      center: cfg.defaultCenter,
      zoom: cfg.defaultZoom,
      spatialReference: SpatialReference.WebMercator,
      // explicit Web Mercator LODs: zoom levels keep working even if the basemap provider is unreachable
      constraints: { lods: TileInfo.create({ spatialReference: SpatialReference.WebMercator }).lods, minZoom: 3, maxZoom: 20, rotationEnabled: false },
      popupEnabled: false,
    });
    this.view.ui.components = ['zoom'];   // attribution is always shown by the SDK (required by the basemap terms)

    this.sketch = new SketchViewModel({
      view: this.view,
      layer: this.sketchLayer,
      updateOnGraphicClick: false,
      polygonSymbol: new SimpleFillSymbol({
        color: rgba('#2a78d6', 0.1),
        outline: new SimpleLineSymbol({ color: rgba('#2a78d6', 1), width: 2 }),
      }),
    });
    this.sketch.on('create', (e) => {
      if (e.state === 'complete' && e.graphic?.geometry) {
        const geo = webMercatorUtils.webMercatorToGeographic(e.graphic.geometry) as Polygon;
        this.drawing.set(false);
        this.commands.polygon.set(geo.rings[0].map((p) => [Number(p[0].toFixed(7)), Number(p[1].toFixed(7))]));
      } else if (e.state === 'cancel') {
        this.drawing.set(false);
      }
    });

    reactiveUtils.when(() => this.view?.stationary, () => this.emitViewport(), { initial: true });
    this.view.on('pointer-move', (e) => this.onHover(e.x, e.y));
    this.view.on('pointer-leave', () => this.tooltip.set(null));
    this.view.on('click', (e) => void this.onClick(e.x, e.y));
    void this.view.when(() => this.viewReady.set(true), () => this.viewReady.set(true));
    if (isDevMode()) (window as unknown as Record<string, unknown>)['__smMap'] = this;   // e2e / debugging hook
    destroyRef.onDestroy(() => this.view?.destroy());
  }

  private async probeFonts(): Promise<void> {
    try {
      const ctrl = new AbortController();
      const timer = setTimeout(() => ctrl.abort(), 3000);
      const r = await fetch(`${esriConfig.fontsUrl}/arial-unicode-ms-bold/0-255.pbf`, { signal: ctrl.signal });
      clearTimeout(timer);
      this.labelsEnabled = r.ok;
    } catch {
      this.labelsEnabled = false;
    }
  }

  private emitViewport(): void {
    const view = this.view;
    if (!view?.extent) return;
    const geo = webMercatorUtils.webMercatorToGeographic(view.extent) as Extent;
    let [xmin, xmax] = [geo.xmin, geo.xmax];
    if (xmax - xmin >= 360 || xmin > xmax) [xmin, xmax] = [-180, 180];
    const bbox = [Math.max(-180, xmin), Math.max(-85, geo.ymin), Math.min(180, xmax), Math.min(85, geo.ymax)];
    // view.zoom is -1 when no tiling scheme is known; derive it from the scale (Web Mercator, 96 dpi)
    const zoom = view.zoom >= 0 ? view.zoom : Math.log2(591_657_527.591555 / view.scale);
    this.viewport.set({ bbox, zoom: Math.max(0, Math.min(22, Math.round(zoom))) });
    this.commands.viewport.set(bbox);
  }

  // ------------------------------------------------------------------ rendering
  private buildRenderer(minMeters: number, maxMeters: number, buildings: boolean): UniqueValueRenderer {
    const marker = (hex: string) => new SimpleMarkerSymbol({
      style: 'circle',
      color: rgba(hex, 0.85),
      outline: new SimpleLineSymbol({ color: [255, 255, 255, 0.95], width: 1.5 }),
    });
    return new UniqueValueRenderer({
      field: 'band',
      defaultSymbol: marker(BAND_COLORS.none),
      uniqueValueInfos: BANDS.map((b) => ({ value: b, label: BAND_LABELS[b], symbol: marker(BAND_COLORS[b]) })),
      visualVariables: [
        new SizeVariable({
          field: 'meters',
          minDataValue: minMeters,
          maxDataValue: Math.max(minMeters + 1, maxMeters),
          minSize: buildings ? 8 : 10,
          maxSize: buildings ? 34 : 52,
        }),
      ],
    });
  }

  private labels(threshold: number): LabelClass[] {
    const text = (color: number[]) => new TextSymbol({
      color,
      font: { size: 9, weight: 'bold', family: 'sans-serif' },
    });
    const expression = 'IIF($feature.meters >= 1000, Text(Round($feature.meters / 1000, 1)) + "k", Text($feature.meters))';
    return [
      new LabelClass({ labelExpressionInfo: { expression }, labelPlacement: 'center-center', symbol: text([255, 255, 255, 1]),
                       where: `meters >= ${threshold} AND band <> 'amber'` }),
      new LabelClass({ labelExpressionInfo: { expression }, labelPlacement: 'center-center', symbol: text([11, 11, 11, 1]),
                       where: `meters >= ${threshold} AND band = 'amber'` }),
    ];
  }

  private async render(res: BubblesDto): Promise<void> {
    const layer = this.bubbleLayer;
    if (!layer) return;
    const values = res.bubbles.map((b) => b.meters);
    const min = values.length ? Math.min(...values) : 1;
    const max = values.length ? Math.max(...values) : 100;
    layer.renderer = this.buildRenderer(min, max, res.mode === 'building');
    layer.labelingInfo = this.labelsEnabled ? this.labels(Math.max(10, Math.round(max * 0.12))) : [];

    // The client-side layer assigns its own objectIds, so features carry their index into `current`.
    const adds = res.bubbles.map((b, idx) => new Graphic({
      geometry: new Point({ longitude: b.lon, latitude: b.lat, spatialReference: WGS84 }),
      attributes: { idx, kind: b.kind, band: b.band, meters: b.meters, failing: b.failing, failurePct: b.failurePct },
    }));
    const existing = await layer.queryObjectIds();
    if (existing?.length) await layer.applyEdits({ deleteFeatures: existing.map((objectId) => ({ objectId: Number(objectId) })) });
    this.current = res.bubbles;
    if (adds.length) await layer.applyEdits({ addFeatures: adds });
    this.info.set({ mode: res.mode, level: res.level, count: res.bubbles.length, truncated: res.truncated, zoom: res.zoom });
  }

  private drawHighlights(): void {
    const layer = this.highlightLayer;
    if (!layer) return;
    layer.removeAll();
    const active = this.commands.activeCluster();
    for (const c of this.commands.clusters()) {
      const isActive = c.id === active;
      const red = BAND_COLORS.red;
      if (c.hull.length >= 4) {
        layer.add(new Graphic({
          geometry: new Polygon({ rings: [c.hull], spatialReference: WGS84 }),
          attributes: { clusterId: c.id },
          symbol: new SimpleFillSymbol({
            color: rgba(red, isActive ? 0.16 : 0.06),
            outline: new SimpleLineSymbol({ color: rgba(red, 1), width: isActive ? 2.5 : 1.25, style: isActive ? 'solid' : 'dash' }),
          }),
        }));
      } else {
        layer.add(new Graphic({
          geometry: new Point({ longitude: c.lon, latitude: c.lat, spatialReference: WGS84 }),
          attributes: { clusterId: c.id },
          symbol: new SimpleMarkerSymbol({ style: 'circle', size: isActive ? 34 : 26, color: rgba(red, 0.06),
                                           outline: new SimpleLineSymbol({ color: rgba(red, 1), width: isActive ? 2.5 : 1.5 }) }),
        }));
      }
    }
    const b = this.commands.selectedBuilding();
    if (b?.lon != null && b.lat != null) {
      layer.add(new Graphic({
        geometry: new Point({ longitude: b.lon, latitude: b.lat, spatialReference: WGS84 }),
        symbol: new SimpleMarkerSymbol({ style: 'circle', size: 38, color: [0, 0, 0, 0],
                                         outline: new SimpleLineSymbol({ color: [11, 11, 11, 0.9], width: 2.5 }) }),
      }));
    }
    const pin = this.commands.meterPin();
    if (pin) {
      layer.add(new Graphic({
        geometry: new Point({ longitude: pin.lon, latitude: pin.lat, spatialReference: WGS84 }),
        symbol: new SimpleMarkerSymbol({ style: 'diamond', size: 18, color: rgba('#1c5cab', 1),
                                         outline: new SimpleLineSymbol({ color: [255, 255, 255, 1], width: 2 }) }),
      }));
    }
  }

  // ------------------------------------------------------------------ interaction
  private onHover(x: number, y: number): void {
    if (this.hoverPending || !this.view || !this.bubbleLayer || this.drawing()) return;
    this.hoverPending = true;
    requestAnimationFrame(async () => {
      try {
        const hit = await this.view!.hitTest({ x, y }, { include: [this.bubbleLayer!] });
        const g = hit.results.find((r) => r.type === 'graphic');
        const idx = g && 'graphic' in g ? (g.graphic.attributes?.['idx'] as number | undefined) : undefined;
        const b = idx !== undefined ? this.current[idx] : undefined;
        this.tooltip.set(b ? { x, y, b } : null);
        this.host().nativeElement.style.cursor = b ? 'pointer' : '';
      } finally {
        this.hoverPending = false;
      }
    });
  }

  private async onClick(x: number, y: number): Promise<void> {
    if (!this.view || this.drawing()) return;
    const hit = await this.view.hitTest({ x, y }, { include: [this.bubbleLayer!, this.highlightLayer!] });
    for (const r of hit.results) {
      if (r.type !== 'graphic' || !('graphic' in r)) continue;
      const attrs = r.graphic.attributes ?? {};
      if (attrs['clusterId']) { this.clusterSelected.emit(attrs['clusterId']); return; }
      const b = attrs['idx'] !== undefined ? this.current[attrs['idx']] : undefined;
      if (!b) continue;
      if (b.kind === 'building' || b.buildingKey) {
        this.buildingSelected.emit({ key: b.buildingKey ?? b.id, lon: b.lon, lat: b.lat });
      } else {
        void this.view.goTo({ center: [b.lon, b.lat], zoom: Math.min(20, Math.round(this.view.zoom) + 2) }, { duration: 500 }).catch(() => undefined);
      }
      return;
    }
  }

  private applyFocus(f: MapFocus): void {
    if (!this.view) return;
    const target = f.kind === 'point'
      ? { center: [f.lon, f.lat], zoom: f.zoom }
      : new Extent({ xmin: f.extent[0], ymin: f.extent[1], xmax: f.extent[2], ymax: f.extent[3], spatialReference: WGS84 }).expand(1.25);
    void this.view.goTo(target, { duration: 800 }).catch(() => undefined);
  }

  private startDraw(tool: 'polygon' | 'lasso' | 'rectangle' | 'clear'): void {
    if (!this.sketch || !this.sketchLayer) return;
    this.sketch.cancel();
    this.sketchLayer.removeAll();
    if (tool === 'clear') {
      this.drawing.set(false);
      this.commands.polygon.set(null);
      return;
    }
    this.tooltip.set(null);
    this.drawing.set(true);
    if (tool === 'rectangle') void this.sketch.create('rectangle');
    else void this.sketch.create('polygon', { mode: tool === 'lasso' ? 'freehand' : 'click' });
  }
}
