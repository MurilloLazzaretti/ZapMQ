import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Connection, DeadLetter, DeadSummary, MetricsPoint, Overview, PendingMessage, QueueDetail, QueueRow, QueueSettings, Session, V1Client,
} from './models';

/**
 * The panel API. Every address is relative, so it follows the <base href> the service writes
 * into the page: the panel works at the root of its port and under the path of a reverse proxy.
 */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  session(): Observable<Session> {
    return this.http.get<Session>('api/session');
  }

  login(user: string, password: string): Observable<Session> {
    return this.http.post<Session>('api/login', { user, password });
  }

  logout(): Observable<void> {
    return this.http.post<void>('api/logout', null);
  }

  overview(): Observable<Overview> {
    return this.http.get<Overview>('api/overview');
  }

  series(range: 'hour' | 'day'): Observable<{ range: string; points: MetricsPoint[] }> {
    return this.http.get<{ range: string; points: MetricsPoint[] }>('api/series', { params: { range } });
  }

  queues(): Observable<QueueRow[]> {
    return this.http.get<QueueRow[]>('api/queues');
  }

  queue(name: string): Observable<QueueDetail> {
    return this.http.get<QueueDetail>(`api/queues/${encodeURIComponent(name)}`);
  }

  messages(queue: string, limit = 50): Observable<PendingMessage[]> {
    return this.http.get<PendingMessage[]>(`api/queues/${encodeURIComponent(queue)}/messages`, { params: { limit } });
  }

  saveSettings(queue: string, settings: QueueSettings): Observable<QueueSettings> {
    return this.http.put<QueueSettings>(`api/queues/${encodeURIComponent(queue)}/settings`, settings);
  }

  clearSettings(queue: string): Observable<void> {
    return this.http.delete<void>(`api/queues/${encodeURIComponent(queue)}/settings`);
  }

  pause(queue: string, paused: boolean): Observable<void> {
    return this.http.post<void>(`api/queues/${encodeURIComponent(queue)}/${paused ? 'pause' : 'resume'}`, null);
  }

  purge(queue: string): Observable<{ purged: number }> {
    return this.http.post<{ purged: number }>(`api/queues/${encodeURIComponent(queue)}/purge`, null);
  }

  deadSummary(): Observable<DeadSummary[]> {
    return this.http.get<DeadSummary[]>('api/dead-letters');
  }

  deadLetters(queue: string): Observable<DeadLetter[]> {
    return this.http.get<DeadLetter[]>(`api/dead-letters/${encodeURIComponent(queue)}`);
  }

  requeue(queue: string, id: string): Observable<{ messageId: string }> {
    return this.http.post<{ messageId: string }>(`api/dead-letters/${encodeURIComponent(queue)}/${encodeURIComponent(id)}/requeue`, null);
  }

  discard(queue: string, id: string): Observable<void> {
    return this.http.delete<void>(`api/dead-letters/${encodeURIComponent(queue)}/${encodeURIComponent(id)}`);
  }

  discardAll(queue: string): Observable<{ discarded: number }> {
    return this.http.delete<{ discarded: number }>(`api/dead-letters/${encodeURIComponent(queue)}`);
  }

  connections(): Observable<{ v2: Connection[]; v1: V1Client[] }> {
    return this.http.get<{ v2: Connection[]; v1: V1Client[] }>('api/connections');
  }
}
