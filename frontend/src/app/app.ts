import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { LiveService } from './core/live/live.service';
import { MetaStore } from './core/state/meta.store';
import { NotificationService } from './core/ui/notification.service';
import { longDate } from './core/ui/format';

@Component({
  selector: 'sm-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="app-header">
      <a class="brand" routerLink="/dashboard"><span class="logo" aria-hidden="true">◉</span> Smart Meter <b>GIS</b></a>
      <nav class="nav">
        <a routerLink="/dashboard" routerLinkActive="on">Map dashboard</a>
        <a routerLink="/explore" routerLinkActive="on">Explorer</a>
        <a routerLink="/ingestion" routerLinkActive="on">Data ingestion</a>
      </nav>
      <div class="status">
        @if (meta.window(); as w) {
          <span class="pill" title="Rolling analytics window">{{ w.days }} days · {{ longDate(w.start) }} – {{ longDate(w.end) }}</span>
          <span class="pill" title="Pre-aggregated generation (bumped by every refresh)">v{{ meta.dataVersion() }}</span>
        }
        <span class="live" [class]="live.state()" [title]="'Live updates: ' + live.state()"><i></i>{{ liveLabel() }}</span>
        <span class="user">{{ auth.userName() ?? 'Signed out' }}</span>
      </div>
    </header>
    <main><router-outlet /></main>
    <div class="toasts" aria-live="polite">
      @for (t of notifications.toasts(); track t.id) {
        <div class="toast" [class]="t.kind" (click)="notifications.dismiss(t.id)">
          <b>{{ t.title }}</b>@if (t.detail) { <span>{{ t.detail }}</span> }
        </div>
      }
    </div>
  `,
  styles: [`
    .app-header { height: var(--header-h); display: flex; align-items: center; gap: 24px; padding: 0 16px; background: #14171c; color: #e8eaed; }
    .brand { color: #fff; text-decoration: none; font-size: 16px; display: flex; align-items: center; gap: 6px; }
    .logo { color: #0ca30c; font-size: 18px; }
    .nav { display: flex; gap: 4px; a { color: #b8bec7; text-decoration: none; padding: 6px 10px; border-radius: 6px; font-size: 14px; }
           a.on, a:hover { color: #fff; background: rgba(255,255,255,.08); } }
    .status { margin-left: auto; display: flex; align-items: center; gap: 10px; font-size: 12px; }
    .pill { padding: 3px 8px; border-radius: 999px; background: rgba(255,255,255,.08); color: #d6d9de; }
    .live { display: flex; align-items: center; gap: 5px; color: #b8bec7; i { width: 8px; height: 8px; border-radius: 50%; background: #898781; }
            &.connected i { background: #0ca30c; } &.reconnecting i, &.connecting i { background: #fab219; } }
    .user { color: #d6d9de; }
    main { min-height: calc(100vh - var(--header-h)); }
    .toasts { position: fixed; bottom: 16px; left: 50%; transform: translateX(-50%); display: flex; flex-direction: column; gap: 8px; z-index: 1000; }
    .toast { min-width: 320px; max-width: 560px; padding: 10px 14px; border-radius: 10px; background: #14171c; color: #fff; box-shadow: 0 8px 24px rgba(0,0,0,.25);
             display: flex; flex-direction: column; gap: 2px; cursor: pointer; font-size: 13px; span { color: #c3c2b7; }
             &.error { border-left: 4px solid #d03b3b; } &.success { border-left: 4px solid #0ca30c; } &.info { border-left: 4px solid #2a78d6; } }
  `],
})
export class App {
  protected readonly meta = inject(MetaStore);
  protected readonly live = inject(LiveService);
  protected readonly auth = inject(AuthService);
  protected readonly notifications = inject(NotificationService);
  protected readonly longDate = longDate;
  protected readonly liveLabel = computed(() => ({ connected: 'Live', connecting: 'Connecting', reconnecting: 'Reconnecting', disconnected: 'Offline' })[this.live.state()]);

  constructor() {
    this.live.start();
  }
}
