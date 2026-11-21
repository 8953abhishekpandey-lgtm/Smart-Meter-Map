import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, inject, input, model, signal } from '@angular/core';
import { FilterOption } from '../core/api/models';
import { compact } from '../core/ui/format';

/** Searchable multi-select with per-option meter counts (cascading filters). */
@Component({
  selector: 'sm-multi-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="trigger" [class.active]="selected().length > 0" (click)="open.set(!open())"
            [attr.aria-expanded]="open()" [attr.aria-label]="label() + ' filter'">
      <span class="label">{{ label() }}</span>
      <span class="value">{{ summary() }}</span>
      <span class="caret" aria-hidden="true">▾</span>
    </button>
    @if (open()) {
      <div class="panel" role="listbox" [attr.aria-label]="label()">
        <input class="search" type="search" placeholder="Search {{ label().toLowerCase() }}…" [value]="term()"
               (input)="term.set($any($event.target).value)" />
        <div class="options">
          @for (o of visible(); track o.id) {
            <label class="option" [class.empty]="o.meters === 0">
              <input type="checkbox" [checked]="isSelected(o.id)" (change)="toggle(o.id)" />
              <span class="name">{{ o.name }}</span>
              <span class="count">{{ compact(o.meters) }}</span>
            </label>
          } @empty {
            <div class="none">No matches</div>
          }
        </div>
        <div class="footer">
          <button type="button" class="link" (click)="selected.set([])" [disabled]="!selected().length">Clear</button>
          <button type="button" class="link" (click)="open.set(false)">Done</button>
        </div>
      </div>
    }
  `,
  styleUrl: './multi-select.scss',
})
export class MultiSelect {
  private readonly host = inject(ElementRef<HTMLElement>);
  readonly label = input.required<string>();
  readonly options = input<FilterOption[]>([]);
  readonly selected = model<number[]>([]);

  protected readonly open = signal(false);
  protected readonly term = signal('');
  protected readonly compact = compact;

  protected readonly visible = computed(() => {
    const t = this.term().trim().toLowerCase();
    const sel = new Set(this.selected());
    // keep selected options visible even when the cascade reduces them to 0 meters
    return this.options().filter((o) => (!t || o.name.toLowerCase().includes(t)) && (o.meters > 0 || sel.has(o.id)));
  });

  protected readonly summary = computed(() => {
    const sel = this.selected();
    if (!sel.length) return 'All';
    if (sel.length === 1) return this.options().find((o) => o.id === sel[0])?.name ?? '1 selected';
    return `${sel.length} selected`;
  });

  protected isSelected(id: number): boolean { return this.selected().includes(id); }

  protected toggle(id: number): void {
    this.selected.update((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(e: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) this.open.set(false);
  }

  @HostListener('keydown.escape')
  protected onEscape(): void { this.open.set(false); }
}
