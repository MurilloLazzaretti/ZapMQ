import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  MessageModel, PublishResult, TapEvent,
  PanelUser,
  AreaItem, ItemCheck, TargetFile, TransportSettings, TransportTarget, PackageDetail, PackageItemDetail, PackageSummary, TransportSummary,
  CatalogDetail, CatalogPage, ObjectChange, ObjectTracking, DatabasePoint, DatabaseQuery, DatabaseState,
  Connection, DeadLetter, DeadSummary, MetricsPoint, Overview, PendingMessage, QueueDetail, QueueRow, QueueSettings, Session, V1Client,
  HealthSample, InstalledService, ParkMap, TrafficError, TrafficScreens, TrafficRoute, TrafficSummary, TrafficUpstreams, WebApplication, WebPublication, WorkerConfig, WorkerControlStatus, WorkerEvent,
} from './models';

/**
 * The panel API. Every address is relative, so it follows the <base href> the service writes
 * into the page: the panel works at the root of its port and under the path of a reverse proxy.
 */
export interface TrafficQuery {
  minutes: number;
  kind?: string;
  host?: string;
  app?: string;
}

/** Only what has a value goes in the address. */
const clean = (values: object): Record<string, string | number> =>
  Object.fromEntries(Object.entries(values).filter(([, value]) => value !== undefined && value !== null && value !== '')) as Record<string, string | number>;

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  session(): Observable<Session> {
    return this.http.get<Session>('api/session');
  }

  login(user: string, password: string): Observable<Session> {
    return this.http.post<Session>('api/login', { user, password });
  }

  changePassword(current: string, password: string): Observable<Session> {
    return this.http.post<Session>('api/password', { current, password });
  }

  users(): Observable<{ users: PanelUser[]; minimumPassword: number }> {
    return this.http.get<{ users: PanelUser[]; minimumPassword: number }>('api/users');
  }

  createUser(user: { login: string; name: string; password: string }): Observable<PanelUser> {
    return this.http.post<PanelUser>('api/users', user);
  }

  changeUser(login: string, change: { name?: string; enabled?: boolean }): Observable<PanelUser> {
    return this.http.put<PanelUser>(`api/users/${encodeURIComponent(login)}`, change);
  }

  resetPassword(login: string, password: string): Observable<void> {
    return this.http.post<void>(`api/users/${encodeURIComponent(login)}/password`, { password });
  }

  deleteUser(login: string): Observable<void> {
    return this.http.delete<void>(`api/users/${encodeURIComponent(login)}`);
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

  recentMessages(queue: string): Observable<TapEvent[]> {
    return this.http.get<TapEvent[]>(`api/queues/${encodeURIComponent(queue)}/recent`);
  }

  publishMessage(queue: string, message: { body: unknown; ttlMs: number; rpc: boolean }): Observable<PublishResult> {
    return this.http.post<PublishResult>(`api/queues/${encodeURIComponent(queue)}/publish`, message);
  }

  messageModels(queue: string): Observable<MessageModel[]> {
    return this.http.get<MessageModel[]>(`api/queues/${encodeURIComponent(queue)}/models`);
  }

  saveMessageModel(queue: string, name: string, model: { body: unknown; ttlMs: number; rpc: boolean }): Observable<MessageModel[]> {
    return this.http.put<MessageModel[]>(`api/queues/${encodeURIComponent(queue)}/models/${encodeURIComponent(name)}`, model);
  }

  removeMessageModel(queue: string, name: string): Observable<MessageModel[]> {
    return this.http.delete<MessageModel[]>(`api/queues/${encodeURIComponent(queue)}/models/${encodeURIComponent(name)}`);
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
  map(minutes: number): Observable<ParkMap> {
    return this.http.get<ParkMap>('api/map', { params: { minutes } });
  }

  // ── Worker Control ───────────────────────────────────────────────────────

  workerStatus(): Observable<WorkerControlStatus> {
    return this.http.get<WorkerControlStatus>('api/workers/status');
  }

  workerConfig(): Observable<{ Text: string; Config: WorkerConfig }> {
    return this.http.get<{ Text: string; Config: WorkerConfig }>('api/workers/config');
  }

  saveWorkerConfig(config: WorkerConfig | string): Observable<unknown> {
    return this.http.put('api/workers/config', { config });
  }

  setGroupEnabled(group: string, enabled: boolean): Observable<unknown> {
    return this.http.post(`api/workers/groups/${encodeURIComponent(group)}/enabled`, { enabled });
  }

  setGroupWorkers(group: string, totalWorkers: number): Observable<unknown> {
    return this.http.post(`api/workers/groups/${encodeURIComponent(group)}/workers`, { totalWorkers });
  }

  restartGroup(group: string): Observable<unknown> {
    return this.http.post(`api/workers/groups/${encodeURIComponent(group)}/restart`, null);
  }

  restartWorker(pid: number): Observable<unknown> {
    return this.http.post(`api/workers/processes/${pid}/restart`, null);
  }

  traffic(filter: TrafficQuery): Observable<TrafficSummary> {
    return this.http.get<TrafficSummary>('api/workers/traffic', { params: clean(filter) });
  }

  trafficRoutes(filter: TrafficQuery & { search?: string; sort?: string; limit?: number }): Observable<{ Routes: TrafficRoute[] }> {
    return this.http.get<{ Routes: TrafficRoute[] }>('api/workers/traffic/routes', { params: clean(filter) });
  }

  trafficPages(filter: { minutes: number; search?: string; limit?: number; names?: string[] }): Observable<TrafficScreens> {
    return this.http.get<TrafficScreens>('api/workers/traffic/pages', { params: clean({ ...filter, names: filter.names?.join(',') }) });
  }

  trafficUpstreams(minutes: number): Observable<TrafficUpstreams> {
    return this.http.get<TrafficUpstreams>('api/workers/traffic/upstreams', { params: { minutes } });
  }

  trafficErrors(filter: { host?: string; app?: string; limit?: number }): Observable<{ Errors: TrafficError[] }> {
    return this.http.get<{ Errors: TrafficError[] }>('api/workers/traffic/errors', { params: clean(filter) });
  }

  database(): Observable<DatabaseState> {
    return this.http.get<DatabaseState>('api/workers/database');
  }

  databaseHistory(minutes: number): Observable<{ Points: DatabasePoint[] }> {
    return this.http.get<{ Points: DatabasePoint[] }>('api/workers/database/history', { params: { minutes } });
  }

  databaseQueries(): Observable<{ Queries: DatabaseQuery[] }> {
    return this.http.get<{ Queries: DatabaseQuery[] }>('api/workers/database/queries');
  }

  databaseObjects(filter: { database?: string; kind?: string; schema?: string; search?: string; sort?: string; limit?: number; fresh?: boolean }): Observable<CatalogPage> {
    return this.http.get<CatalogPage>('api/workers/database/objects', { params: clean(filter) });
  }

  databaseObject(database: string, kind: string, schema: string, name: string): Observable<CatalogDetail> {
    return this.http.get<CatalogDetail>('api/workers/database/object', { params: { database, kind, schema, name } });
  }

  databaseChanges(filter: { database?: string; kind?: string; schema?: string; name?: string; search?: string; days?: number; limit?: number }): Observable<{ Total: number; Changes: ObjectChange[]; Tracking: ObjectTracking[] }> {
    return this.http.get<{ Total: number; Changes: ObjectChange[]; Tracking: ObjectTracking[] }>('api/workers/database/changes', { params: clean(filter) });
  }

  databaseChange(id: number): Observable<{ Change: ObjectChange }> {
    return this.http.get<{ Change: ObjectChange }>(`api/workers/database/changes/${id}`);
  }

  transportSummary(): Observable<TransportSummary> {
    return this.http.get<TransportSummary>('api/transport/summary');
  }

  area(): Observable<{ environment: string; items: AreaItem[] }> {
    return this.http.get<{ environment: string; items: AreaItem[] }>('api/transport/area');
  }

  addToArea(item: { database?: string; kind: string; schema: string; name: string; drop?: boolean }): Observable<AreaItem> {
    return this.http.post<AreaItem>('api/transport/area/objects', item);
  }

  addScript(script: { title: string; script: string }): Observable<AreaItem> {
    return this.http.post<AreaItem>('api/transport/area/scripts', script);
  }

  transportTargets(): Observable<{ Inbox: string; Targets: TransportTarget[]; Unmatched: string[] }> {
    return this.http.get<{ Inbox: string; Targets: TransportTarget[]; Unmatched: string[] }>('api/transport/targets');
  }

  /** The new version somebody left for a target in the inbox of the machine. */
  addIncoming(kind: string, name: string): Observable<AreaItem> {
    return this.http.post<AreaItem>('api/transport/area/incoming', { kind, name });
  }

  /** What a target is running right now, packed by the Worker Control. */
  addRunning(kind: string, name: string): Observable<AreaItem> {
    return this.http.post<AreaItem>('api/transport/area/running', { kind, name });
  }

  /** The zip of a published folder, brought from the machine of whoever is at the panel. */
  addFiles(kind: string, name: string, file: Blob): Observable<AreaItem> {
    return this.http.post<AreaItem>('api/transport/area/upload', file, { params: { kind, name }, headers: { 'Content-Type': 'application/octet-stream' } });
  }

  transportSettings(): Observable<TransportSettings> {
    return this.http.get<TransportSettings>('api/transport/settings');
  }

  setInboxes(inboxes: Record<string, string>): Observable<unknown> {
    return this.http.put('api/transport/settings/inboxes', inboxes);
  }

  /** The files of an application that belong to the environment. */
  targetFiles(kind: string, name: string): Observable<{ Target: TransportTarget; Files: TargetFile[] }> {
    return this.http.get<{ Target: TransportTarget; Files: TargetFile[] }>('api/transport/files', { params: { kind, name } });
  }

  targetFile(kind: string, name: string, instance: number, path: string): Observable<{ Content: string; Sha256: string; ModifiedAt: string }> {
    return this.http.get<{ Content: string; Sha256: string; ModifiedAt: string }>('api/transport/file', { params: { kind, name, instance, path } });
  }

  writeTargetFile(file: { kind: string; name: string; instance: number; path: string; content: string; sha256: string; restart: boolean }): Observable<{ Sha256: string; Backup: string; Restarted: boolean; RestartProblem: string | null }> {
    return this.http.put<{ Sha256: string; Backup: string; Restarted: boolean; RestartProblem: string | null }>('api/transport/file', file);
  }

  removeFromArea(id: string): Observable<void> {
    return this.http.delete<void>(`api/transport/area/${id}`);
  }

  /** When each object last went into a package made here, by "kind|schema|name" in lower case. */
  packaged(): Observable<Record<string, string>> {
    return this.http.get<Record<string, string>>('api/transport/packaged');
  }

  packages(): Observable<{ environment: string; packages: PackageSummary[] }> {
    return this.http.get<{ environment: string; packages: PackageSummary[] }>('api/transport/packages');
  }

  closePackage(request: { name: string; description: string; items?: string[] }): Observable<PackageDetail> {
    return this.http.post<PackageDetail>('api/transport/packages', request);
  }

  importPackage(file: Blob): Observable<PackageDetail> {
    return this.http.post<PackageDetail>('api/transport/packages/import', file, { headers: { 'Content-Type': 'application/octet-stream' } });
  }

  package(id: string): Observable<PackageDetail> {
    return this.http.get<PackageDetail>(`api/transport/packages/${id}`);
  }

  deletePackage(id: string): Observable<void> {
    return this.http.delete<void>(`api/transport/packages/${id}`);
  }

  packageCheck(id: string): Observable<{ checks: ItemCheck[] }> {
    return this.http.get<{ checks: ItemCheck[] }>(`api/transport/packages/${id}/check`);
  }

  packageItem(id: string, number: number): Observable<PackageItemDetail> {
    return this.http.get<PackageItemDetail>(`api/transport/packages/${id}/items/${number}`);
  }

  approvePackage(id: string, at: string | null): Observable<PackageDetail> {
    return this.http.post<PackageDetail>(`api/transport/packages/${id}/approve`, { at });
  }

  rejectPackage(id: string, reason: string): Observable<PackageDetail> {
    return this.http.post<PackageDetail>(`api/transport/packages/${id}/reject`, { reason });
  }

  frontends(): Observable<{ Frontends: WebApplication[]; Publications: WebPublication[] }> {
    return this.http.get<{ Frontends: WebApplication[]; Publications: WebPublication[] }>('api/workers/frontends');
  }

  installedServices(): Observable<{ SuggestFrom: string[]; Services: InstalledService[] }> {
    return this.http.get<{ SuggestFrom: string[]; Services: InstalledService[] }>('api/workers/services/installed');
  }

  serviceAction(name: string, action: 'start' | 'stop' | 'restart'): Observable<unknown> {
    return this.http.post(`api/workers/services/${encodeURIComponent(name)}/${action}`, null);
  }

  workerEvents(filter: { group?: string; kind?: string; limit?: number }): Observable<{ Events: WorkerEvent[] }> {
    const params: Record<string, string | number> = { limit: filter.limit ?? 200 };
    if (filter.group) {
      params['group'] = filter.group;
    }
    if (filter.kind) {
      params['kind'] = filter.kind;
    }
    return this.http.get<{ Events: WorkerEvent[] }>('api/workers/events', { params });
  }

  workerHealth(pid: number, limit = 1000): Observable<{ Samples: HealthSample[] }> {
    return this.http.get<{ Samples: HealthSample[] }>('api/workers/health', { params: { pid, limit } });
  }

  detachWorkerControl(): Observable<unknown> {
    return this.http.post('api/workers/detach', null);
  }
}
