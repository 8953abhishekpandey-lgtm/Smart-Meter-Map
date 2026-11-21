import { Injectable, signal } from '@angular/core';

export interface Toast { id: number; kind: 'info' | 'success' | 'error'; title: string; detail?: string; }

@Injectable({ providedIn: 'root' })
export class NotificationService {
  private seq = 0;
  readonly toasts = signal<Toast[]>([]);

  info(title: string, detail?: string): void { this.push('info', title, detail); }
  success(title: string, detail?: string): void { this.push('success', title, detail); }
  error(title: string, detail?: string): void {
    // de-duplicate bursts (e.g. many tiles failing at once)
    if (this.toasts().some((t) => t.title === title && t.detail === detail)) return;
    this.push('error', title, detail, 8000);
  }

  dismiss(id: number): void { this.toasts.update((list) => list.filter((t) => t.id !== id)); }

  private push(kind: Toast['kind'], title: string, detail?: string, ttl = 4500): void {
    const id = ++this.seq;
    this.toasts.update((list) => [...list.slice(-3), { id, kind, title, detail }]);
    setTimeout(() => this.dismiss(id), ttl);
  }
}
