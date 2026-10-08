import { HttpErrorResponse } from '@angular/common/http';
import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { Api } from '../core/api';
import { AgoPipe, NumPipe } from '../core/format';
import { ParkMap, TrafficUpstreams, WorkerControlStatus } from '../core/models';
import { Column, NODE_HEIGHT, ParkEdge, ParkNode, assemble, linkKey, place } from '../core/park';
import { FormsModule } from '@angular/forms';

const TITLES: Record<Column, string> = { [-1]: 'Entrada', 0: 'Publicam', 1: 'Filas', 2: 'Consomem', 3: 'Supervisão' };
const ICONS = { application: 'deployed_code', queue: 'stacks', supervisor: 'precision_manufacturing', group: 'memory', proxy: 'dns', bundle: 'stacks' } as const;
const RATE = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });
const RATE_WHOLE = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

/** How long a queue has to keep messages waiting before it is said to be accumulating. */
const WAITING_SAMPLES = 6;
/** Over how long the rate of a link is measured. */
const RATE_WINDOW = 60_000;

/**
 * Everything that talks through the broker, in one drawing: who publishes on the left, the
 * queues in the middle, who consumes on the right, and the Worker Control over what it keeps
 * running.
 */
@Component({
  selector: 'zap-map',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, NumPipe, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './map.html',
  styleUrl: './map.scss',
})
export class MapPage implements OnInit, AfterViewInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly canvas = viewChild<ElementRef<HTMLElement>>('canvas');
  private timer: ReturnType<typeof setInterval> | null = null;
  private observer: ResizeObserver | null = null;

  /** What each link had counted, lately, to tell how fast it is going. */
  private readonly counted = new Map<string, { at: number; count: number }[]>();
  /** The last pending counts of each queue. */
  private readonly pending = new Map<string, number[]>();

  protected readonly nodeHeight = NODE_HEIGHT;
  protected readonly minutes = signal(60);
  protected readonly map = signal<ParkMap | null>(null);
  protected readonly status = signal<WorkerControlStatus | null>(null);
  /** Where the reverse proxy sends its requests; null while the Worker Control does not say. */
  private readonly upstreams = signal<TrafficUpstreams | null>(null);
  protected readonly problem = signal('');
  protected readonly selectedId = signal<string | null>(null);
  private readonly available = signal(0);
  private readonly rates = signal<ReadonlyMap<string, number>>(new Map());
  private readonly waiting = signal<ReadonlySet<string>>(new Set());

  /** Only the queues something was published in during the period, unless something is wrong with them. */
  protected readonly activeOnly = signal(false);
  /** Queues used by exactly the same applications are drawn as one. */
  protected readonly bundling = signal(true);
  private readonly expanded = signal<ReadonlySet<string>>(new Set());
  protected readonly opened = computed(() => this.expanded().size);
  protected readonly search = signal('');

  private readonly assembled = computed(() => {
    const map = this.map();
    return map
      ? assemble(map, this.status(), this.rates(), this.waiting(), Date.now(), this.upstreams(), { activeOnly: this.activeOnly(), bundle: this.bundling(), expanded: this.expanded() })
      : null;
  });

  /** Queues left out for having had nothing published in them. */
  protected readonly hidden = computed(() => this.assembled()?.hidden ?? 0);

  protected readonly park = computed(() => {
    const assembled = this.assembled();
    if (!assembled || !this.available()) {
      return null;
    }
    // Placing writes on the nodes; each look gets its own.
    return place(assembled.nodes.map((node) => ({ ...node })), assembled.edges.map((edge) => ({ ...edge })), this.available());
  });

  protected readonly selected = computed(() => this.park()?.nodes.find((node) => node.id === this.selectedId()) ?? null);

  /** The selected node and everything joined to it; null when nothing is selected. */
  protected readonly related = computed(() => {
    const selected = this.selected();
    const park = this.park();
    if (!selected || !park) {
      return null;
    }
    const ids = new Set([selected.id]);
    for (const edge of park.edges) {
      if (edge.from === selected.id) {
        ids.add(edge.to);
      } else if (edge.to === selected.id) {
        ids.add(edge.from);
      }
    }
    return ids;
  });

  /** What asks for attention, the worst first. */
  protected readonly attention = computed(() =>
    (this.park()?.nodes ?? [])
      .filter((node) => node.tone === 'danger' || node.tone === 'warn')
      .sort((a, b) => (a.tone === b.tone ? a.label.localeCompare(b.label, 'pt-BR') : a.tone === 'danger' ? -1 : 1)),
  );

  protected readonly totals = computed(() => {
    const map = this.map();
    return {
      applications: map?.applications.length ?? 0,
      instances: map?.applications.reduce((total, application) => total + application.instances.length, 0) ?? 0,
      queues: map?.queues.length ?? 0,
      perMinute: [...this.rates().entries()].filter(([key]) => key.startsWith('publish|')).reduce((total, [, rate]) => total + rate, 0),
    };
  });

  /** The lines of the selected node, for its panel: what it publishes in and what it consumes. */
  protected readonly lines = computed(() => {
    const selected = this.selected();
    const park = this.park();
    if (!selected || !park) {
      return { publish: [], consume: [], supervise: [], http: [] };
    }
    const label = new Map(park.nodes.map((node) => [node.id, node]));
    const other = (edge: ParkEdge) => label.get(edge.from === selected.id ? edge.to : edge.from)!;
    const mine = park.edges.filter((edge) => edge.from === selected.id || edge.to === selected.id);
    const list = (kind: ParkEdge['kind']) =>
      mine
        .filter((edge) => edge.kind === kind)
        .map((edge) => ({ edge, node: other(edge) }))
        .sort((a, b) => a.node.label.localeCompare(b.node.label, 'pt-BR'));
    return { publish: list('publish'), consume: list('consume'), supervise: list('supervise'), http: list('http') };
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 5000);
  }

  ngAfterViewInit(): void {
    const element = this.canvas()?.nativeElement;
    if (element) {
      this.observer = new ResizeObserver(([entry]) => this.available.set(Math.floor(entry.contentRect.width)));
      this.observer.observe(element);
    }
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
    this.observer?.disconnect();
  }

  protected title(column: Column): string {
    return TITLES[column];
  }

  protected icon(node: ParkNode): string {
    return node.application?.protocol === 'v1' ? 'lan' : ICONS[node.kind];
  }

  protected rate(value: number): string {
    return value > 0 ? `${(value >= 100 ? RATE_WHOLE : RATE).format(value)}/min` : '—';
  }

  /** Thicker the more goes through. */
  protected stroke(edge: ParkEdge): number {
    return edge.kind === 'supervise' ? 1 : edge.kind === 'http' ? 1.25 + Math.min(2.75, Math.log10(1 + edge.rate) * 1.2) : 1.25 + Math.min(2.75, Math.log10(1 + edge.rate) * 0.9);
  }

  protected dimmed(id: string): boolean {
    const related = this.related();
    return related !== null && !related.has(id);
  }

  protected edgeDimmed(edge: ParkEdge): boolean {
    const selected = this.selectedId();
    return this.related() !== null && edge.from !== selected && edge.to !== selected;
  }

  /** Draws the queues of a bundle one by one. */
  protected expand(node: ParkNode): void {
    this.expanded.update((expanded) => new Set([...expanded, node.id]));
    this.selectedId.set(null);
  }

  protected collapseAll(): void {
    this.expanded.set(new Set());
  }

  /** Picks what was typed: an application, a queue, or the bundle a queue is in. */
  protected find(text: string): void {
    this.search.set(text);
    const wanted = text.trim().toLowerCase();
    if (!wanted) {
      this.selectedId.set(null);
      return;
    }
    const nodes = this.park()?.nodes ?? [];
    const found =
      nodes.find((node) => node.label.toLowerCase() === wanted) ??
      nodes.find((node) => node.label.toLowerCase().includes(wanted)) ??
      nodes.find((node) => node.bundle?.some((queue) => queue.name.toLowerCase().includes(wanted)));
    this.selectedId.set(found?.id ?? null);
  }

  protected select(node: ParkNode | null): void {
    this.selectedId.set(node && node.id !== this.selectedId() ? node.id : null);
  }

  protected window(minutes: number): void {
    this.minutes.set(minutes);
    this.refresh();
  }

  protected ms(value: number | null): string {
    return value === null ? '—' : value >= 1000 ? `${RATE.format(value / 1000)} s` : `${Math.round(value)} ms`;
  }

  protected share(part: number, whole: number): string {
    return whole ? RATE.format((part / whole) * 100) + '%' : '0%';
  }

  protected up(node: ParkNode): number {
    return node.groups.reduce((total, group) => total + group.Workers.filter((worker) => worker.State === 'Up').length, 0);
  }

  private refresh(): void {
    forkJoin({
      map: this.api.map(this.minutes()),
      // The map is still worth seeing without the Worker Control.
      status: this.api.workerStatus().pipe(catchError(() => of(null))),
      upstreams: this.api.trafficUpstreams(this.minutes()).pipe(catchError(() => of(null))),
    }).subscribe({
      next: ({ map, status, upstreams }) => {
        this.measure(map);
        this.status.set(status);
        this.upstreams.set(upstreams);
        this.map.set(map);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }

  private measure(map: ParkMap): void {
    const now = Date.now();
    const delivered = new Map(map.queues.map((queue) => [queue.name, queue.delivered]));
    const rates = new Map<string, number>();
    const seen = new Set<string>();

    for (const link of map.links) {
      const key = linkKey(link);
      seen.add(key);
      // A connection that only listens has no count of its own: what the queue delivered stands for it.
      const count = link.kind === 'consume' && link.bound ? (delivered.get(link.queue) ?? 0) : link.count;
      const samples = (this.counted.get(key) ?? []).filter((sample) => now - sample.at <= RATE_WINDOW);
      samples.push({ at: now, count });
      this.counted.set(key, samples);
      const first = samples[0];
      const elapsed = now - first.at;
      rates.set(key, elapsed > 0 ? Math.max(0, ((count - first.count) / elapsed) * 60_000) : 0);
    }
    [...this.counted.keys()].filter((key) => !seen.has(key)).forEach((key) => this.counted.delete(key));

    const waiting = new Set<string>();
    const live = new Set<string>();
    for (const queue of map.queues) {
      live.add(queue.name);
      const samples = [...(this.pending.get(queue.name) ?? []), queue.pending].slice(-WAITING_SAMPLES);
      this.pending.set(queue.name, samples);
      if (samples.length === WAITING_SAMPLES && samples.every((value) => value > 0) && samples[samples.length - 1] >= samples[0]) {
        waiting.add(queue.name);
      }
    }
    [...this.pending.keys()].filter((name) => !live.has(name)).forEach((name) => this.pending.delete(name));

    this.rates.set(rates);
    this.waiting.set(waiting);
  }
}
