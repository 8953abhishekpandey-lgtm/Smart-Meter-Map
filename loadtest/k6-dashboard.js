// Smart Meter GIS - load test (k6, native binary).  BASE_URL=http://localhost:8080 VUS=50 HOLD=3m k6 run loadtest/k6-dashboard.js
// Models operators panning/zooming the map with random filters, opening buildings, plus a trickle of heavy analytics.
import http from 'k6/http';
import { check, group, sleep } from 'k6';

const BASE = (__ENV.BASE_URL || 'http://localhost:8080') + '/api/v1';
const TOKEN = __ENV.TOKEN || '';
const headers = TOKEN ? { Authorization: `Bearer ${TOKEN}` } : {};

export const options = {
  scenarios: {
    dashboard: {
      executor: 'ramping-vus',
      exec: 'dashboard',
      startVUs: 5,
      stages: [
        { duration: __ENV.RAMP || '30s', target: Number(__ENV.VUS || 50) },
        { duration: __ENV.HOLD || '2m', target: Number(__ENV.VUS || 50) },
        { duration: '15s', target: 0 },
      ],
    },
    analytics: {
      executor: 'constant-arrival-rate',
      exec: 'analytics',
      rate: Number(__ENV.ANALYTICS_RPM || 6),
      timeUnit: '1m',
      duration: __ENV.HOLD || '2m',
      preAllocatedVUs: 4,
    },
  },
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    'http_req_duration{kind:dashboard}': ['p(95)<500', 'p(99)<1500'],
    'http_req_duration{kind:map}': ['p(95)<800'],
    'http_req_duration{kind:analytics}': ['p(95)<5000'],
  },
};

let meta;
export function setup() {
  meta = http.get(`${BASE}/meta`, { headers }).json();
  return { zones: meta.zones.map((z) => z.id), nsps: meta.nsps.map((n) => n.id), mfrs: meta.manufacturers.map((m) => m.id), window: meta.window };
}

const cities = [
  { lon: 77.21, lat: 28.62 }, { lon: 72.88, lat: 19.08 }, { lon: 77.60, lat: 12.97 }, { lon: 78.45, lat: 17.40 },
];
const pick = (a) => a[Math.floor(Math.random() * a.length)];

function filters(d) {
  const p = [];
  if (Math.random() < 0.3) p.push(`zone=${pick(d.zones)}`);
  if (Math.random() < 0.2) p.push(`nsp=${pick(d.nsps)}`);
  if (Math.random() < 0.1) p.push(`mfr=${pick(d.mfrs)}`);
  if (Math.random() < 0.15) p.push(`health=${pick(['dead', 'intermittent', 'allDay', 'todayFail', 'last7Days'])}`);
  if (Math.random() < 0.2) {
    const day = new Date(d.window.end);
    day.setDate(day.getDate() - Math.floor(Math.random() * d.window.days));
    p.push(`date=${day.toISOString().slice(0, 10)}`);
  }
  return p.join('&');
}

function viewport() {
  const zoom = pick([5, 6, 8, 10, 11, 12, 13, 14, 15, 16, 17]);
  const c = pick(cities);
  const w = 360 / Math.pow(2, zoom) * 5.5, h = w * 0.55;      // ~1400x800 px viewport
  const jx = (Math.random() - 0.5) * w, jy = (Math.random() - 0.5) * h;
  return { zoom, bbox: [c.lon + jx - w / 2, c.lat + jy - h / 2, c.lon + jx + w / 2, c.lat + jy + h / 2].map((v) => v.toFixed(5)).join(',') };
}

export function dashboard(d) {
  const f = filters(d);
  group('dashboard', () => {
    const r = http.batch([
      ['GET', `${BASE}/dashboard/kpis?${f}`, null, { headers, tags: { kind: 'dashboard', name: 'kpis' } }],
      ['GET', `${BASE}/dashboard/filter-options?${f}`, null, { headers, tags: { kind: 'dashboard', name: 'options' } }],
      ['GET', `${BASE}/dashboard/trend?${f}`, null, { headers, tags: { kind: 'dashboard', name: 'trend' } }],
    ]);
    r.forEach((x) => check(x, { 'dashboard 200': (res) => res.status === 200 }));
  });
  for (let i = 0; i < 4; i++) {                                 // pan / zoom
    const v = viewport();
    const r = http.get(`${BASE}/map/bubbles?bbox=${v.bbox}&zoom=${v.zoom}&${f}`, { headers, tags: { kind: 'map', name: 'bubbles' } });
    check(r, { 'bubbles 200': (res) => res.status === 200 });
    if (v.zoom >= 15 && r.status === 200) {
      const b = r.json('bubbles.0.buildingKey');
      if (b) {
        const key = encodeURIComponent(b);
        http.get(`${BASE}/buildings/${key}`, { headers, tags: { kind: 'dashboard', name: 'building' } });
        http.get(`${BASE}/buildings/${key}/meters?pageSize=25`, { headers, tags: { kind: 'dashboard', name: 'building-meters' } });
      }
    }
    sleep(0.5 + Math.random());
  }
  if (Math.random() < 0.2) http.get(`${BASE}/meters/search?q=SM0000${Math.floor(Math.random() * 90 + 10)}`, { headers, tags: { kind: 'dashboard', name: 'search' } });
  sleep(1);
}

export function analytics(d) {
  const f = filters(d);
  const which = Math.random();
  let r;
  if (which < 0.4) {
    r = http.post(`${BASE}/analytics/failure-clusters?${f}`, JSON.stringify({ minMeters: 300, minFailurePct: 25, epsMeters: 400 }),
      { headers: { ...headers, 'Content-Type': 'application/json' }, tags: { kind: 'analytics', name: 'clusters' } });
  } else if (which < 0.7) {
    const c = pick(cities), s = 0.02 + Math.random() * 0.05;
    const ring = [[c.lon - s, c.lat - s], [c.lon + s, c.lat - s], [c.lon + s, c.lat + s], [c.lon - s, c.lat + s], [c.lon - s, c.lat - s]];
    r = http.post(`${BASE}/analytics/polygon?${f}`, JSON.stringify({ geometry: { type: 'Polygon', coordinates: [ring] } }),
      { headers: { ...headers, 'Content-Type': 'application/json' }, tags: { kind: 'analytics', name: 'polygon' } });
  } else {
    r = http.get(`${BASE}/analytics/sim-switch?${f}&pageSize=50`, { headers, tags: { kind: 'analytics', name: 'sim-switch' } });
  }
  check(r, { 'analytics 200': (res) => res.status === 200 });
}
