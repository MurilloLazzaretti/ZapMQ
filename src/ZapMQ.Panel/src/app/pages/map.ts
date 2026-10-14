import { HttpErrorResponse } from '@angular/common/http';
import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { Api } from '../core/api';
import { NumPipe } from '../core/format';
import { PART_HEIGHT, PART_WIDTH, Part, PartKind, Wire, assemble, place, rate } from '../core/landscape';
import { Landscape, ParkMap, TrafficUpstreams, WorkerControlStatus } from '../core/models';

const ICONS: Record<PartKind, string> = {
  web: 'web', proxy: 'dns', api: 'deployed_code', worker: 'memory', service: 'settings_applications',
  client: 'lan', broker: 'stacks', supervisor: 'precision_manufacturing', database: 'database',
};
const KINDS: Record<PartKind, string> = {
  web: 'Aplicação web', proxy: 'Proxy reverso', api: 'API', worker: 'Micro serviço', service: 'Serviço do Windows',
  client: 'Cliente do ZapMQ', broker: 'Mensageria', supervisor: 'Supervisor', database: 'Banco de dados',
};
const SMALLEST = 0.3;
const BIGGEST = 1.6;
/** How often the map is read again, in seconds, and what is offered. Each reading asks the Worker Control to look at the whole machine. */
const EVERY_KEY = 'zapmq.map.every';
const EVERY_DEFAULT = 30;
export const EVERY_CHOICES = [10, 30, 60, 120, 300];
/** On a phone the drawing does not fit readably, so it starts at this size, from its left, and is dragged. */
const NARROW = 600;
const READABLE = 0.75;

/**
 * The environment in one drawing: what comes in on the left, the applications that answer,
 * the processes behind them and the database on the right, each with how it stands now and
 * what it is talking to. The queues are not drawn: each part says which ones it uses.
 */
@Component({
  selector: 'zap-map',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './map.html',
  styleUrl: './map.scss',
})
export class MapPage implements OnInit, AfterViewInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private timer: ReturnType<typeof setInterval> | null = null;
  private observer: ResizeObserver | null = null;
  /** Until somebody moves or zooms, the drawing keeps fitting the screen by itself. */
  private touched = false;
  private drag: { x: number; y: number; left: number; top: number; moved: boolean } | null = null;
  /** The fingers on the screen, to zoom with two. */
  private readonly fingers = new Map<number, { x: number; y: number }>();
  private pinch: number | null = null;

  protected readonly width = PART_WIDTH;
  protected readonly height = PART_HEIGHT;
  protected readonly minutes = signal(60);
  protected readonly everyChoices = EVERY_CHOICES;
  protected readonly every = signal(MapPage.savedEvery());
  /** When the map on the screen was read. */
  protected readonly readAt = signal<Date | null>(null);
  /** A reading is under way: another one waits for it instead of piling up. */
  protected readonly reading = signal(false);
  private readonly map = signal<ParkMap | null>(null);
  private readonly status = signal<WorkerControlStatus | null>(null);
  private readonly land = signal<Landscape | null>(null);
  private readonly traffic = signal<TrafficUpstreams | null>(null);
  protected readonly loaded = signal(false);
  protected readonly problem = signal('');
  /** The Worker Control did not say what there is on the machine: the drawing is only of the messaging. */
  protected readonly partial = signal(false);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly databaseLines = signal(true);
  protected readonly messageLines = signal(true);
  protected readonly zoom = signal({ k: 1, x: 0, y: 0 });
  private readonly size = signal({ width: 0, height: 0 });

  protected readonly drawing = computed(() => (this.loaded() ? place(assemble(this.map(), this.status(), this.land(), this.traffic(), Date.now())) : null));

  protected readonly selected = computed(() => this.drawing()?.parts.find((part) => part.id === this.selectedId()) ?? null);

  /** The selected part and everything joined to it; null when nothing is selected. */
  private readonly related = computed(() => {
    const selected = this.selected();
    const drawing = this.drawing();
    if (!selected || !drawing) {
      return null;
    }
    const ids = new Set([selected.id]);
    for (const wire of drawing.wires) {
      if (wire.from === selected.id) {
        ids.add(wire.to);
      } else if (wire.to === selected.id) {
        ids.add(wire.from);
      }
    }
    return ids;
  });

  /** What was typed, found in the names of the parts and of the queues they use. */
  protected readonly found = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    const parts = this.drawing()?.parts ?? [];
    if (wanted.length < 2) {
      return null;
    }
    const ids = new Set<string>();
    const queues = new Map<string, { name: string; consumes: Part[]; publishes: Part[] }>();
    for (const part of parts) {
      if (part.label.toLowerCase().includes(wanted) || part.id.toLowerCase().includes(wanted)) {
        ids.add(part.id);
      }
      for (const [list, side] of [[part.consumes, 'consumes'], [part.publishes, 'publishes']] as const) {
        for (const queue of list) {
          if (queue.name.toLowerCase().includes(wanted)) {
            ids.add(part.id);
            const entry = queues.get(queue.name) ?? queues.set(queue.name, { name: queue.name, consumes: [], publishes: [] }).get(queue.name)!;
            entry[side].push(part);
          }
        }
      }
    }
    return { ids, queues: [...queues.values()].sort((a, b) => a.name.localeCompare(b.name, 'pt-BR')), named: parts.filter((part) => part.label.toLowerCase().includes(wanted)) };
  });

  /** What asks for attention, the worst first. */
  protected readonly attention = computed(() =>
    (this.drawing()?.parts ?? [])
      .filter((part) => part.tone === 'danger' || part.tone === 'warn')
      .sort((a, b) => (a.tone === b.tone ? a.label.localeCompare(b.label, 'pt-BR') : a.tone === 'danger' ? -1 : 1)),
  );

  protected readonly totals = computed(() => {
    const parts = this.drawing()?.parts ?? [];
    const of = (...kinds: PartKind[]) => parts.filter((part) => kinds.includes(part.kind));
    const up = (list: Part[]) => list.filter((part) => part.tone === 'ok').length;
    return {
      apis: of('api').length, apisUp: up(of('api')),
      workers: of('worker').length, workersUp: up(of('worker')),
      services: of('service').length, servicesUp: up(of('service')),
      onDatabase: parts.filter((part) => part.database).length,
      usingDatabase: parts.filter((part) => part.database?.active).length,
      database: parts.find((part) => part.kind === 'database') ?? null,
    };
  });

  /** What joins the selected part to the others, in words, for its panel. */
  protected readonly talks = computed(() => {
    const selected = this.selected();
    const drawing = this.drawing();
    if (!selected || !drawing) {
      return [];
    }
    const name = new Map(drawing.parts.map((part) => [part.id, part]));
    return drawing.wires
      .filter((wire) => wire.kind !== 'database' && (wire.from === selected.id || wire.to === selected.id))
      .map((wire) => ({ wire, other: name.get(wire.from === selected.id ? wire.to : wire.from)!, out: wire.from === selected.id }))
      .sort((a, b) => a.other.label.localeCompare(b.other.label, 'pt-BR'));
  });

  constructor() {
    // The first time there is something to draw, and whenever the space changes while nobody moved it.
    effect(() => {
      const drawing = this.drawing();
      const size = this.size();
      if (drawing && size.width && !this.touched) {
        untracked(() => this.fit(false));
      }
    });
  }

  ngOnInit(): void {
    this.refresh();
    this.schedule();
  }

  /** Reads again every so many seconds, as chosen on the screen and remembered by this browser. */
  protected setEvery(seconds: number): void {
    this.every.set(seconds);
    try {
      localStorage.setItem(EVERY_KEY, String(seconds));
    } catch {
      // Without storage it is only for this visit.
    }
    this.schedule();
  }

  protected refreshNow(): void {
    this.refresh();
    this.schedule();
  }

  private schedule(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
    this.timer = setInterval(() => this.refresh(), this.every() * 1000);
  }

  private static savedEvery(): number {
    try {
      const saved = Number(localStorage.getItem(EVERY_KEY));
      return EVERY_CHOICES.includes(saved) ? saved : EVERY_DEFAULT;
    } catch {
      return EVERY_DEFAULT;
    }
  }

  protected everyLabel(seconds: number): string {
    return seconds < 60 ? `${seconds} s` : `${seconds / 60} min`;
  }

  ngAfterViewInit(): void {
    const element = this.viewport()?.nativeElement;
    if (element) {
      this.observer = new ResizeObserver(([entry]) => this.size.set({ width: Math.floor(entry.contentRect.width), height: Math.floor(entry.contentRect.height) }));
      this.observer.observe(element);
    }
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
    this.observer?.disconnect();
  }

  protected icon(part: Part): string {
    return ICONS[part.kind];
  }

  protected kind(part: Part): string {
    return KINDS[part.kind];
  }

  protected rate(perMinute: number): string {
    return rate(perMinute);
  }

  /** The line of a part about its queues: the names while they fit, how many when they do not. */
  protected queuesLine(part: Part): string {
    const wanted = this.search().trim().toLowerCase();
    // What was searched for comes first, so that it is seen on the part itself.
    const pick = (list: Part['consumes']) => (wanted.length >= 2 ? [...list].sort((a, b) => Number(b.name.toLowerCase().includes(wanted)) - Number(a.name.toLowerCase().includes(wanted))) : list);
    const names = (list: Part['consumes']) => (list.length <= 2 ? pick(list).map((queue) => queue.name).join(', ') : `${pick(list)[0].name} +${list.length - 1}`);
    return [part.consumes.length ? `↓ ${names(part.consumes)}` : '', part.publishes.length ? `↑ ${part.publishes.length <= 1 ? names(part.publishes) : part.publishes.length + ' filas'}` : ''].filter(Boolean).join('   ');
  }

  /** Everything about a part in a few lines, for whoever only points at it. */
  protected hint(part: Part): string {
    return [
      `${KINDS[part.kind]} · ${part.detail}`,
      part.protocols.length ? `ZapMQ: ${this.protocolWords(part)}` : '',
      ...part.reasons,
      part.consumes.length ? `Consome: ${part.consumes.map((queue) => queue.name).join(', ')}` : '',
      part.publishes.length ? `Publica em: ${part.publishes.map((queue) => queue.name).join(', ')}` : '',
      part.database ? `Banco: ${part.database.sessions} conexões${part.database.active ? ', em uso' : ', sem uso agora'}` : '',
      part.http?.count ? `HTTP: ${rate(part.http.perMinute)}${part.http.errors ? `, ${part.http.errors} com erro` : ''}` : '',
    ].filter(Boolean).join('\n');
  }

  /** How a part talks to the broker, in words. */
  protected protocolWords(part: Part): string {
    return part.protocols.length > 1 ? 'tem programas no protocolo 1.x e no 2.x' : part.protocols[0] === 'v1' ? 'protocolo 1.x (wrapper antigo)' : 'protocolo 2.x';
  }

  protected idle(seconds: number | null): string {
    return seconds === null ? 'não se sabe' : seconds < 60 ? `há ${seconds} s` : seconds < 3600 ? `há ${Math.round(seconds / 60)} min` : `há ${Math.round(seconds / 3600)} h`;
  }

  protected dimmed(part: Part): boolean {
    const found = this.found();
    if (found) {
      return !found.ids.has(part.id);
    }
    const related = this.related();
    return related !== null && !related.has(part.id);
  }

  protected shown(wire: Wire): boolean {
    const selected = this.selectedId();
    if (selected && (wire.from === selected || wire.to === selected)) {
      return true;
    }
    return wire.kind === 'database' ? this.databaseLines() : wire.kind === 'message' ? this.messageLines() : true;
  }

  protected wireDimmed(wire: Wire): boolean {
    const selected = this.selectedId();
    const found = this.found();
    if (selected) {
      return wire.from !== selected && wire.to !== selected;
    }
    return !!found && !(found.ids.has(wire.from) && found.ids.has(wire.to));
  }

  protected select(part: Part | null): void {
    if (this.drag?.moved) {
      return;
    }
    this.selectedId.set(part && part.id !== this.selectedId() ? part.id : null);
  }

  /** Brings a part to the middle of what is seen, and picks it. */
  protected go(part: Part): void {
    const size = this.size();
    const k = Math.max(this.zoom().k, 0.8);
    this.touched = true;
    this.zoom.set({ k, x: size.width / 2 - (part.x + PART_WIDTH / 2) * k, y: size.height / 2 - (part.y + PART_HEIGHT / 2) * k });
    this.selectedId.set(part.id);
  }

  protected window(minutes: number): void {
    this.minutes.set(minutes);
    this.refresh();
  }

  /** The whole drawing in what there is of screen, never bigger than it is. */
  protected fit(byHand = true): void {
    const drawing = this.drawing();
    const size = this.size();
    if (!drawing || !size.width || !size.height) {
      return;
    }
    if (byHand) {
      this.touched = false;
    }
    // By itself it fits the width, which keeps the names readable, and what is below is a drag away; asked for, it shows everything.
    const wide = (size.width - 16) / drawing.width;
    if (!byHand && size.width < NARROW && wide < READABLE) {
      this.zoom.set({ k: READABLE, x: 8, y: 8 });
      return;
    }
    const k = Math.max(SMALLEST, Math.min(1, byHand ? Math.min(wide, (size.height - 16) / drawing.height) : wide));
    this.zoom.set({ k, x: Math.max(8, (size.width - drawing.width * k) / 2), y: Math.max(8, (size.height - drawing.height * k) / 2) });
  }

  /** Closer or farther, keeping still the point that is at the middle (or under the pointer). */
  protected scale(factor: number, at?: { x: number; y: number }): void {
    const now = this.zoom();
    const size = this.size();
    const point = at ?? { x: size.width / 2, y: size.height / 2 };
    const k = Math.max(SMALLEST, Math.min(BIGGEST, now.k * factor));
    this.touched = true;
    this.zoom.set({ k, x: point.x - ((point.x - now.x) / now.k) * k, y: point.y - ((point.y - now.y) / now.k) * k });
  }

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    if (event.ctrlKey || event.metaKey) {
      this.scale(Math.exp(-event.deltaY * 0.0022), { x: event.clientX - box.left, y: event.clientY - box.top });
      return;
    }
    this.touched = true;
    this.zoom.update((now) => ({ ...now, x: now.x - event.deltaX, y: now.y - event.deltaY }));
  }

  protected press(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    if (event.pointerType === 'touch') {
      this.fingers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (this.fingers.size === 2) {
        this.pinch = this.spread();
        this.drag = null;
        return;
      }
    }
    const now = this.zoom();
    this.drag = { x: event.clientX, y: event.clientY, left: now.x, top: now.y, moved: false };
  }

  protected move(event: PointerEvent): void {
    if (this.fingers.has(event.pointerId)) {
      this.fingers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (this.pinch !== null && this.fingers.size === 2) {
        const spread = this.spread();
        const [a, b] = [...this.fingers.values()];
        const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
        this.scale(spread / this.pinch, { x: (a.x + b.x) / 2 - box.left, y: (a.y + b.y) / 2 - box.top });
        this.pinch = spread;
        return;
      }
    }
    const drag = this.drag;
    if (!drag || !(event.buttons & 1)) {
      return;
    }
    const dx = event.clientX - drag.x;
    const dy = event.clientY - drag.y;
    if (!drag.moved && Math.abs(dx) + Math.abs(dy) < 5) {
      return;
    }
    drag.moved = true;
    this.touched = true;
    this.zoom.update((now) => ({ ...now, x: drag.left + dx, y: drag.top + dy }));
  }

  /** How far apart the two fingers are. */
  private spread(): number {
    const [a, b] = [...this.fingers.values()];
    return Math.hypot(a.x - b.x, a.y - b.y) || 1;
  }

  protected release(event?: PointerEvent): void {
    if (event) {
      this.fingers.delete(event.pointerId);
      if (this.fingers.size < 2) {
        this.pinch = null;
      }
    }
    // After the click that ends a drag has been thrown away.
    setTimeout(() => (this.drag = null));
  }

  protected percent(): number {
    return Math.round(this.zoom().k * 100);
  }

  private refresh(): void {
    if (this.reading()) {
      return;
    }
    this.reading.set(true);
    forkJoin({
      map: this.api.map(this.minutes()).pipe(catchError((failure: HttpErrorResponse) => of(failure))),
      // The drawing is still worth seeing without the Worker Control: it says so, and shows the rest.
      status: this.api.workerStatus().pipe(catchError(() => of(null))),
      land: this.api.landscape().pipe(catchError(() => of(null))),
      traffic: this.api.trafficUpstreams(this.minutes()).pipe(catchError(() => of(null))),
    }).subscribe(({ map, status, land, traffic }) => {
      if (map instanceof HttpErrorResponse) {
        if (map.status !== 401) {
          this.problem.set(map.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
        this.map.set(null);
      } else {
        this.map.set(map);
        this.problem.set('');
      }
      this.status.set(status);
      this.land.set(land);
      this.traffic.set(traffic);
      this.partial.set(!!status && !land);
      this.loaded.set(true);
      this.readAt.set(new Date());
      this.reading.set(false);
    });
  }
}
