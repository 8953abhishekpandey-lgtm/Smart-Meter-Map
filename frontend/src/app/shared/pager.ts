import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { fmt } from '../core/ui/format';

@Component({
  selector: 'sm-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pager">
      <span class="info">{{ from() }}–{{ to() }} of {{ fmt(total()) }}</span>
      <button type="button" (click)="pageChange.emit(1)" [disabled]="page() <= 1" aria-label="First page">«</button>
      <button type="button" (click)="pageChange.emit(page() - 1)" [disabled]="page() <= 1" aria-label="Previous page">‹</button>
      <span class="page">Page {{ page() }} / {{ pages() }}</span>
      <button type="button" (click)="pageChange.emit(page() + 1)" [disabled]="page() >= pages()" aria-label="Next page">›</button>
      <button type="button" (click)="pageChange.emit(pages())" [disabled]="page() >= pages()" aria-label="Last page">»</button>
    </div>
  `,
  styles: [`
    .pager { display: flex; align-items: center; gap: 6px; justify-content: flex-end; font-size: 13px; padding: 8px 0; }
    .info { margin-right: auto; color: var(--ink-muted); }
    button { min-width: 28px; height: 28px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface); cursor: pointer; }
    button:disabled { opacity: .4; cursor: default; }
    .page { padding: 0 6px; }
  `],
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly total = input.required<number>();
  readonly pageChange = output<number>();
  protected readonly fmt = fmt;
  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
  protected readonly from = computed(() => (this.total() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1));
  protected readonly to = computed(() => Math.min(this.total(), this.page() * this.pageSize()));
}
