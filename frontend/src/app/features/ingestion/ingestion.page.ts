import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom, interval } from 'rxjs';
import { IngestionJobDto, IngestionKind } from '../../core/api/models';
import { SmartMeterApi, saveBlob } from '../../core/api/smart-meter-api';
import { LiveService } from '../../core/live/live.service';
import { MetaStore } from '../../core/state/meta.store';
import { NotificationService } from '../../core/ui/notification.service';
import { fmt } from '../../core/ui/format';

interface UploadState { kind: IngestionKind; fileName: string; size: number; sent: number; }

const ACTIVE = new Set(['uploading', 'queued', 'parsing', 'merging']);

/** Bulk Excel / CSV ingestion with chunked, parallel uploads and live job progress (SignalR + polling fallback). */
@Component({
  selector: 'sm-ingestion-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ingestion.page.html',
  styleUrl: './ingestion.page.scss',
})
export class IngestionPage {
  private readonly api = inject(SmartMeterApi);
  private readonly live = inject(LiveService);
  private readonly notifications = inject(NotificationService);
  protected readonly meta = inject(MetaStore);
  protected readonly fmt = fmt;

  protected readonly jobs = signal<IngestionJobDto[]>([]);
  protected readonly uploads = signal<UploadState[]>([]);
  protected readonly dragOver = signal<IngestionKind | null>(null);
  protected readonly refreshing = signal(false);
  protected readonly anyActive = computed(() => this.jobs().some((j) => ACTIVE.has(j.status)));

  protected readonly kinds: { kind: IngestionKind; title: string; text: string; columns: string }[] = [
    { kind: 'cis', title: 'CIS customer master', text: 'Meter attributes and locations. Upsert by MeterId (last row wins).',
      columns: 'MeterId, BuildingId, Latitude, Longitude, Address, NSP, Manufacturer, Zone, InstallationDate, DtId' },
    { kind: 'mdm', title: 'MDM communication logs', text: 'Daily communication status per meter (rolling 30/31-day window).',
      columns: 'MeterId, LogDate, IsCommunicating (1/0, true/false, success/fail)' },
  ];

  constructor() {
    const destroyRef = inject(DestroyRef);
    void this.live.watchJobs();
    destroyRef.onDestroy(() => void this.live.unwatchJobs());
    this.live.jobUpdates.pipe(takeUntilDestroyed()).subscribe((job) => this.upsert(job));
    void this.load();
    // polling fallback while anything is running (covers a dropped WebSocket)
    interval(4000).pipe(takeUntilDestroyed()).subscribe(() => { if (this.anyActive()) void this.load(); });
  }

  protected async load(): Promise<void> {
    this.jobs.set(await firstValueFrom(this.api.jobs(30)));
  }

  protected onDrop(e: DragEvent, kind: IngestionKind): void {
    e.preventDefault();
    this.dragOver.set(null);
    const file = e.dataTransfer?.files?.[0];
    if (file) void this.upload(kind, file);
  }

  protected onPick(e: Event, kind: IngestionKind): void {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) void this.upload(kind, file);
    input.value = '';
  }

  /** Chunked upload: create session -> PUT chunks (3 in parallel, any order) -> complete -> job is queued. */
  protected async upload(kind: IngestionKind, file: File): Promise<void> {
    const state: UploadState = { kind, fileName: file.name, size: file.size, sent: 0 };
    this.uploads.update((u) => [...u, state]);
    try {
      const session = await firstValueFrom(this.api.createUpload(kind, file.name, file.size));
      const indexes = Array.from({ length: session.chunkCount }, (_, i) => i);
      const worker = async () => {
        for (let i = indexes.shift(); i !== undefined; i = indexes.shift()) {
          const chunk = file.slice(i * session.chunkSize, Math.min(file.size, (i + 1) * session.chunkSize));
          await firstValueFrom(this.api.uploadChunk(session.jobId, i, chunk));
          state.sent += chunk.size;
          this.uploads.update((u) => [...u]);
        }
      };
      await Promise.all([worker(), worker(), worker()]);
      const job = await firstValueFrom(this.api.completeUpload(session.jobId));
      this.upsert(job);
      this.notifications.success('Upload complete', `${file.name} is queued for ingestion.`);
    } catch {
      this.notifications.error('Upload failed', file.name);
    } finally {
      this.uploads.update((u) => u.filter((x) => x !== state));
    }
  }

  protected downloadErrors(job: IngestionJobDto): void {
    this.api.jobErrors(job.jobId).subscribe((b) => saveBlob(b, `errors_${job.fileName}.csv`));
  }

  protected downloadTemplate(kind: IngestionKind): void {
    this.api.template(kind).subscribe((b) => saveBlob(b, `${kind}_template.csv`));
  }

  protected refresh(): void {
    this.refreshing.set(true);
    this.api.refreshAnalytics().subscribe({
      next: () => { this.notifications.info('Analytics refresh queued', 'Dashboards update automatically when the new generation is live.'); this.refreshing.set(false); },
      error: () => this.refreshing.set(false),
    });
  }

  protected duration(j: IngestionJobDto): string {
    if (!j.startedAt) return '—';
    const ms = (j.finishedAt ? Date.parse(j.finishedAt) : Date.now()) - Date.parse(j.startedAt);
    return ms < 60_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.floor(ms / 60_000)} m ${Math.round((ms % 60_000) / 1000)} s`;
  }

  protected size(bytes: number): string {
    return bytes > 1 << 30 ? `${(bytes / (1 << 30)).toFixed(2)} GB` : bytes > 1 << 20 ? `${(bytes / (1 << 20)).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`;
  }

  protected time(iso: string): string { return new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }); }

  private upsert(job: IngestionJobDto): void {
    this.jobs.update((list) => {
      const i = list.findIndex((j) => j.jobId === job.jobId);
      return i >= 0 ? list.map((j, k) => (k === i ? job : j)) : [job, ...list];
    });
  }
}
