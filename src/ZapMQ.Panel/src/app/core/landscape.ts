import { Landscape, MapQueue, ParkMap, TrafficUpstreams, WorkerControlStatus } from './models';

export type Tone = 'ok' | 'warn' | 'danger' | 'off' | 'neutral';
export type PartKind = 'web' | 'proxy' | 'api' | 'worker' | 'service' | 'client' | 'broker' | 'supervisor' | 'database';

/** Where a kind of part is drawn, from who asks on the left to what is asked on the right. */
export const COLUMNS: { title: string; kinds: PartKind[] }[] = [
  { title: 'Entrada', kinds: ['web', 'proxy'] },
  { title: 'APIs', kinds: ['api'] },
  { title: 'Micro serviços', kinds: ['worker'] },
  { title: 'Serviços e infraestrutura', kinds: ['service', 'client', 'broker', 'supervisor'] },
  { title: 'Banco de dados', kinds: ['database'] },
];

/** A queue as one part uses it. */
export interface PartQueue {
  name: string;
  pending: number;
  deadLetters: number;
  paused: boolean;
  /** Something went through this use of it in the last minute and a half. */
  active: boolean;
  /** For a queue the part publishes in: nobody is consuming it now. */
  unattended: boolean;
}

/** How a part stands with the database. */
export interface PartDatabase {
  sessions: number;
  running: number;
  /** Seconds since it last asked for something; null when that is not known. */
  idle: number | null;
  /** Doing something now, or a moment ago. */
  active: boolean;
  programs: string[];
}

export interface PartHttp {
  count: number;
  perMinute: number;
  errors: number;
  p95: number | null;
}

/** One component of the environment, as it is drawn. */
export interface Part {
  id: string;
  kind: PartKind;
  label: string;
  /** One line under the name: how many are up, the ports, the state. */
  detail: string;
  tone: Tone;
  /** What is wrong, in words; empty when nothing is. */
  reasons: string[];
  processIds: number[];
  /** How it talks to the broker: the protocol of 1.x, of 2.x, or both when it has programs of each. Empty when it does not. */
  protocols: ('v1' | 'v2')[];
  consumes: PartQueue[];
  publishes: PartQueue[];
  database: PartDatabase | null;
  http: PartHttp | null;
  /** More to say in the panel of the part: label and value. */
  facts: { label: string; value: string }[];
  /** The screen of the panel that is about this part, if there is one. */
  link: { path: (string | number)[]; label: string } | null;
  x: number;
  y: number;
}

export interface Wire {
  id: string;
  from: string;
  to: string;
  kind: 'http' | 'message' | 'database';
  /** Something went through it just now. */
  active: boolean;
  /** For messages: the queues that join the two. */
  queues: string[];
  /** What to say about it: requests per minute, sessions. */
  note: string;
  path: string;
}

export interface Drawing {
  parts: Part[];
  wires: Wire[];
  width: number;
  height: number;
  columns: { title: string; x: number; count: number }[];
  /** Who is connected to the database and is none of the parts drawn. */
  strangers: { host: string; program: string; sessions: number }[];
}

export const PART_WIDTH = 214;
export const PART_HEIGHT = 72;
const ROW = PART_HEIGHT + 10;
const GAP = 92;
const TOP = 40;
const SIDE = 20;
/** For how long after the last message a use of a queue is still said to be moving. */
const RECENT = 90_000;

const same = (one: string | undefined | null, other: string | undefined | null) => !!one && !!other && one.toLowerCase() === other.toLowerCase();
const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`;
const worst = (tones: Tone[]): Tone => (tones.includes('danger') ? 'danger' : tones.includes('warn') ? 'warn' : tones.includes('ok') ? 'ok' : (tones[0] ?? 'neutral'));

/**
 * Puts together what the broker, the Worker Control and the reverse proxy know into the parts
 * of the environment and what joins them. Positions come later, from {@link place}.
 */
export function assemble(map: ParkMap | null, status: WorkerControlStatus | null, land: Landscape | null, traffic: TrafficUpstreams | null, now: number): Omit<Drawing, 'width' | 'height' | 'columns'> {
  const parts: Part[] = [];
  const wires: Wire[] = [];
  const machine = land?.Machine ?? status?.Service.Machine ?? null;
  const part = (kind: PartKind, id: string, label: string, detail: string, tone: Tone, more: Partial<Part> = {}): Part => {
    const made: Part = { id, kind, label, detail, tone, reasons: [], processIds: [], protocols: [], consumes: [], publishes: [], database: null, http: null, facts: [], link: null, x: 0, y: 0, ...more };
    parts.push(made);
    return made;
  };

  // ── What runs on the machine ────────────────────────────────────────────
  for (const app of land?.Frontends ?? []) {
    part('web', `web:${app.Name}`, app.Name, plural(app.Modules, 'módulo', 'módulos'), app.Problem ? 'warn' : 'ok', {
      reasons: app.Problem ? [app.Problem] : [],
      facts: app.BaseUrl ? [{ label: 'Endereço', value: app.BaseUrl }] : [],
      link: { path: ['/web'], label: 'Aplicação web' },
    });
  }

  for (const api of land?.Apis ?? []) {
    const known = api.Sites.filter((site) => site.Started !== null);
    const stopped = known.filter((site) => site.Started === false);
    // The worker processes of the web server, and what they started from the folder of the application.
    const ids = [...new Set([...api.Sites.flatMap((site) => site.ProcessIds), ...(api.ProcessIds ?? [])])];
    const ports = api.Sites.map((site) => site.Port).filter((port): port is number => port !== null);
    const tone: Tone = !known.length ? 'neutral' : stopped.length === known.length ? 'danger' : stopped.length ? 'warn' : 'ok';
    part('api', `api:${api.Name}`, api.Name, `${plural(api.Sites.length, 'instância', 'instâncias')}${ports.length ? ' · ' + (ports.length > 2 ? `${ports[0]}–${ports[ports.length - 1]}` : ports.join(', ')) : ''}${tone === 'ok' && !ids.length ? ' · ociosa' : ''}`, tone, {
      processIds: ids,
      reasons: stopped.length ? [`${stopped.length === known.length ? 'Todos os sites estão parados' : 'Parado: ' + stopped.map((site) => site.Name).join(', ')}.`] : [],
      facts: [
        ...api.Sites.map((site) => ({ label: site.Name, value: `${site.Port ?? '—'} · ${site.Started === null ? 'estado desconhecido' : site.Started ? (site.ProcessIds.length ? 'no ar' : 'no ar, ociosa') : 'parado'}` })),
        ...(api.Version ? [{ label: 'Versão', value: api.Version }] : []),
      ],
      link: { path: ['/workers/aplicacoes', 'api', api.Name], label: 'Aplicação' },
    });
  }

  for (const group of status?.Groups ?? []) {
    const up = group.Workers.filter((worker) => worker.State === 'Up').length;
    const tone: Tone = !group.Enabled ? 'off' : group.Unstable || (group.DesiredWorkers > 0 && up === 0) ? 'danger' : up < group.DesiredWorkers ? 'warn' : 'ok';
    part('worker', `worker:${group.Name}`, group.Name, group.Enabled ? `${up} de ${group.DesiredWorkers} no ar` : 'desabilitado', tone, {
      processIds: group.Workers.map((worker) => worker.ProcessId),
      reasons: [
        ...(group.Unstable ? ['Os processos estão falhando logo depois de iniciar.'] : []),
        ...(group.Enabled && up < group.DesiredWorkers ? [`Faltam ${group.DesiredWorkers - up} de ${group.DesiredWorkers} processos.`] : []),
      ],
      facts: [{ label: 'Executável', value: group.ApplicationFullPath }],
      link: { path: ['/workers'], label: 'Micro serviços' },
    });
  }

  for (const service of status?.Services ?? []) {
    if (service.IsSupervisor) {
      continue;
    }
    // The reverse proxy is a service like the others, with a place of its own in the drawing.
    const proxy = /nginx/i.test(service.Name) || (service.Children ?? []).some((child) => /nginx/i.test(child));
    const tone: Tone = service.State === 'Running' ? (service.Check?.Ok === false ? 'warn' : 'ok') : service.State === 'Stopped' || service.State === 'Missing' ? 'danger' : 'warn';
    const words: Record<string, string> = { Running: 'em execução', Stopped: 'parado', Starting: 'iniciando', Stopping: 'parando', Paused: 'pausado', Missing: 'não instalado' };
    part(proxy ? 'proxy' : 'service', proxy ? 'proxy' : `service:${service.Name}`, proxy ? 'Proxy reverso' : service.DisplayName || service.Name, proxy ? `${service.Name} · ${words[service.State] ?? service.State}` : (words[service.State] ?? service.State), tone, {
      processIds: service.ProcessIds ?? (service.ProcessId ? [service.ProcessId] : []),
      reasons: [
        ...(tone === 'danger' ? [`O serviço ${service.Name} está ${words[service.State] ?? service.State}.`] : []),
        ...(service.Check?.Ok === false ? [`Não respondeu à verificação: ${service.Check.Detail ?? service.Check.Target}.`] : []),
      ],
      facts: [{ label: 'Serviço', value: service.Name }, ...(service.ExecutablePath ? [{ label: 'Executável', value: service.ExecutablePath }] : [])],
      link: proxy ? { path: ['/workers/proxy'], label: 'Proxy reverso' } : { path: ['/workers/servicos'], label: 'Serviços do Windows' },
    });
  }
  if (traffic?.Upstreams.length && !parts.some((item) => item.kind === 'proxy')) {
    part('proxy', 'proxy', 'Proxy reverso', 'encaminhando', 'neutral', { link: { path: ['/workers/proxy'], label: 'Proxy reverso' } });
  }

  part('broker', 'broker', 'ZapMQ', map ? `${plural(map.queues.length, 'fila', 'filas')} · ${plural(map.applications.length, 'aplicação', 'aplicações')}` : 'sem resposta', map ? 'ok' : 'danger', {
    reasons: map ? [] : ['O painel não conseguiu ler o estado da mensageria.'],
    link: { path: ['/filas'], label: 'Mensageria' },
  });
  part('supervisor', 'supervisor', 'Worker Control', status ? `versão ${status.Version}` : 'sem resposta', status ? (status.Service.ZapMQ.Healthy ? 'ok' : 'warn') : 'danger', {
    processIds: status ? [status.Service.ProcessId] : [],
    reasons: status ? (status.Service.ZapMQ.Healthy ? [] : ['Sem contato estável com o ZapMQ.']) : ['Sem ele, o mapa não sabe o que deveria estar no ar nesta máquina.'],
    link: { path: ['/workers'], label: 'Processos e serviços' },
  });

  const database = land?.Database
    ? part('database', 'database', land.Database.Name || 'Banco de dados', land.Database.Online === false ? 'fora do ar' : land.Database.Online === null ? 'verificando' : (land.Database.Server ?? 'no ar'),
        land.Database.Online === false ? 'danger' : land.Database.Online ? 'ok' : 'neutral', {
          reasons: land.Database.Online === false ? [land.Database.Error ?? 'A instância não respondeu.'] : [],
          facts: land.Database.Server ? [{ label: 'Instância', value: land.Database.Server }] : [],
          link: { path: ['/banco'], label: 'Banco de dados' },
        })
    : null;

  // ── Who talks through the broker ────────────────────────────────────────
  const owner = new Map<number, Part>();
  for (const item of parts) {
    for (const id of item.processIds) {
      owner.set(id, item);
    }
  }
  const queues = new Map<string, MapQueue>((map?.queues ?? []).map((queue) => [queue.name, queue]));
  const partOf = new Map<string, Part>();
  for (const application of map?.applications ?? []) {
    const local = application.instances.find((instance) => (!machine || !instance.host || same(instance.host, machine)) && owner.has(instance.pid));
    let found = local ? owner.get(local.pid)! : null;
    if (found?.kind === 'supervisor') {
      // What the Worker Control says through the broker is its own business, not the environment's.
      continue;
    }
    if (!found) {
      found = part('client', `client:${application.id}`, application.name, plural(application.instances.length, 'instância', 'instâncias'), application.instances.length ? 'ok' : 'off', {
        facts: application.instances.map((instance) => ({ label: instance.host || 'máquina', value: `processo ${instance.pid}` })),
      });
    }
    if (!found.protocols.includes(application.protocol)) {
      found.protocols = [...found.protocols, application.protocol].sort();
    }
    partOf.set(application.id, found);
  }

  const consumers = new Map<string, Set<string>>();
  const publishers = new Map<string, Map<string, boolean>>();
  for (const link of map?.links ?? []) {
    const user = partOf.get(link.application);
    const queue = queues.get(link.queue);
    if (!user || !queue) {
      continue;
    }
    const active = !!link.lastSeen && now - new Date(link.lastSeen).getTime() <= RECENT;
    const list = link.kind === 'consume' ? user.consumes : user.publishes;
    const had = list.find((item) => item.name === queue.name);
    if (had) {
      had.active ||= active;
    } else {
      list.push({ name: queue.name, pending: queue.pending, deadLetters: queue.deadLetters, paused: queue.paused, active, unattended: link.kind === 'publish' && queue.consumers === 0 });
    }
    if (link.kind === 'consume') {
      (consumers.get(queue.name) ?? consumers.set(queue.name, new Set()).get(queue.name)!).add(user.id);
    } else {
      const by = publishers.get(queue.name) ?? publishers.set(queue.name, new Map()).get(queue.name)!;
      by.set(user.id, (by.get(user.id) ?? false) || active);
    }
  }
  const joined = new Map<string, Wire>();
  for (const [queue, by] of publishers) {
    for (const [from, active] of by) {
      for (const to of consumers.get(queue) ?? []) {
        if (from === to) {
          continue;
        }
        const id = `message|${from}|${to}`;
        const wire = joined.get(id) ?? joined.set(id, { id, from, to, kind: 'message', active: false, queues: [], note: '', path: '' }).get(id)!;
        wire.queues.push(queue);
        wire.active ||= active;
      }
    }
  }
  for (const wire of joined.values()) {
    wire.queues.sort((a, b) => a.localeCompare(b, 'pt-BR'));
    wire.note = wire.queues.length === 1 ? wire.queues[0] : plural(wire.queues.length, 'fila', 'filas');
    wires.push(wire);
  }
  for (const item of parts) {
    item.consumes.sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
    item.publishes.sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
    const dead = item.consumes.reduce((total, queue) => total + queue.deadLetters, 0);
    const paused = item.consumes.filter((queue) => queue.paused);
    if (dead) {
      item.reasons.push(`${plural(dead, 'mensagem morta', 'mensagens mortas')} nas filas que consome.`);
    }
    if (paused.length) {
      item.reasons.push(`Fila pausada: ${paused.map((queue) => queue.name).join(', ')}.`);
    }
    if ((dead || paused.length) && item.tone === 'ok') {
      item.tone = 'warn';
    }
  }

  // ── What the reverse proxy sends, and to whom ───────────────────────────
  const proxy = parts.find((item) => item.kind === 'proxy');
  const minutes = Math.max(1, traffic?.Minutes ?? 1);
  if (proxy) {
    const received = new Map<string, PartHttp>();
    for (const upstream of traffic?.Upstreams ?? []) {
      const api = upstream.Site ? parts.find((item) => item.kind === 'api' && (land?.Apis ?? []).some((known) => `api:${known.Name}` === item.id && known.Sites.some((site) => same(site.Name, upstream.Site)))) : null;
      const target = api ?? upstream.Processes.map((process) => owner.get(process.ProcessId)).find((found) => found && found.id !== proxy.id);
      if (!target) {
        continue;
      }
      const sum = received.get(target.id) ?? received.set(target.id, { count: 0, perMinute: 0, errors: 0, p95: null }).get(target.id)!;
      sum.count += upstream.Count;
      sum.errors += upstream.S5;
      sum.p95 = Math.max(sum.p95 ?? 0, upstream.P95 ?? 0) || null;
    }
    let all = 0;
    let failed = 0;
    for (const [id, http] of received) {
      http.perMinute = http.count / minutes;
      parts.find((item) => item.id === id)!.http = http;
      all += http.count;
      failed += http.errors;
      wires.push({ id: `http|${id}`, from: proxy.id, to: id, kind: 'http', active: http.count > 0, queues: [], note: http.count ? `${rate(http.perMinute)}${http.errors ? ` · ${http.errors} com erro` : ''}` : 'sem requisições no período', path: '' });
    }
    proxy.http = { count: all, perMinute: all / minutes, errors: failed, p95: null };
    for (const web of parts.filter((item) => item.kind === 'web')) {
      wires.push({ id: `http|${web.id}`, from: web.id, to: proxy.id, kind: 'http', active: all > 0, queues: [], note: 'requisições dos navegadores', path: '' });
    }
  }

  // ── Who is connected to the database ────────────────────────────────────
  const strangers: Drawing['strangers'] = [];
  if (database) {
    for (const client of land?.Database?.Clients ?? []) {
      const user = (!machine || same(client.Host, machine)) && client.ProcessId > 0 ? owner.get(client.ProcessId) : undefined;
      if (!user || user.id === database.id) {
        const known = strangers.find((item) => same(item.host, client.Host) && item.program === client.Program);
        if (known) {
          known.sessions += client.Sessions;
        } else {
          strangers.push({ host: client.Host, program: client.Program, sessions: client.Sessions });
        }
        continue;
      }
      const link = (user.database ??= { sessions: 0, running: 0, idle: null, active: false, programs: [] });
      link.sessions += client.Sessions;
      link.running += client.Running;
      link.idle = client.IdleSeconds === null ? link.idle : link.idle === null ? client.IdleSeconds : Math.min(link.idle, client.IdleSeconds);
      if (client.Program && !link.programs.includes(client.Program)) {
        link.programs.push(client.Program);
      }
    }
    for (const item of parts) {
      if (item.database) {
        item.database.active = item.database.running > 0 || (item.database.idle !== null && item.database.idle <= 60);
        wires.push({ id: `database|${item.id}`, from: item.id, to: database.id, kind: 'database', active: item.database.active, queues: [], note: `${plural(item.database.sessions, 'conexão', 'conexões')}${item.database.running ? ` · ${item.database.running} em uso agora` : ''}`, path: '' });
      }
    }
    strangers.sort((a, b) => b.sessions - a.sessions);
  }

  return { parts, wires, strangers };
}

const RATE = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });

export function rate(perMinute: number): string {
  // A handful in the whole period is not nothing.
  if (perMinute > 0 && perMinute < 0.1) {
    return 'menos de 0,1/min';
  }
  return `${RATE.format(perMinute >= 100 ? Math.round(perMinute) : perMinute)}/min`;
}

/** Gives every part its place, column by column, and draws what joins them. */
export function place(assembled: Omit<Drawing, 'width' | 'height' | 'columns'>): Drawing {
  const rank: Record<Tone, number> = { danger: 0, warn: 1, ok: 2, neutral: 3, off: 4 };
  const columns: Drawing['columns'] = [];
  let x = SIDE;
  let tallest = 0;
  for (const column of COLUMNS) {
    const mine = assembled.parts
      .filter((item) => column.kinds.includes(item.kind))
      .sort((a, b) => column.kinds.indexOf(a.kind) - column.kinds.indexOf(b.kind) || a.label.localeCompare(b.label, 'pt-BR'));
    if (!mine.length) {
      continue;
    }
    mine.forEach((item, index) => {
      item.x = x;
      item.y = TOP + index * ROW;
    });
    columns.push({ title: column.title, x, count: mine.length });
    tallest = Math.max(tallest, mine.length);
    x += PART_WIDTH + GAP;
  }
  const height = TOP + tallest * ROW + SIDE;
  // A column with little in it sits in the middle of the drawing, not at its top.
  for (const column of columns) {
    const mine = assembled.parts.filter((item) => item.x === column.x);
    const shift = Math.max(0, (height - TOP - SIDE - mine.length * ROW) / 2);
    mine.forEach((item) => (item.y += Math.min(shift, 220)));
  }

  const byId = new Map(assembled.parts.map((item) => [item.id, item]));
  const wires = assembled.wires.filter((wire) => byId.has(wire.from) && byId.has(wire.to));
  for (const wire of wires) {
    const from = byId.get(wire.from)!;
    const to = byId.get(wire.to)!;
    const y1 = from.y + PART_HEIGHT / 2;
    const y2 = to.y + PART_HEIGHT / 2;
    if (to.x > from.x) {
      const x1 = from.x + PART_WIDTH;
      const bend = Math.max(40, (to.x - x1) * 0.45);
      wire.path = `M${x1},${y1} C${x1 + bend},${y1} ${to.x - bend},${y2} ${to.x},${y2}`;
    } else if (to.x < from.x) {
      // Back to the left: out of the left side and into the right side of who is before.
      const x2 = to.x + PART_WIDTH;
      const bend = Math.max(40, (from.x - x2) * 0.45);
      wire.path = `M${from.x},${y1} C${from.x - bend},${y1} ${x2 + bend},${y2} ${x2},${y2}`;
    } else {
      // In the same column: a loop out of the right side.
      const x1 = from.x + PART_WIDTH;
      const out = 34 + Math.min(40, Math.abs(y2 - y1) / 8);
      wire.path = `M${x1},${y1} C${x1 + out},${y1} ${x1 + out},${y2} ${x1},${y2}`;
    }
  }
  // The worst first in the list of what asks for attention; the drawing keeps its order.
  void rank;
  return { ...assembled, wires, width: x - GAP + SIDE, height, columns };
}

export { worst };
