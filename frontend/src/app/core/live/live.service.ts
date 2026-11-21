import { Injectable, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { IngestionJobDto } from '../api/models';
import { AuthService } from '../auth/auth.service';
import { AppConfigService } from '../config/app-config';

/** SignalR channel: live ingestion progress + "new analytics generation is live" broadcasts. */
@Injectable({ providedIn: 'root' })
export class LiveService {
  private readonly auth = inject(AuthService);
  private readonly config = inject(AppConfigService);
  private connection?: HubConnection;
  private watchingJobs = false;

  readonly state = signal<'connecting' | 'connected' | 'reconnecting' | 'disconnected'>('disconnected');
  readonly dataVersion = signal<number | null>(null);
  readonly jobUpdates = new Subject<IngestionJobDto>();

  start(): void {
    if (this.connection) return;
    this.connection = new HubConnectionBuilder()
      .withUrl(`${this.config.value.apiBaseUrl}/hubs/live`, { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
    this.connection.on('dataVersionChanged', (v: number) => this.dataVersion.set(v));
    this.connection.on('jobUpdated', (job: IngestionJobDto) => this.jobUpdates.next(job));
    this.connection.onreconnecting(() => this.state.set('reconnecting'));
    this.connection.onreconnected(() => {
      this.state.set('connected');
      if (this.watchingJobs) void this.connection?.invoke('WatchJobs');
    });
    this.connection.onclose(() => {
      this.state.set('disconnected');
      setTimeout(() => this.connect(), 15000);
    });
    void this.connect();
  }

  async watchJobs(): Promise<void> {
    this.watchingJobs = true;
    if (this.connection?.state === HubConnectionState.Connected) await this.connection.invoke('WatchJobs');
  }

  async unwatchJobs(): Promise<void> {
    this.watchingJobs = false;
    if (this.connection?.state === HubConnectionState.Connected) await this.connection.invoke('UnwatchJobs');
  }

  private async connect(): Promise<void> {
    if (!this.connection || this.connection.state !== HubConnectionState.Disconnected) return;
    this.state.set('connecting');
    try {
      await this.connection.start();
      this.state.set('connected');
      if (this.watchingJobs) await this.connection.invoke('WatchJobs');
    } catch {
      this.state.set('disconnected');
      setTimeout(() => this.connect(), 10000);
    }
  }
}
