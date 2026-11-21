import { Injectable, computed, effect, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { SmartMeterApi } from '../api/smart-meter-api';
import { LiveService } from '../live/live.service';

/** Analytics metadata (window, dimensions, data version). Reloads when SignalR announces a new generation. */
@Injectable({ providedIn: 'root' })
export class MetaStore {
  private readonly api = inject(SmartMeterApi);
  private readonly live = inject(LiveService);

  readonly meta = rxResource({ stream: () => this.api.meta() });
  readonly value = computed(() => (this.meta.hasValue() ? this.meta.value() : undefined));
  readonly window = computed(() => this.value()?.window);
  readonly dataVersion = computed(() => this.value()?.dataVersion ?? 0);

  constructor() {
    effect(() => {
      const announced = this.live.dataVersion();
      if (announced !== null && announced !== this.dataVersion()) this.meta.reload();
    });
  }

  zoneName(id: number): string { return this.value()?.zones.find((z) => z.id === id)?.name ?? `Zone ${id}`; }
}
