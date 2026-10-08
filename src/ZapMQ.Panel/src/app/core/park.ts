import { MapApplication, MapLink, MapQueue, ParkMap, WorkerControlStatus, WorkerGroup } from './models';

export type Tone = 'ok' | 'warn' | 'danger' | 'off' | 'neutral';
export type NodeKind = 'application' | 'queue' | 'supervisor' | 'group';

/** Publishers, queues, consumers and, when there is one, the Worker Control. */
export type Column = 0 | 1 | 2 | 3;

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
  /** Groups of the Worker Control this node stands for. */
  groups: WorkerGroup[];
  x: number;
  y: number;
}

export interface ParkEdge {
  id: string;
  from: string;
  to: string;
  kind: 'publish' | 'consume' | 'supervise';
  /** Messages per minute, lately. */
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
  /** Left edge of each column in use, by column. */
  columns: { column: Column; x: number }[];
}

export const NODE_HEIGHT = 52;
const ROW = NODE_HEIGHT + 14;
const HEADER = 34;
const PADDING = 16;
const MIN_NODE = 184;
const MAX_NODE = 260;
const MIN_GAP = 72;

export const linkKey = (link: MapLink) => `${link.kind}|${link.application}|${link.queue}`;

const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`;

/** The narrowest the drawing can be with that many columns. */
export function minimumWidth(columns: number): number {
  return PADDING * 2 + columns * MIN_NODE + (columns - 1) * MIN_GAP;
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
): { nodes: ParkNode[]; edges: ParkEdge[] } {
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
    let detail = application.protocol === 'v1' ? 'protocolo 1.x' : plural(application.instances.length, 'instância', 'instâncias');

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
      label: application.protocol === 'v1' ? `Cliente 1.x · ${application.name}` : application.name,
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
      detail: `${plural(status.Groups.filter((group) => group.Enabled).length, 'grupo', 'grupos')} · ${status.Service.Machine}`,
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

  return { nodes, edges };
}

/**
 * Gives every node a place: one column per role, and inside each column an order that keeps
 * the lines from crossing more than they have to.
 */
export function place(nodes: ParkNode[], edges: ParkEdge[], available: number): Park {
  const used = ([0, 1, 2, 3] as Column[]).filter((column) => column < 3 || nodes.some((node) => node.column === 3));
  const width = Math.max(available, minimumWidth(used.length));
  const nodeWidth = Math.min(MAX_NODE, Math.max(MIN_NODE, (width - PADDING * 2 - (used.length - 1) * MIN_GAP) / used.length));
  const gap = (width - PADDING * 2 - used.length * nodeWidth) / (used.length - 1);
  const columns = used.map((column, index) => ({ column, x: PADDING + index * (nodeWidth + gap) }));

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
  for (const { column, x } of columns) {
    const list = byColumn.get(column) ?? [];
    const top = HEADER + ((rows - list.length) * ROW) / 2;
    list.forEach((node, index) => {
      node.x = x;
      node.y = top + index * ROW;
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
    const shift = edge.kind === 'supervise' ? 0 : edge.back ? 9 : -4;
    const leftToRight = from.x < to.x;
    const x1 = leftToRight ? from.x + nodeWidth : from.x;
    const x2 = leftToRight ? to.x - 5 : to.x + nodeWidth + 5;
    const y1 = from.y + NODE_HEIGHT / 2 + shift;
    const y2 = to.y + NODE_HEIGHT / 2 + shift;
    const bend = (x2 - x1) / 2;
    edge.path = `M${x1.toFixed(1)},${y1.toFixed(1)} C${(x1 + bend).toFixed(1)},${y1.toFixed(1)} ${(x2 - bend).toFixed(1)},${y2.toFixed(1)} ${x2.toFixed(1)},${y2.toFixed(1)}`;
  }

  return { nodes, edges: edges.filter((edge) => edge.path), width, height, nodeWidth, columns };
}
