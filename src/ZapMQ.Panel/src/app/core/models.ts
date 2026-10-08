// The shapes the panel API answers with (src/ZapMQ.Server/Panel/PanelEndpoints.cs).

export interface Session {
  user: string | null;
  defaultPassword?: boolean;
  version: string;
}

export interface Totals {
  published: number;
  delivered: number;
  confirmed: number;
  deadLettered: number;
}

export interface Overview {
  version: string;
  startedAt: string;
  totals: Totals;
  rates: { published: number; delivered: number; confirmed: number };
  queues: number;
  pending: number;
  processing: number;
  paused: number;
  withoutConsumer: number;
  deadLetters: { expired: number; notConsumed: number; unconfirmed: number };
  connections: number;
  applications: number;
  v1Clients: number;
}

export interface MetricsPoint {
  at: string;
  publishedPerSecond: number;
  deliveredPerSecond: number;
  confirmedPerSecond: number;
  deadPerSecond: number;
  pending: number;
  processing: number;
  queues: number;
  connections: number;
}

export interface QueueRow {
  name: string;
  exists: boolean;
  defined: boolean;
  paused: boolean;
  pending: number;
  processing: number;
  awaitingResponse: number;
  consumers: number;
  published: number;
  delivered: number;
  confirmed: number;
  deadLetters: number;
}

export interface QueueSettings {
  retentionSeconds: number | null;
  redeliverUnconfirmed: boolean;
  paused?: boolean;
  deadLetters: { maxMessagesPerQueue: number | null; maxAgeHours: number | null } | null;
}

export interface QueueSnapshot {
  name: string;
  pending: number;
  processing: number;
  awaitingResponse: number;
  answered: number;
  consumers: number;
  published: number;
  delivered: number;
  confirmed: number;
  responded: number;
  redelivered: number;
  expired: number;
  notConsumed: number;
  unconfirmed: number;
  dropped: number;
  paused: boolean;
  purged: number;
}

export interface Party {
  protocol: 'v1' | 'v2';
  connection: string | null;
  application: string;
  host: string | null;
  pid: string | null;
  lastSeen: string;
  count: number;
}

export interface Connection {
  id: string;
  application: string;
  pid: number;
  host: string;
  wrapper: string;
  connectedAt: string;
  busy: boolean;
  queues: string[];
}

export interface V1Client {
  address: string;
  lastSeen: string;
  consumes: string[];
  publishes: string[];
}

export interface QueueDetail {
  name: string;
  exists: boolean;
  snapshot: QueueSnapshot | null;
  settings: QueueSettings | null;
  defaults: { retentionSeconds: number; deadLetters: { maxMessagesPerQueue: number; maxAgeHours: number } };
  deadLetters: { expired: number; notConsumed: number; unconfirmed: number };
  publishers: Party[];
  askers: Party[];
  consumers: Connection[];
}

export interface PendingMessage {
  id: string;
  rpc: boolean;
  publishedAt: string;
  ttlMs: number | null;
  requeuedFrom: string | null;
  body: unknown;
}

export type DeadReason = 'expired' | 'not-consumed' | 'unconfirmed';

export interface DeadLetter {
  id: string;
  queue: string;
  reason: DeadReason;
  rpc: boolean;
  publishedAt: string;
  diedAt: string;
  consumer: string | null;
  body: unknown;
}

export interface DeadSummary {
  queue: string;
  expired: number;
  notConsumed: number;
  unconfirmed: number;
}

export interface LiveEvent {
  overview: Overview;
  queues: QueueRow[];
}

// ── Worker Control ─────────────────────────────────────────────────────────
// These come from the Worker Control itself, through the panel, with the field names of its
// administration contract.

export type WorkerState = 'Starting' | 'Up' | 'Stopping' | 'Killing';

export interface Worker {
  ProcessId: number;
  State: WorkerState;
  StartedAt: string;
  LastKeepAlive: string | null;
  KeepAliveMs: number | null;
  Adopted: boolean;
  BeingReplaced: boolean;
  CpuPercent: number | null;
  MemoryBytes: number | null;
}

export interface WorkerGroup {
  Name: string;
  Enabled: boolean;
  ApplicationFullPath: string;
  TotalWorkers: number;
  DesiredWorkers: number;
  BoostWorkers: number;
  ScaleWorkers: number;
  Recycling: boolean;
  Unstable: boolean;
  MonitoringRate: number;
  TimeoutKeepAlive: number;
  LastSyncConfig: string;
  Workers: Worker[];
}

export interface WorkerControlStatus {
  Version: string;
  Contract: number;
  Service: { StartedAt: string; Machine: string; ProcessId: number; ZapMQ: { Host: string; Port: number; Healthy: boolean } };
  Groups: WorkerGroup[];
}

export interface WorkerEvent {
  Id: number;
  At: string;
  Kind: string;
  Group: string | null;
  ProcessId: number | null;
  Detail: string;
}

export interface HealthSample {
  At: string;
  Group: string;
  ProcessId: number;
  State: string;
  UptimeSeconds: number;
  CpuPercent: number | null;
  MemoryBytes: number | null;
  KeepAliveMs: number | null;
}

/** ConfigWorkers.json as it is; only the keys the forms edit are named here. */
export interface WorkerConfig {
  ZapMQHost: string;
  ZapMQPort: number;
  RateLoadConfig?: number;
  StartBatchSize?: number;
  StartBatchIntervalMs?: number;
  StartupGraceMs?: number;
  SafeStopTimeoutMs?: number;
  WorkerGroups: GroupConfig[];
  [other: string]: unknown;
}

export interface BoostWindow {
  Workers: number;
  StartTime: string;
  EndTime: string;
  Days?: string[];
}

export interface GroupConfig {
  Name: string;
  Enabled: boolean;
  ApplicationFullPath: string;
  Arguments?: string;
  WorkingDirectory?: string;
  TotalWorkers: number;
  MonitoringRate: number;
  TimeoutKeepAlive: number;
  StartupGraceMs?: number;
  SafeStopTimeoutMs?: number;
  Boost?: { Enabled: boolean; BoostWorkers: number; StartTime: string; EndTime: string };
  BoostWindows?: BoostWindow[];
  QueueScaling?: { Queue: string; PendingPerWorker: number; MaxWorkers: number; CooldownMs: number } | null;
  Recycle?: { Time: string; Days?: string[] } | null;
  [other: string]: unknown;
}

// ── Map ────────────────────────────────────────────────────────────────────

export interface MapInstance {
  host: string;
  pid: number;
  connections: number;
  busy: boolean;
}

export interface MapApplication {
  id: string;
  protocol: 'v1' | 'v2';
  name: string;
  lastSeen: string | null;
  /** Queues of its own (keep-alive, safe stop, trace) that are not drawn. */
  internalQueues: number;
  instances: MapInstance[];
}

export interface MapQueue {
  name: string;
  /** False when the queue is only remembered by what was published in it. */
  exists: boolean;
  pending: number;
  processing: number;
  consumers: number;
  paused: boolean;
  published: number;
  delivered: number;
  deadLetters: number;
}

export interface MapLink {
  application: string;
  queue: string;
  kind: 'publish' | 'consume';
  count: number;
  lastSeen: string | null;
  /** Consuming over a connection that is open now. */
  bound: boolean;
}

export interface ParkMap {
  windowMinutes: number;
  applications: MapApplication[];
  queues: MapQueue[];
  links: MapLink[];
}
