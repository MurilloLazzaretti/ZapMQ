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
