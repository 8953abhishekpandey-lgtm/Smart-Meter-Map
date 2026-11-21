import { Injectable, computed, signal } from '@angular/core';
import { AppConfigService } from '../config/app-config';
import { AuthConfigDto } from '../api/models';
import { inject } from '@angular/core';

interface StoredToken { accessToken: string; expiresAt: number; refreshToken?: string; }

const TOKEN_KEY = 'smartmeter.token';
const PKCE_KEY = 'smartmeter.pkce';

/**
 * Authentication scaffolding.
 *  - mode "None": API runs with Auth:Enabled=false (dev toggle) - no token.
 *  - mode "Dev":  short-lived token from POST /api/v1/auth/dev-token (local development only).
 *  - mode "Oidc": Authorization Code + PKCE against Auth:Authority (Entra ID, Keycloak, Okta ...).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly config = inject(AppConfigService);
  private readonly settings = signal<AuthConfigDto>({ enabled: false, mode: 'None' });
  private readonly stored = signal<StoredToken | null>(null);

  readonly mode = computed(() => this.settings().mode);
  readonly enabled = computed(() => this.settings().enabled);
  readonly userName = computed(() => {
    const t = this.stored()?.accessToken;
    if (!t) return this.enabled() ? null : 'Local (auth disabled)';
    try {
      const payload = JSON.parse(atob(t.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
      return payload.name ?? payload.preferred_username ?? payload.unique_name ?? payload.sub ?? 'user';
    } catch {
      return 'user';
    }
  });

  token(): string | null {
    const t = this.stored();
    return t && t.expiresAt > Date.now() + 30_000 ? t.accessToken : null;
  }

  async init(): Promise<void> {
    try {
      const r = await fetch(`${this.config.value.apiBaseUrl}/api/v1/auth/config`);
      if (r.ok) this.settings.set(await r.json());
    } catch {
      /* API unreachable: keep anonymous, the error interceptor reports failures */
    }
    const s = this.settings();
    if (!s.enabled) return;
    const cached = sessionStorage.getItem(TOKEN_KEY);
    if (cached) this.stored.set(JSON.parse(cached));
    if (s.mode === 'Dev' && !this.token()) await this.devLogin();
    if (s.mode === 'Oidc') {
      if (await this.completeOidcCallback()) return;
      if (!this.token()) await this.login();
    }
  }

  async login(): Promise<void> {
    const s = this.settings();
    if (s.mode === 'Dev') return this.devLogin();
    if (s.mode !== 'Oidc' || !s.authority || !s.clientId) return;
    const discovery = await (await fetch(`${s.authority.replace(/\/$/, '')}/.well-known/openid-configuration`)).json();
    const verifier = randomString(64);
    const challenge = await sha256Base64Url(verifier);
    const state = randomString(24);
    sessionStorage.setItem(PKCE_KEY, JSON.stringify({ verifier, state, tokenEndpoint: discovery.token_endpoint, returnUrl: location.pathname + location.search }));
    const params = new URLSearchParams({
      response_type: 'code',
      client_id: s.clientId,
      redirect_uri: `${location.origin}/`,
      scope: s.scope ?? 'openid profile',
      code_challenge: challenge,
      code_challenge_method: 'S256',
      state,
    });
    location.assign(`${discovery.authorization_endpoint}?${params}`);
  }

  logout(): void {
    sessionStorage.removeItem(TOKEN_KEY);
    this.stored.set(null);
  }

  private async devLogin(): Promise<void> {
    const r = await fetch(`${this.config.value.apiBaseUrl}/api/v1/auth/dev-token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ user: 'dev.operator', roles: ['viewer', 'ingestion-operator', 'admin'] }),
    });
    if (!r.ok) return;
    const body = await r.json();
    this.save({ accessToken: body.accessToken, expiresAt: Date.now() + body.expiresInSeconds * 1000 });
  }

  private async completeOidcCallback(): Promise<boolean> {
    const url = new URL(location.href);
    const code = url.searchParams.get('code');
    const pkce = sessionStorage.getItem(PKCE_KEY);
    if (!code || !pkce) return false;
    const { verifier, state, tokenEndpoint, returnUrl } = JSON.parse(pkce);
    sessionStorage.removeItem(PKCE_KEY);
    if (url.searchParams.get('state') !== state) return false;
    const body = new URLSearchParams({
      grant_type: 'authorization_code',
      code,
      redirect_uri: `${location.origin}/`,
      client_id: this.settings().clientId ?? '',
      code_verifier: verifier,
    });
    const r = await fetch(tokenEndpoint, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
    if (!r.ok) return false;
    const t = await r.json();
    this.save({ accessToken: t.access_token, expiresAt: Date.now() + (t.expires_in ?? 3600) * 1000, refreshToken: t.refresh_token });
    history.replaceState(null, '', returnUrl ?? '/');
    return true;
  }

  private save(t: StoredToken): void {
    sessionStorage.setItem(TOKEN_KEY, JSON.stringify(t));
    this.stored.set(t);
  }
}

function randomString(length: number): string {
  const bytes = crypto.getRandomValues(new Uint8Array(length));
  return Array.from(bytes, (b) => 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~'[b % 66]).join('');
}

async function sha256Base64Url(input: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(input));
  return btoa(String.fromCharCode(...new Uint8Array(digest))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
