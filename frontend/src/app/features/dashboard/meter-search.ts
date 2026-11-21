import { ChangeDetectionStrategy, Component, ElementRef, HostListener, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { MeterSearchItem } from '../../core/api/models';
import { SmartMeterApi } from '../../core/api/smart-meter-api';

/** Server-side MeterId prefix search. Selecting a result flies the map to the meter WITHOUT touching filters. */
@Component({
  selector: 'sm-meter-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ms">
      <input type="search" placeholder="Search meter ID…" [value]="term()" (input)="term.set($any($event.target).value)"
             (keydown.enter)="pickFirst()" (focus)="open.set(true)" aria-label="Search meter by ID" autocomplete="off" />
      @if (open() && results().length) {
        <ul class="results" role="listbox">
          @for (r of results(); track r.meterId) {
            <li role="option" (mousedown)="pick(r)"><b>{{ r.meterId }}</b><span>{{ r.zone }} · {{ r.address }}</span></li>
          }
        </ul>
      }
    </div>
  `,
  styles: [`
    .ms { position: relative; }
    input { height: 34px; width: 230px; border: 1px solid var(--border); border-radius: 8px; padding: 0 10px; font: inherit; background: var(--surface); }
    .results { position: absolute; right: 0; top: 38px; z-index: 40; width: 340px; list-style: none; margin: 0; padding: 4px;
               background: var(--surface); border: 1px solid var(--border); border-radius: 10px; box-shadow: var(--shadow); }
    li { padding: 6px 8px; border-radius: 6px; cursor: pointer; display: flex; flex-direction: column; }
    li:hover { background: var(--hover); }
    li span { font-size: 12px; color: var(--ink-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  `],
})
export class MeterSearch {
  private readonly api = inject(SmartMeterApi);
  private readonly router = inject(Router);
  private readonly host = inject(ElementRef<HTMLElement>);
  protected readonly term = signal('');
  protected readonly open = signal(false);
  protected readonly results = signal<MeterSearchItem[]>([]);

  constructor() {
    toObservable(this.term).pipe(
      debounceTime(200),
      distinctUntilChanged(),
      switchMap((t) => (t.trim().length >= 2 ? this.api.searchMeters(t.trim(), 8).pipe(catchError(() => of([]))) : of([]))),
      takeUntilDestroyed(),
    ).subscribe((r) => this.results.set(r));
  }

  protected pickFirst(): void {
    const first = this.results()[0];
    if (first) this.pick(first);
  }

  protected pick(r: MeterSearchItem): void {
    this.open.set(false);
    this.term.set(r.meterId);
    void this.router.navigate(['/dashboard'], { queryParams: { meter: r.meterId, building: null }, queryParamsHandling: 'merge' });
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(e: MouseEvent): void {
    if (!this.host.nativeElement.contains(e.target as Node)) this.open.set(false);
  }
}
