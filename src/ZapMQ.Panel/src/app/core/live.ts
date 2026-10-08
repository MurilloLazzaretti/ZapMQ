import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { LiveEvent, Overview, QueueRow } from './models';

/**
 * The state of the broker as the service keeps sending it. One connection for the whole
 * panel; the screens read the signals.
 */
@Injectable({ providedIn: 'root' })
export class Live {
  private readonly document = inject(DOCUMENT);
  private source: EventSource | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;

  readonly overview = signal<Overview | null>(null);
  readonly queues = signal<QueueRow[]>([]);
  /** True while events are arriving. */
  readonly connected = signal(false);
  readonly lastEventAt = signal<number | null>(null);

  start(): void {
    if (this.source) {
      return;
    }
    // Relative to <base href>, like everything else.
    const url = new URL('api/live', this.document.baseURI).toString();
    const source = new EventSource(url, { withCredentials: true });
    this.source = source;

    source.onmessage = (message) => {
      const event = JSON.parse(message.data) as LiveEvent;
      this.overview.set(event.overview);
      this.queues.set(event.queues);
      this.connected.set(true);
      this.lastEventAt.set(Date.now());
    };

    source.onerror = () => {
      // The browser retries by itself while the connection is only interrupted. Once it gives
      // up (the service is down, or the session ended), try again from scratch after a while.
      this.connected.set(false);
      if (source.readyState === EventSource.CLOSED) {
        this.stop();
        this.retry = setTimeout(() => this.start(), 5000);
      }
    };
  }

  stop(): void {
    if (this.retry) {
      clearTimeout(this.retry);
      this.retry = null;
    }
    this.source?.close();
    this.source = null;
    this.connected.set(false);
  }
}
