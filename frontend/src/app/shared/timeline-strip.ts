import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { shortDate } from '../core/ui/format';

/** 30-day communication strip: one cell per day ('1' communicated, '0' failed, '-' not expected). */
@Component({
  selector: 'sm-timeline-strip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="strip" role="img" [attr.aria-label]="summary()">
      @for (c of cells(); track $index) {
        <span class="cell" [class.ok]="c.s === '1'" [class.fail]="c.s === '0'" [class.na]="c.s === '-'"
              [class.selected]="c.date === selectedDate()" [title]="c.title"></span>
      }
    </span>
  `,
  styles: [`
    .strip { display: inline-flex; gap: 1px; }
    .cell { width: 4px; height: 14px; border-radius: 1px; background: #e1e0d9; }
    .cell.ok { background: #2a78d6; }
    .cell.fail { background: #d03b3b; }
    .cell.na { background: repeating-linear-gradient(45deg, #e1e0d9 0 2px, #f7f6f2 2px 4px); }
    .cell.selected { outline: 1.5px solid #0b0b0b; outline-offset: 1px; }
  `],
})
export class TimelineStrip {
  readonly timeline = input.required<string>();
  readonly windowStart = input.required<string>();
  readonly selectedDate = input<string | null>(null);

  protected readonly cells = computed(() => {
    const start = new Date(this.windowStart() + 'T00:00:00');
    return [...this.timeline()].map((s, i) => {
      const d = new Date(start);
      d.setDate(start.getDate() + i);
      const date = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
      const state = s === '1' ? 'communicated' : s === '0' ? 'failed' : 'not installed';
      return { s, date, title: `${shortDate(date)}: ${state}` };
    });
  });

  protected readonly summary = computed(() => {
    const t = this.timeline();
    return `${[...t].filter((c) => c === '1').length} of ${[...t].filter((c) => c !== '-').length} expected days communicated`;
  });
}
