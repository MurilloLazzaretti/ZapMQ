import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { NumPipe } from '../core/format';
import { TraceLine, TraceState } from '../core/models';

type Phase = TraceState['state'] | 'offline';

const STATES: Record<Phase, { label: string; tone: string }> = {
  starting: { label: 'pedindo o trace', tone: 'info' },
  on: { label: 'ao vivo', tone: 'ok' },
  unsupported: { label: 'sem suporte', tone: 'warn' },
  unreachable: { label: 'sem resposta', tone: 'danger' },
  ended: { label: 'processo saiu', tone: '' },
  offline: { label: 'reconectando', tone: 'warn' },
};

/** A line on screen: which process wrote it, and when, as a number to sort by. */
interface Row extends TraceLine {
  key: string;
  pid: number;
  moment: number;
}

interface Process {
  pid: number;
  /** Which of the colours tells this process from the others. */
  colour: number;
  state: Phase;
  message: string | null;
}

/** Lines kept in the browser; the oldest go first. */
const KEPT = 4000;
const COLOURS = 8;
const CLOCK = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });

/**
 * What one process, or every process of a group, writes with Trace(), as it is written. Being
 * on this screen is what turns the trace on; leaving turns it off.
 *
 * A group is followed as a whole because there is no telling which of its processes will take
 * a message: the lines of all of them come together, in the order they were written.
 */
@Component({
  selector: 'zap-trace',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './trace.html',
  styleUrl: './trace.scss',
})
export class TracePage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly document = inject(DOCUMENT);
  private readonly output = viewChild<ElementRef<HTMLElement>>('output');
  private source: EventSource | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  private poll: ReturnType<typeof setInterval> | null = null;
  /** The last line taken from each process, so that what comes again is not shown twice. */
  private readonly taken = new Map<number, number>();
  private readonly colours = new Map<number, number>();

  /** From the route, when one process is followed. */
  readonly pid = input<string>();
  /** The group: from the route when the whole group is followed, else only to say whose the process is. */
  readonly grupo = input<string>();

  protected readonly whole = computed(() => !this.pid());
  protected readonly processes = signal<Process[]>([]);
  protected readonly lines = signal<Row[]>([]);
  /** Processes left out of the screen for now. */
  protected readonly hidden = signal<ReadonlySet<number>>(new Set());
  /** Why there is nothing to follow, when that is the case. */
  protected readonly problem = signal('');
  private readonly offline = signal(false);
  /** What was on screen when the pause began; null while not paused. */
  private readonly frozen = signal<Row[] | null>(null);
  protected readonly paused = computed(() => this.frozen() !== null);
  protected readonly follow = signal(true);
  protected readonly wrap = signal(true);
  protected readonly search = signal('');

  /** One word for the whole screen. */
  protected readonly label = computed(() => {
    const processes = this.processes();
    if (this.offline()) {
      return STATES.offline;
    }
    if (processes.length === 1) {
      return STATES[processes[0].state];
    }
    const on = processes.filter((process) => process.state === 'on').length;
    if (!processes.length) {
      return { label: 'sem processos', tone: '' };
    }
    return { label: `${on} de ${processes.length} ao vivo`, tone: on === processes.length ? 'ok' : on ? 'warn' : 'danger' };
  });

  /** What has to be said about the processes that are not simply on, each thing once. */
  protected readonly notices = computed(() => {
    const said = new Map<string, { message: string; state: Phase; pids: number[] }>();
    for (const process of this.processes()) {
      if (process.message) {
        const notice = said.get(process.message) ?? { message: process.message, state: process.state, pids: [] };
        notice.pids.push(process.pid);
        said.set(process.message, notice);
      }
    }
    return [...said.values()];
  });

  protected readonly visible = computed(() => {
    const hidden = this.hidden();
    const wanted = this.search().trim().toLowerCase();
    let lines = this.frozen() ?? this.lines();
    if (hidden.size) {
      lines = lines.filter((line) => !hidden.has(line.pid));
    }
    return wanted ? lines.filter((line) => line.text.toLowerCase().includes(wanted)) : lines;
  });

  /** Lines that arrived during the pause. */
  protected readonly waiting = computed(() => {
    const frozen = this.frozen();
    return frozen ? Math.max(0, this.lines().length - frozen.length) : 0;
  });

  constructor() {
    // Keeps the end in sight as lines arrive, unless the reader went up to look at something.
    effect(() => {
      this.visible();
      if (this.follow()) {
        requestAnimationFrame(() => this.toEnd());
      }
    });
  }

  ngOnInit(): void {
    const pid = Number(this.pid());
    if (!this.whole()) {
      this.watch([pid]);
      return;
    }
    // The processes of a group change: one is replaced, another is added. Whoever is there is followed.
    this.findProcesses();
    this.poll = setInterval(() => this.findProcesses(), 5000);
  }

  ngOnDestroy(): void {
    if (this.poll) {
      clearInterval(this.poll);
    }
    this.close();
  }

  protected state(process: { state: Phase }) {
    return STATES[process.state];
  }

  protected clock(line: Row): string {
    return CLOCK.format(line.moment);
  }

  protected colour(pid: number): number {
    return this.colours.get(pid) ?? 0;
  }

  protected toggle(pid: number): void {
    const hidden = new Set(this.hidden());
    if (!hidden.delete(pid)) {
      hidden.add(pid);
    }
    this.hidden.set(hidden);
  }

  protected scrolled(): void {
    const element = this.output()?.nativeElement;
    if (element) {
      this.follow.set(element.scrollHeight - element.scrollTop - element.clientHeight < 24);
    }
  }

  protected toEnd(): void {
    const element = this.output()?.nativeElement;
    if (element) {
      element.scrollTop = element.scrollHeight;
    }
  }

  protected resume(): void {
    this.follow.set(true);
    this.toEnd();
  }

  protected togglePause(): void {
    this.frozen.set(this.paused() ? null : this.lines());
  }

  protected clear(): void {
    this.lines.set([]);
    if (this.paused()) {
      this.frozen.set([]);
    }
  }

  /** What is on screen, filters included, as a text file. */
  protected export(): void {
    const whole = this.whole();
    const text = this.visible()
      .map((line) =>
        line.dropped
          ? `--- ${whole ? line.pid + ': ' : ''}${line.dropped} linhas descartadas pelo processo ---`
          : `${this.clock(line)}  ${whole ? String(line.pid).padEnd(7) : ''}${line.text}`,
      )
      .join('\n');
    const link = this.document.createElement('a');
    link.href = URL.createObjectURL(new Blob([text + '\n'], { type: 'text/plain;charset=utf-8' }));
    link.download = `trace-${(whole ? this.grupo() : this.pid())?.replace(/[^\w.-]+/g, '_')}-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.txt`;
    link.click();
    URL.revokeObjectURL(link.href);
  }

  private findProcesses(): void {
    this.api.workerStatus().subscribe({
      next: (status) => {
        const group = status.Groups.find((item) => item.Name === this.grupo());
        if (!group) {
          this.problem.set('O Worker Control não tem um grupo com esse nome.');
          return;
        }
        this.problem.set(group.Workers.length ? '' : 'O grupo não tem nenhum processo rodando agora.');
        const pids = group.Workers.map((worker) => worker.ProcessId).sort((a, b) => a - b);
        const current = this.processes().map((process) => process.pid);
        if (pids.length !== current.length || pids.some((pid, index) => pid !== current[index])) {
          this.watch(pids);
        }
      },
      error: (failure) => failure.status !== 401 && !this.processes().length && this.problem.set(failure.error?.error ?? 'O Worker Control não está respondendo.'),
    });
  }

  /** Follows these processes from now on. Lines already here stay, even of a process that is no longer followed. */
  private watch(pids: number[]): void {
    this.close();
    const known = new Map(this.processes().map((process) => [process.pid, process]));
    for (const pid of pids) {
      if (!this.colours.has(pid)) {
        this.colours.set(pid, this.colours.size % COLOURS);
      }
    }
    this.processes.set(pids.map((pid) => known.get(pid) ?? { pid, colour: this.colours.get(pid)!, state: 'starting', message: null }));
    if (pids.length) {
      this.open(pids);
    }
  }

  private open(pids: number[]): void {
    // Relative to <base href>, like everything else.
    const source = new EventSource(new URL(`api/trace?pids=${pids.join(',')}`, this.document.baseURI).toString(), { withCredentials: true });
    this.source = source;

    source.addEventListener('state', (event) => {
      const state = JSON.parse((event as MessageEvent).data) as TraceState & { pid: number };
      this.offline.set(false);
      this.processes.update((processes) => processes.map((process) => (process.pid === state.pid ? { ...process, state: state.state, message: state.message } : process)));
    });

    source.addEventListener('lines', (event) => {
      const batch = JSON.parse((event as MessageEvent).data) as { pid: number; lines: TraceLine[] };
      const last = this.taken.get(batch.pid) ?? 0;
      const fresh = batch.lines.filter((line) => line.n > last);
      if (!fresh.length) {
        return;
      }
      this.taken.set(batch.pid, fresh[fresh.length - 1].n);
      const rows = fresh.map<Row>((line) => ({ ...line, pid: batch.pid, key: `${batch.pid}:${line.n}`, moment: Date.parse(line.at) }));
      const current = this.lines();
      const merged = [...current, ...rows];
      // Each process sends in its own time; on screen the lines go in the order they were written.
      if (current.length && rows[0].moment < current[current.length - 1].moment) {
        merged.sort((a, b) => a.moment - b.moment || a.pid - b.pid || a.n - b.n);
      }
      this.lines.set(merged.slice(-KEPT));
    });

    source.onerror = () => {
      this.offline.set(true);
      // The browser retries while the connection is only interrupted; once it gives up, start
      // over. The service may be another one by then, counting its lines from the start.
      if (source.readyState === EventSource.CLOSED) {
        this.close();
        this.taken.clear();
        this.retry = setTimeout(() => this.open(pids), 3000);
      }
    };
  }

  private close(): void {
    if (this.retry) {
      clearTimeout(this.retry);
      this.retry = null;
    }
    this.source?.close();
    this.source = null;
  }
}
