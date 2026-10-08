import { MapApplication, MapLink, MapQueue, MonitoredService, ParkMap, TrafficUpstreams, WorkerControlStatus, WorkerGroup } from './models';

export type Tone = 'ok' | 'warn' | 'danger' | 'off' | 'neutral';
export type NodeKind = 'application' | 'queue' | 'supervisor' | 'group' | 'proxy' | 'bundle';

/** How much of what there is gets drawn. */
export interface ParkView {
  /** Leaves out the queues nothing was published in during the period, unless something is wrong with them. */
  activeOnly: boolean;
  /** Draws as one the queues that have exactly the same publishers and consumers. */
  bundle: boolean;
  /** Bundles that were opened, and stay drawn queue by queue. */
  expanded: ReadonlySet<string>;
}

/** The fewest queues that are worth drawing as one. */
const BUNDLE_FROM = 3;

/** The reverse proxy (when its traffic is known), publishers, queues, consumers and, when there is one, the Worker Control. */
export type Column = -1 | 0 | 1 | 2 | 3;

/** What the reverse proxy sent to an application in the period. */
export interface HttpTraffic {
  /** How the proxy calls what it forwards there: `api/orders`. */
  apps: string[];
  upstreams: string[];
  count: number;
  perMinute: number;
  serverErrors: number;
  p95: number | null;
}

export interface ParkNode {
  id: string;
  kind: NodeKind;
  column: Column;
  label: string;
  detail: string;
  tone: Tone;
  /** What is wrong, in words; empty when nothing is. */
  reasons: string[];
  /** The figure on the right of the node: instances of an application, pending of a queue. */
  figure: number | null;
  application?: MapApplication;
  queue?: MapQueue;
  /** The queues drawn as one, when this node stands for several. */
  bundle?: MapQueue[];
  /** Groups of the Worker Control this node stands for. */
  groups: WorkerGroup[];
  /** Windows services this application is, when it is one. */
  services?: MonitoredService[];
  /** What it receives through the reverse proxy. On the proxy itself, everything it forwarded. */
  http?: HttpTraffic;
  x: number;
  y: number;
  /** As wide as its column. */
  width?: number;
}

export interface ParkEdge {
  id: string;
  from: string;
  to: string;
  kind: 'publish' | 'consume' | 'supervise' | 'http';
  /** Messages (or, from the proxy, requests) per minute, lately. */
  rate: number;
  /** Something went through it in the last minute. */
  active: boolean;
  /** Nothing for a while, and nobody connected to it. */
  stale: boolean;
  /** Drawn from right to left: a consumer that also publishes. */
  back: boolean;
  link?: MapLink;
  path: string;
}

export interface Park {
  nodes: ParkNode[];
  edges: ParkEdge[];
  width: number;
  height: number;
  nodeWidth: number;
  /** Left edge and width of each column in use, by column. */
  columns: { column: Column; x: number; width: number }[];
}

export const NODE_HEIGHT = 52;
const ROW = NODE_HEIGHT + 14;
const HEADER = 34;
const PADDING = 16;
const MIN_NODE = 184;
const MAX_NODE = 260;
const MIN_GAP = 72;
const SLIM_NODE = 178;

export const linkKey = (link: MapLink) => `${link.kind}|${link.application}|${link.queue}`;

const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`;

/** With every column in use the nodes may be a little narrower, so the drawing still fits a common screen. */
const sizes = (columns: number) => (columns >= 5 ? { node: 164, gap: 54 } : { node: MIN_NODE, gap: MIN_GAP });

/** The narrowest the drawing can be with that many columns. */
export function minimumWidth(columns: number): number {
  const { node, gap } = sizes(columns);
  return PADDING * 2 + columns * node + (columns - 1) * gap;
}

function sameMachine(one: string | undefined, other: string | undefined): boolean {
  return !one || !other || one.toLowerCase() === other.toLowerCase();
}

function upOf(group: WorkerGroup): number {
  return group.Workers.filter((worker) => worker.State === 'Up').length;
}

/**
 * Turns what the broker and the Worker Control say into nodes and edges, with a state on
 * each. Positions come later, from {@link place}.
 *
 * @param rates messages per minute of each link, by {@link linkKey}
 * @param waiting queues that have had messages waiting for a while
 */
export function assemble(
  map: ParkMap,
  status: WorkerControlStatus | null,
  rates: ReadonlyMap<string, number>,
  waiting: ReadonlySet<string>,
  now: number,
  traffic: TrafficUpstreams | null = null,
  view: ParkView = { activeOnly: false, bundle: false, expanded: new Set() },
): { nodes: ParkNode[]; edges: ParkEdge[]; hidden: number } {
  const nodes: ParkNode[] = [];
  const edges: ParkEdge[] = [];
  const machine = status?.Service.Machine;

  const consumes = new Set(map.links.filter((link) => link.kind === 'consume').map((link) => link.application));
  const publishes = new Set(map.links.filter((link) => link.kind === 'publish').map((link) => link.application));

  const supervisor = status
    ? map.applications.find((application) => application.instances.some((instance) => instance.pid === status.Service.ProcessId && sameMachine(instance.host, machine)))
    : undefined;

  const taken = new Set<string>();
  for (const application of map.applications) {
    if (application === supervisor) {
      continue;
    }
    const groups = (status?.Groups ?? []).filter((group) =>
      group.Workers.some((worker) => application.instances.some((instance) => instance.pid === worker.ProcessId && sameMachine(instance.host, machine))),
    );
    groups.forEach((group) => taken.add(group.Name));

    const reasons: string[] = [];
    let tone: Tone = application.protocol === 'v1' ? 'neutral' : 'ok';
    let detail =
      application.protocol === 'v1'
        ? application.instances.length
          ? `protocolo 1.x · ${plural(application.instances.length, 'processo', 'processos')}`
          : 'protocolo 1.x'
        : plural(application.instances.length, 'instância', 'instâncias');

    if (application.protocol === 'v2' && application.instances.length === 0) {
      tone = 'off';
      detail = 'desconectada';
      reasons.push('Não está conectada agora; aparece pelo que publicou há pouco.');
    }
    const desired = groups.reduce((total, group) => total + group.DesiredWorkers, 0);
    const up = groups.reduce((total, group) => total + upOf(group), 0);
    if (groups.length) {
      detail = `${up} de ${desired} no ar`;
      if (up < desired) {
        tone = 'warn';
        reasons.push(`Faltam ${plural(desired - up, 'processo', 'processos')} para o que o Worker Control quer manter.`);
      }
      if (groups.some((group) => group.Unstable)) {
        tone = 'danger';
        reasons.push('Grupo instável: os processos estão caindo logo depois de iniciar.');
      }
    }

    nodes.push({
      id: application.id,
      kind: 'application',
      column: consumes.has(application.id) ? 2 : publishes.has(application.id) ? 0 : groups.length ? 2 : 0,
      // A 1.x client is known by its process when it is on the machine of the broker; else it is only an address.
      label: application.protocol === 'v1' && !application.instances.length ? `Cliente 1.x · ${application.name}` : application.name,
      detail,
      tone,
      reasons,
      figure: application.instances.length || null,
      application,
      groups,
      x: 0,
      y: 0,
    });
  }

  // Groups whose processes do not speak v2 (or are all down) have no application to stand for them.
  for (const group of status?.Groups ?? []) {
    if (taken.has(group.Name) || !group.Enabled) {
      continue;
    }
    const up = upOf(group);
    const reasons: string[] = [];
    let tone: Tone = 'neutral';
    if (up < group.DesiredWorkers) {
      tone = 'warn';
      reasons.push(`Faltam ${plural(group.DesiredWorkers - up, 'processo', 'processos')} para o que o Worker Control quer manter.`);
    }
    if (group.Unstable) {
      tone = 'danger';
      reasons.push('Grupo instável: os processos estão caindo logo depois de iniciar.');
    }
    nodes.push({
      id: 'group:' + group.Name,
      kind: 'group',
      column: 2,
      label: group.Name,
      detail: `${up} de ${group.DesiredWorkers} no ar · sem conexão v2`,
      tone,
      reasons,
      figure: group.Workers.length || null,
      groups: [group],
      x: 0,
      y: 0,
    });
  }

  const recentlyConsumed = new Set(
    map.links.filter((link) => link.kind === 'consume' && (link.bound || (link.lastSeen !== null && now - Date.parse(link.lastSeen) < 60_000))).map((link) => link.queue),
  );

  for (const queue of map.queues) {
    const reasons: string[] = [];
    let tone: Tone = queue.exists ? 'ok' : 'neutral';
    if (queue.deadLetters > 0) {
      tone = 'warn';
      reasons.push(`${plural(queue.deadLetters, 'mensagem morta guardada', 'mensagens mortas guardadas')}.`);
    }
    if (waiting.has(queue.name)) {
      tone = 'warn';
      reasons.push('Acumulando: há mensagens esperando sem que a fila esvazie.');
    }
    if (queue.pending > 0 && queue.consumers === 0 && !recentlyConsumed.has(queue.name)) {
      tone = 'danger';
      reasons.push('Há mensagens esperando e ninguém consumindo.');
    }
    if (queue.paused) {
      tone = tone === 'danger' ? 'danger' : 'off';
      reasons.push('Pausada: recebe e não entrega.');
    }
    nodes.push({
      id: 'queue:' + queue.name,
      kind: 'queue',
      column: 1,
      label: queue.name,
      detail: queue.paused
        ? 'pausada'
        : queue.pending > 0
          ? plural(queue.pending, 'pendente', 'pendentes')
          : queue.processing > 0
            ? `${queue.processing} em processamento`
            : 'vazia',
      tone,
      reasons,
      figure: queue.pending || null,
      queue,
      groups: [],
      x: 0,
      y: 0,
    });
  }

  const columnOf = new Map(nodes.map((node) => [node.id, node.column]));
  for (const link of map.links) {
    const column = columnOf.get(link.application);
    if (column === undefined) {
      continue;
    }
    const age = link.lastSeen === null ? Infinity : now - Date.parse(link.lastSeen);
    const rate = rates.get(linkKey(link)) ?? 0;
    const publish = link.kind === 'publish';
    edges.push({
      id: linkKey(link),
      from: publish ? link.application : 'queue:' + link.queue,
      to: publish ? 'queue:' + link.queue : link.application,
      kind: link.kind,
      rate,
      active: rate > 0 || age < 60_000,
      stale: !link.bound && age > 5 * 60_000,
      back: publish ? column > 1 : column < 1,
      link,
      path: '',
    });
  }

  if (supervisor && status) {
    const unstable = status.Groups.filter((group) => group.Unstable).length;
    nodes.push({
      id: supervisor.id,
      kind: 'supervisor',
      column: 3,
      label: 'Worker Control',
      detail: plural(status.Groups.filter((group) => group.Enabled).length, 'grupo', 'grupos'),
      tone: !status.Service.ZapMQ.Healthy ? 'warn' : unstable ? 'warn' : 'ok',
      reasons: [
        ...(status.Service.ZapMQ.Healthy ? [] : ['Sem contato estável com o ZapMQ.']),
        ...(unstable ? [`${plural(unstable, 'grupo instável', 'grupos instáveis')}.`] : []),
      ],
      figure: null,
      application: supervisor,
      groups: status.Groups,
      x: 0,
      y: 0,
    });
    for (const node of nodes) {
      if (node.kind !== 'supervisor' && node.groups.length) {
        edges.push({ id: 'supervise|' + node.id, from: supervisor.id, to: node.id, kind: 'supervise', rate: 0, active: false, stale: false, back: true, path: '' });
      }
    }
  }

  // An application that is a Windows service says so.
  for (const node of nodes) {
    if (node.kind === 'application' && node.application) {
      const services = (status?.Services ?? []).filter((service) => node.application!.instances.some((instance) => service.ProcessIds?.includes(instance.pid)));
      if (services.length) {
        node.services = services;
      }
    }
  }

  addProxy(nodes, edges, traffic);
  const hidden = simplify(nodes, edges, view);
  return { nodes, edges, hidden };
}

/**
 * Makes a drawing with many queues readable: leaves out the queues nothing went through, and
 * draws as one the queues that are used by exactly the same applications. A queue with
 * something wrong is always drawn, and by itself. Returns how many queues were left out.
 */
function simplify(nodes: ParkNode[], edges: ParkEdge[], view: ParkView): number {
  const remove = (ids: Set<string>) => {
    for (let i = nodes.length - 1; i >= 0; i--) {
      if (ids.has(nodes[i].id)) {
        nodes.splice(i, 1);
      }
    }
    for (let i = edges.length - 1; i >= 0; i--) {
      if (ids.has(edges[i].from) || ids.has(edges[i].to)) {
        edges.splice(i, 1);
      }
    }
  };
  const calm = (node: ParkNode) => node.tone === 'ok' || node.tone === 'neutral';

  let hidden = 0;
  if (view.activeOnly) {
    const publishedIn = new Set(edges.filter((edge) => edge.kind === 'publish').map((edge) => edge.to));
    const idle = new Set(
      nodes
        .filter((node) => node.kind === 'queue' && calm(node) && !publishedIn.has(node.id) && !node.queue!.pending && !node.queue!.processing)
        .map((node) => node.id),
    );
    hidden = idle.size;
    remove(idle);
  }

  if (!view.bundle) {
    return hidden;
  }

  // Queues with the same publishers and the same consumers tell the same story.
  const signature = new Map<string, string>();
  for (const node of nodes) {
    if (node.kind === 'queue' && calm(node)) {
      const from = edges.filter((edge) => edge.to === node.id).map((edge) => edge.kind + ':' + edge.from).sort();
      const to = edges.filter((edge) => edge.from === node.id).map((edge) => edge.kind + ':' + edge.to).sort();
      signature.set(node.id, from.join(',') + '>' + to.join(','));
    }
  }
  const groups = new Map<string, ParkNode[]>();
  for (const node of nodes) {
    const key = signature.get(node.id);
    if (key !== undefined) {
      groups.set(key, [...(groups.get(key) ?? []), node]);
    }
  }

  for (const [key, members] of groups) {
    const id = 'bundle:' + key;
    if (members.length < BUNDLE_FROM || view.expanded.has(id)) {
      continue;
    }
    const ids = new Set(members.map((member) => member.id));
    const queues = members.map((member) => member.queue!).sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
    const pending = queues.reduce((total, queue) => total + queue.pending, 0);

    // One line for each application joined to the bundle, standing for all the lines to its queues.
    const merged = new Map<string, ParkEdge>();
    for (const edge of edges) {
      const inward = ids.has(edge.to);
      if (!inward && !ids.has(edge.from)) {
        continue;
      }
      const other = inward ? edge.from : edge.to;
      const mergedId = `${edge.kind}|${other}|${id}`;
      const sum = merged.get(mergedId);
      if (sum) {
        sum.rate += edge.rate;
        sum.active ||= edge.active;
        sum.stale &&= edge.stale;
      } else {
        merged.set(mergedId, { ...edge, id: mergedId, from: inward ? other : id, to: inward ? id : other, link: undefined, path: '' });
      }
    }

    remove(ids);
    edges.push(...merged.values());
    nodes.push({
      id,
      kind: 'bundle',
      column: 1,
      label: `${queues.length} filas`,
      detail: queues.map((queue) => queue.name).join(', '),
      tone: 'ok',
      reasons: [],
      figure: pending || null,
      bundle: queues,
      groups: [],
      x: 0,
      y: 0,
    });
  }
  return hidden;
}

/**
 * The reverse proxy and what it forwards to: each address it sends requests to is joined to
 * the application whose process answers there. An application that is not connected to the
 * broker is drawn too, by the name of its process, or by how the proxy calls it when the
 * process could not be told.
 */
function addProxy(nodes: ParkNode[], edges: ParkEdge[], traffic: TrafficUpstreams | null): void {
  if (!traffic?.Upstreams.length) {
    return;
  }
  const targets = new Map<string, HttpTraffic>();
  const byId = new Map(nodes.map((node) => [node.id, node]));

  for (const upstream of traffic.Upstreams) {
    const pids = new Set(upstream.Processes.map((process) => process.ProcessId));
    let node = nodes.find((candidate) => candidate.kind === 'application' && candidate.application?.instances.some((instance) => pids.has(instance.pid)));
    if (!node) {
      // By the name of the process when it is known: an application connects to the broker under that name.
      const name = upstream.Processes[0]?.Name;
      const id = name ? 'v2:' + name : 'http:' + upstream.App;
      // An application talks to the broker under the name of its process, in either protocol.
      node =
        byId.get(id) ??
        [...byId.values()].find((candidate) => candidate.id.toLowerCase() === id.toLowerCase()) ??
        (name ? nodes.find((candidate) => candidate.kind === 'application' && candidate.application?.name.toLowerCase() === name.toLowerCase()) : undefined);
      if (!node) {
        // A site of the web server with nothing running from its folder: the application was put to rest for want of requests.
        const resting = !name && !!upstream.Site;
        node = {
          id,
          kind: 'application',
          column: 0,
          label: name ?? upstream.App,
          detail: name ? 'sem conexão com o ZapMQ' : resting ? 'sem processo no momento' : 'processo não identificado',
          tone: resting ? 'off' : 'neutral',
          reasons: resting ? [`O site ${upstream.Site} não tem nenhum processo rodando agora: o servidor web encerra a aplicação quando ela fica sem requisições e a inicia de novo na próxima.`] : [],
          figure: null,
          groups: [],
          x: 0,
          y: 0,
        };
        nodes.push(node);
        byId.set(id, node);
      }
    }

    const sum = targets.get(node.id) ?? { apps: [], upstreams: [], count: 0, perMinute: 0, serverErrors: 0, p95: null };
    if (!sum.apps.includes(upstream.App)) {
      sum.apps.push(upstream.App);
    }
    sum.upstreams.push(upstream.Upstream);
    sum.count += upstream.Count;
    sum.serverErrors += upstream.S5;
    sum.p95 = upstream.P95 === null ? sum.p95 : Math.max(sum.p95 ?? 0, upstream.P95);
    sum.perMinute = sum.count / Math.max(1, traffic.Minutes);
    targets.set(node.id, sum);
  }

  const all: HttpTraffic = { apps: [], upstreams: [], count: 0, perMinute: 0, serverErrors: 0, p95: null };
  for (const [id, sum] of targets) {
    const node = byId.get(id)!;
    node.http = sum;
    // A handful of requests says little; a share of many does.
    const failing = sum.count >= 20 && sum.serverErrors / sum.count >= 0.02;
    if (failing) {
      node.tone = 'danger';
      node.reasons.push(`${((sum.serverErrors / sum.count) * 100).toFixed(1).replace('.', ',')}% das requisições recebidas do proxy terminaram em erro do servidor.`);
    }
    edges.push({ id: 'http|' + id, from: 'proxy', to: id, kind: 'http', rate: sum.perMinute, active: sum.perMinute > 0, stale: false, back: false, path: '' });
    all.apps.push(...sum.apps);
    all.count += sum.count;
    all.serverErrors += sum.serverErrors;
    all.p95 = sum.p95 === null ? all.p95 : Math.max(all.p95 ?? 0, sum.p95);
  }
  all.perMinute = all.count / Math.max(1, traffic.Minutes);

  const failing = all.count >= 20 && all.serverErrors / all.count >= 0.02;
  nodes.push({
    id: 'proxy',
    kind: 'proxy',
    column: -1,
    label: 'Proxy',
    detail: `${all.perMinute >= 10 ? Math.round(all.perMinute) : all.perMinute.toFixed(1).replace('.', ',')}/min`,
    tone: failing ? 'danger' : 'ok',
    reasons: failing ? [`${((all.serverErrors / all.count) * 100).toFixed(1).replace('.', ',')}% do que foi encaminhado terminou em erro do servidor.`] : [],
    figure: targets.size,
    groups: [],
    http: all,
    x: 0,
    y: 0,
  });
}

/**
 * Gives every node a place: one column per role, and inside each column an order that keeps
 * the lines from crossing more than they have to.
 */
export function place(nodes: ParkNode[], edges: ParkEdge[], available: number): Park {
  const used = ([-1, 0, 1, 2, 3] as Column[]).filter((column) => (column >= 0 && column < 3) || nodes.some((node) => node.column === column));
  const width = Math.max(available, minimumWidth(used.length));
  const least = sizes(used.length);
  // The columns at the ends hold one node each (the proxy, the Worker Control): they get by
  // with less, and what they give up goes to the names in the middle.
  const slim = (column: Column) => column === -1 || column === 3;
  const slims = used.filter(slim).length;
  const room = width - PADDING * 2 - (used.length - 1) * least.gap;
  const nodeWidth = Math.min(MAX_NODE, Math.max(least.node, (room - slims * SLIM_NODE) / (used.length - slims)));
  const widthOf = (column: Column) => (slim(column) ? Math.min(SLIM_NODE, nodeWidth) : nodeWidth);
  const gap = (width - PADDING * 2 - used.reduce<number>((total, column) => total + widthOf(column), 0)) / (used.length - 1);
  let left = PADDING;
  const columns = used.map((column) => {
    const placed = { column, x: left, width: widthOf(column) };
    left += placed.width + gap;
    return placed;
  });

  const byColumn = new Map<Column, ParkNode[]>(used.map((column) => [column, nodes.filter((node) => node.column === column).sort((a, b) => a.label.localeCompare(b.label, 'pt-BR'))]));
  const flow = edges.filter((edge) => edge.kind !== 'supervise');
  const neighbours = new Map<string, string[]>();
  for (const edge of flow) {
    neighbours.set(edge.from, [...(neighbours.get(edge.from) ?? []), edge.to]);
    neighbours.set(edge.to, [...(neighbours.get(edge.to) ?? []), edge.from]);
  }

  // Each node goes towards the middle of the ones it is joined to; a few rounds settle it.
  const rank = new Map<string, number>();
  const rankAll = () => byColumn.forEach((list) => list.forEach((node, index) => rank.set(node.id, list.length > 1 ? index / (list.length - 1) : 0.5)));
  rankAll();
  for (let round = 0; round < 4; round++) {
    for (const column of [2, 0, 1] as Column[]) {
      const list = byColumn.get(column);
      if (!list) {
        continue;
      }
      const wish = new Map(
        list.map((node) => {
          const joined = (neighbours.get(node.id) ?? []).map((id) => rank.get(id)).filter((value): value is number => value !== undefined);
          // Nodes joined to nothing go to the end, in their alphabetical order.
          return [node.id, joined.length ? joined.reduce((total, value) => total + value, 0) / joined.length : 2 + (rank.get(node.id) ?? 0)];
        }),
      );
      list.sort((a, b) => wish.get(a.id)! - wish.get(b.id)! || a.label.localeCompare(b.label, 'pt-BR'));
      rankAll();
    }
  }

  const rows = Math.max(1, ...[...byColumn.values()].map((list) => list.length));
  const height = HEADER + rows * ROW + PADDING;
  for (const { column, x, width: columnWidth } of columns) {
    const list = byColumn.get(column) ?? [];
    const top = HEADER + ((rows - list.length) * ROW) / 2;
    list.forEach((node, index) => {
      node.x = x;
      node.y = top + index * ROW;
      node.width = columnWidth;
    });
  }

  const at = new Map(nodes.map((node) => [node.id, node]));
  for (const edge of edges) {
    const from = at.get(edge.from);
    const to = at.get(edge.to);
    if (!from || !to) {
      continue;
    }
    // Lines that go against the flow run a little lower, so they do not hide the ones that go with it.
    const shift = edge.kind === 'supervise' || edge.kind === 'http' ? 0 : edge.back ? 9 : -4;
    const leftToRight = from.x < to.x;
    const x1 = leftToRight ? from.x + (from.width ?? nodeWidth) : from.x;
    const x2 = leftToRight ? to.x - 5 : to.x + (to.width ?? nodeWidth) + 5;
    const y1 = from.y + NODE_HEIGHT / 2 + shift;
    const y2 = to.y + NODE_HEIGHT / 2 + shift;
    const bend = (x2 - x1) / 2;
    edge.path = `M${x1.toFixed(1)},${y1.toFixed(1)} C${(x1 + bend).toFixed(1)},${y1.toFixed(1)} ${(x2 - bend).toFixed(1)},${y2.toFixed(1)} ${x2.toFixed(1)},${y2.toFixed(1)}`;
  }

  return { nodes, edges: edges.filter((edge) => edge.path), width, height, nodeWidth, columns };
}
