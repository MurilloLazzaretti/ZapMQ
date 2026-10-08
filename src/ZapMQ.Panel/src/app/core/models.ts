// The shapes the panel API answers with (src/ZapMQ.Server/Panel/PanelEndpoints.cs).

export interface Session {
  user: string | null;
  name?: string;
  /** The one who creates and removes the other users. */
  master?: boolean;
  /** The password is still the one the user was given and has not chosen. */
  defaultPassword?: boolean;
  version: string;
}

/** Someone who may get into the panel, as the master sees them. */
export interface PanelUser {
  login: string;
  name: string;
  master: boolean;
  enabled: boolean;
  initialPassword: boolean;
  createdAt: string;
  createdBy: string | null;
  lastLoginAt: string | null;
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
  /** How many of the last messages are kept to be looked at; null keeps none. */
  keepRecent?: number | null;
  paused?: boolean;
  deadLetters: { maxMessagesPerQueue: number | null; maxAgeHours: number | null } | null;
}

/** One step in the life of a message of a queue that is being watched. */
export interface TapEvent {
  kind: 'published' | 'delivered' | 'confirmed' | 'responded' | 'dead';
  queue: string;
  id: string;
  at: string;
  /** JSON text. Comes with the publication. */
  body?: string;
  /** The body or the answer was too long and was cut. */
  truncated?: boolean;
  rpc?: boolean;
  /** Who published, or who the message was delivered to. */
  who?: string;
  /** JSON text. Comes with the response. */
  response?: string;
  reason?: string;
}

/** A message body saved under a name, to publish again. */
export interface MessageModel {
  name: string;
  body: unknown;
  ttlMs: number;
  rpc: boolean;
}

export interface PublishResult {
  id: string;
  rpc: boolean;
  answered?: boolean;
  response?: unknown;
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
  /** The name of the process, when the client is on the machine of the broker. */
  application: string | null;
  pid: number | null;
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
  /** Windows services that are watched. Absent when the Worker Control is older than that. */
  Services?: MonitoredService[];
}

// ── Traffic ────────────────────────────────────────────────────────────────

/** What is counted of the requests of a route, an application or a stretch of time. */
export interface TrafficTally {
  Count: number;
  S2: number;
  S3: number;
  S4: number;
  S5: number;
  Bytes: number;
  /** Milliseconds. Null with no requests. */
  Average: number | null;
  P50: number | null;
  P95: number | null;
  P99: number | null;
  /** Different addresses. Null where it is not counted. */
  Users: number | null;
}

export interface TrafficSummary {
  /** False when the Worker Control has no access log to read. */
  Configured: boolean;
  Source: { File: string; Found: boolean; Size: number; Pending: number; LastLineAt: string | null; Lines: number; Refused: number; Problem: string | null };
  From: string;
  To: string;
  StepSeconds: number;
  Totals: TrafficTally;
  Series: (TrafficTally & { At: string })[];
  Apps: (TrafficTally & { App: string; Kind: 'api' | 'static' })[];
  Upstreams: (TrafficTally & { Upstream: string; App: string })[];
  Hosts: string[];
}

export interface TrafficRoute extends TrafficTally {
  Host: string;
  Method: string;
  Route: string;
  Kind: 'api' | 'static';
  App: string;
  /** The same stretch of the day before. */
  PreviousCount: number | null;
  PreviousP95: number | null;
}

/** An address the proxy forwards to, with what went there and which processes answer on it. */
export interface TrafficUpstream extends TrafficTally {
  Upstream: string;
  App: string;
  /** The site of the web server of the machine that listens there on behalf of the application, if one does. */
  Site: string | null;
  Processes: { ProcessId: number; Name: string }[];
}

export interface TrafficUpstreams {
  Minutes: number;
  Upstreams: TrafficUpstream[];
}

/** A screen of the web application, by what was asked from it. */
export interface TrafficScreen {
  Host: string;
  Page: string;
  Count: number;
  Users: number;
}

export interface TrafficScreens {
  Pages: TrafficScreen[];
  /** For each name asked about: the screens that have it as a part of their path. */
  Named: { Name: string; Count: number; Users: number }[];
}

export interface TrafficError {
  At: string;
  Host: string;
  Method: string;
  Path: string;
  Route: string;
  App: string;
  Status: number;
  Upstream: string;
  Milliseconds: number;
}

// ── Web application ────────────────────────────────────────────────────────

export interface ModuleVersion {
  Version: string | null;
  Date: string | null;
  Descriptions: string[];
}

export interface WebModule {
  Name: string;
  Entry: string;
  /** Incomplete: listed by the manifest and not on disk. */
  State: 'Up' | 'Down' | 'Incomplete';
  /** Null when nothing was asked, for want of an address. */
  Online: boolean | null;
  Status: number | null;
  Problem: string | null;
  /** How the module calls itself. */
  Title: string | null;
  Build: number | null;
  PublishedAt: string | null;
  Files: number;
  Bytes: number;
  /** The newest first, as the module tells them. */
  Versions: ModuleVersion[];
}

export interface WebApplication {
  Name: string;
  Root: string;
  BaseUrl: string | null;
  Problem: string | null;
  CheckedAt: string;
  /** When the application around the modules was last published. */
  ShellPublishedAt: string | null;
  /** Folders beside the modules that the manifest does not list. */
  Orphans: string[];
  Modules: WebModule[];
}

export interface WebPublication {
  At: string;
  App: string;
  /** A module, "(shell)" for the application around them, or "*" for the first look. */
  Module: string;
  Kind: 'first-seen' | 'published' | 'new' | 'removed';
  FromVersion: string | null;
  ToVersion: string | null;
  FromBuild: number | null;
  ToBuild: number | null;
  Count: number;
}

export type ServiceState = 'Missing' | 'Stopped' | 'Starting' | 'Stopping' | 'Running' | 'Paused';

export interface MonitoredService {
  Name: string;
  DisplayName: string | null;
  ExecutablePath: string | null;
  State: ServiceState;
  StartType: string | null;
  ProcessId: number | null;
  StartedAt: string | null;
  ExitCode: number | null;
  CpuPercent: number | null;
  MemoryBytes: number | null;
  Threads: number | null;
  Handles: number | null;
  AutoRestart: boolean;
  /** When it is going to be started again by itself. */
  RestartingAt: string | null;
  /** Being restarted because somebody asked. */
  Restarting: boolean;
  Unstable: boolean;
  /** It is the service of the Worker Control itself. */
  IsSupervisor: boolean;
  /** The process of the service and of everything it started. */
  ProcessIds?: number[];
  /** Names of the programs the service started, which are measured with it. */
  Children?: string[] | null;
  HasLog: boolean;
  Check: { Target: string; Ok: boolean | null; Detail: string | null; At: string | null } | null;
}

export interface InstalledService {
  Name: string;
  DisplayName: string;
  ExecutablePath: string | null;
  State: ServiceState;
  StartType: string;
  /** Its executable is in one of the folders the suggestions come from. */
  Suggested: boolean;
  /** It is part of Windows. */
  System: boolean;
  Watched: boolean;
}

/** One entry of "Services.Items" in ConfigWorkers.json. */
export interface ServiceConfig {
  Name: string;
  AutoRestart?: boolean;
  StopTimeoutMs?: number;
  LogFiles?: string;
  Check?: { Tcp?: string; Url?: string };
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
  Services?: { SuggestFrom?: string[]; Items?: ServiceConfig[] };
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

// ── Trace ──────────────────────────────────────────────────────────────────

export interface TraceLine {
  n: number;
  at: string;
  text: string;
  /** Above zero: lines the process threw away before this point; the text is empty. */
  dropped: number;
}

export interface TraceState {
  state: 'starting' | 'on' | 'unsupported' | 'unreachable' | 'ended';
  message: string | null;
}

/** The database instance of the environment, as the Worker Control sees it. */
export interface DatabaseState {
  Configured: boolean;
  Name: string | null;
  Server: string | null;
  Databases: string[];
  /** Null until the first look comes back. */
  Online: boolean | null;
  Error: string | null;
  Since: string | null;
  CheckedAt: string | null;
  ResponseMs: number | null;
  BatchesPerSecond: number | null;
  Instance: { Server: string; Product: string; Version: string; Level: string; Edition: string } | null;
  Machine: { UptimeSeconds: number; Processors: number; MemoryKb: number } | null;
  Resources: { CpuPercent: number | null; OtherCpuPercent: number | null; MemoryKb: number | null; TargetMemoryKb: number | null; PageLifeSeconds: number | null; Connections: number | null } | null;
  DatabaseList: DatabaseInfo[] | null;
  Sessions: DatabaseSessions[] | null;
  Activity: DatabaseActivity[] | null;
  Volumes: { Mount: string; Label: string; TotalBytes: number; FreeBytes: number }[] | null;
  Backups: { Database: string; FullMinutes: number | null; DifferentialMinutes: number | null; LogMinutes: number | null }[] | null;
  Jobs: { Name: string; Enabled: boolean; Outcome: number | null; LastRunMinutes: number | null; DurationSeconds: number | null; Message: string }[] | null;
  SlowAt: string | null;
  Alerts: DatabaseAlert[];
  /** The parts the user of the connection could not read, each with what the instance said. */
  Problems: Record<string, string>;
}

export interface DatabaseInfo {
  Name: string;
  State: string;
  Recovery: string;
  Access: string;
  ReadOnly: boolean;
  Compatibility: number;
  System: boolean;
  DataKb: number;
  LogKb: number;
  LogUsedPercent: number | null;
}

export interface DatabaseSessions {
  Program: string;
  Host: string;
  Login: string;
  Database: string;
  Sessions: number;
  Running: number;
  InTransaction: number;
}

export interface DatabaseActivity {
  SessionId: number;
  Status: string;
  Command: string;
  ElapsedMs: number;
  CpuMs: number;
  Reads: number;
  WaitType: string;
  WaitMs: number;
  BlockedBy: number;
  Database: string;
  Program: string;
  Host: string;
  Login: string;
  OpenTransactions: number;
  Running: boolean;
  /** The statement, without the values written in it. */
  Text: string;
}

export interface DatabaseAlert {
  Kind: 'State' | 'Blocking' | 'Disk' | 'Backup' | 'Job' | string;
  Subject: string;
  Severity: 'danger' | 'warn';
  Value: number | null;
  Since: string;
}

export interface DatabasePoint {
  At: string;
  Online: boolean;
  ResponseMs: number | null;
  CpuPercent: number | null;
  OtherCpuPercent: number | null;
  MemoryKb: number | null;
  Sessions: number | null;
  Running: number | null;
  Blocked: number | null;
  BatchesPerSecond: number | null;
}

export interface DatabaseQuery {
  Database: string;
  Object: string;
  Executions: number;
  CpuMs: number;
  ElapsedMs: number;
  Reads: number;
  MaxElapsedMs: number;
  LastMinutes: number;
  TopCpu: boolean;
  TopTime: boolean;
  TopReads: boolean;
  Text: string;
}

export type CatalogKind = 'Table' | 'View' | 'Procedure' | 'Function' | 'Type';

/** Something defined in a database. */
export interface CatalogObject {
  Id: number;
  Kind: CatalogKind;
  Schema: string;
  Name: string;
  /** As the instance tells them apart: U, V, P, FN, IF, TF, T, TT. */
  Variety: string;
  CreatedAt: string | null;
  ModifiedAt: string | null;
  Rows: number | null;
  SizeKb: number | null;
}

export interface CatalogPage {
  Database: string;
  ReadAt: string;
  Total: number;
  Objects: CatalogObject[];
  Kinds: { Kind: CatalogKind; Count: number }[];
  Schemas: { Schema: string; Count: number }[];
}

export interface CatalogColumn {
  Name: string;
  Type: string;
  Nullable: boolean;
  Identity: boolean;
  Seed: string | null;
  Increment: string | null;
  DefaultName: string | null;
  Default: string | null;
  Computed: string | null;
  Persisted: boolean;
  Collation: string | null;
}

export interface CatalogReference {
  Schema: string | null;
  Name: string;
  /** Null for what is not in this database. */
  Kind: CatalogKind | null;
  Database: string | null;
}

export interface CatalogDetail {
  Database: string;
  Object: CatalogObject;
  Columns: CatalogColumn[];
  Parameters: { Name: string; Type: string; Output: boolean; ReadOnly: boolean; Default: string | null }[];
  Returns: string | null;
  BaseType: string | null;
  Indexes: { Name: string; Variety: string; Unique: boolean; PrimaryKey: boolean; UniqueConstraint: boolean; Disabled: boolean; Filter: string | null; Columns: { Name: string; Descending: boolean; Included: boolean }[] }[];
  ForeignKeys: { Name: string; Columns: string[]; ReferencedSchema: string; ReferencedTable: string; ReferencedColumns: string[]; OnDelete: string; OnUpdate: string; Disabled: boolean }[];
  Checks: { Name: string; Definition: string; Disabled: boolean }[];
  Triggers: { Name: string; Disabled: boolean; InsteadOf: boolean; Definition: string | null }[];
  Uses: CatalogReference[];
  UsedBy: CatalogReference[];
  Script: string | null;
  ScriptSource: 'instance' | 'generated' | 'encrypted';
  Fingerprint: string | null;
  Problems: Record<string, string>;
}

/** Something that happened to an object of a database. */
export interface ObjectChange {
  Id: number;
  /** When it was noticed. */
  At: string;
  Database: string;
  Kind: CatalogKind;
  Schema: string;
  Name: string;
  Action: 'Created' | 'Altered' | 'Renamed' | 'Dropped';
  OldName: string | null;
  /** When the instance says it happened. */
  ModifiedAt: string | null;
  OldFingerprint: string | null;
  NewFingerprint: string | null;
  Login: string | null;
  Host: string | null;
  Application: string | null;
  OldScript: string | null;
  NewScript: string | null;
}

/** How far the watching of the objects of one database has got. */
export interface ObjectTracking {
  Database: string;
  Known: number;
  Total: number;
  /** Null while the database is being read for the first time. */
  BaselineAt: string | null;
  ScannedAt: string | null;
  Problem: string | null;
}

/** Something waiting in the area of the environment to go into a package. */
export interface AreaItem {
  id: string;
  kind: string;
  action: 'Define' | 'Drop' | 'Script';
  objectKind: CatalogKind | null;
  variety: string | null;
  schema: string | null;
  name: string | null;
  title: string | null;
  script: string | null;
  database: string | null;
  addedBy: string;
  addedAt: string;
}

export type PackageStatus = 'Closed' | 'Pending' | 'Rejected' | 'Approved' | 'Applying' | 'Applied' | 'Partial' | 'Failed';

export interface PackageSummary {
  id: string;
  name: string;
  description: string;
  origin: string;
  createdAt: string;
  createdBy: string;
  items: number;
  status: PackageStatus;
  /** It came from another environment, to be applied here. */
  received: boolean;
  applyAt: string | null;
  approvedBy: string | null;
  size: number;
  changedAt: string;
}

export interface PackageItem {
  number: number;
  kind: string;
  action: 'Define' | 'Drop' | 'Script';
  objectKind: CatalogKind | null;
  variety: string | null;
  schema: string | null;
  name: string | null;
  title: string | null;
  database: string | null;
  fingerprint: string | null;
  base: string | null;
  size: number;
}

export interface PackageResult {
  number: number;
  status: 'applied' | 'failed' | 'unknown';
  did: string | null;
  problem: string | null;
  batch: number | null;
  line: number | null;
  messages: string[];
  at: string;
  hasPrevious: boolean;
}

export interface PackageDetail {
  environment: string;
  package: PackageSummary;
  sha256: string;
  items: PackageItem[];
  history: { at: string; by: string; what: string; detail: string | null }[];
  results: PackageResult[];
}

/** How one item of a package stands against what this environment has. */
export interface ItemCheck {
  number: number;
  state: 'new' | 'same' | 'clean' | 'changes' | 'conflict' | 'blocked' | 'drops' | 'absent' | 'script' | 'unknown';
  currentFingerprint: string | null;
  missing: string[];
  problem: string | null;
}

export interface PackageItemDetail {
  item: PackageItem;
  script: string | null;
  current: string | null;
  currentFingerprint: string | null;
  previous: string | null;
  previousFingerprint: string | null;
  problem: string | null;
}

export interface TransportSummary {
  environment: string;
  area: number;
  pending: number;
  scheduled: number;
  troubled: number;
}
