import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

export interface Crumb { label: string; link?: unknown[]; }

@Component({
  selector: 'sm-crumbs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <nav class="crumbs" aria-label="Breadcrumb">
      @for (c of items(); track $index; let last = $last) {
        @if (c.link && !last) { <a [routerLink]="c.link">{{ c.label }}</a><span class="sep" aria-hidden="true">›</span> }
        @else { <span [attr.aria-current]="last ? 'page' : null" class="cur">{{ c.label }}</span> }
      }
    </nav>
  `,
  styles: [`.crumbs { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 13px; margin-bottom: 12px; }
            a { color: var(--accent); text-decoration: none; } a:hover { text-decoration: underline; }
            .sep { color: var(--ink-muted); } .cur { color: var(--ink-secondary); font-weight: 600; }`],
})
export class Crumbs {
  readonly items = input.required<Crumb[]>();
}
