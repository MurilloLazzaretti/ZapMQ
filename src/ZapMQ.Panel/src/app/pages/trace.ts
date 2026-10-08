import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { NumPipe } from '../core/format';
import { TraceLine, TraceState } from '../core/models';

const STATES: Record<TraceState['state'] | 'offline', { label: string; tone: string }> = {
  starting: { label: 'pedindo o trace', tone: 'info' },
  on: { label: 'ao vivo', tone: 'ok' },
  unsupported: { label: 'sem suporte', tone: 'warn' },
  unreachable: { label: 'sem resposta', tone: 'danger' },
  ended: { label: 'processo saiu', tone: '' },
  offline: { label: 'reconectando', tone: 'warn' },
};

/** Lines kept in the browser; the oldest go first. */
const KEPT = 3000;
const CLOCK = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });

/**
 * What one process writes with Trace(), as it writes. Being on this screen is what turns the
 * trace on; leaving turns it off.
 */
@Component({
  selector: 'zap-trace',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './trace.html',
  styleUrl: './trace.scss',
})
export class TracePage implements OnInit, OnDestroy {
  private readonly document = inject(DOCUMENT);
  private readonly output = viewChild<ElementRef<HTMLElement>>('output');
  private source: EventSource | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;

  /** From the route. */
  readonly pid = input.required<string>();
  /** From the query string, only to say which group the process is of. */
  readonly grupo = input<string>();

  protected readonly state = signal<TraceState['state'] | 'offline'>('starting');
  protected readonly message = signal<string | null>(null);
  protected readonly lines = signal<TraceLine[]>([]);
  /** What was on screen when the pause began; null while not paused. */
  private readonly frozen = signal<TraceLine[] | null>(null);
  protected readonly paused = computed(() => this.frozen() !== null);
  protected readonly follow = signal(true);
  protected readonly wrap = signal(true);
  protected readonly search = signal('');

  protected readonly label = computed(() => STATES[this.state()]);

  protected readonly visible = computed(() => {
    const lines = this.frozen() ?? this.lines();
    const wanted = this.search().trim().toLowerCase();
    return wanted ? lines.filter((line) => line.text.toLowerCase().includes(wanted)) : lines;
  });

  /** Lines that arrived during the pause. */
  protected readonly waiting = computed(() => {
    const frozen = this.frozen();
    if (!frozen) {
      return 0;
    }
    const last = frozen.length ? frozen[frozen.length - 1].n : 0;
    return this.lines().filter((line) => line.n > last).length;
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
    this.open();
  }

  ngOnDestroy(): void {
    this.close();
  }

  protected clock(line: TraceLine): string {
    return CLOCK.format(new Date(line.at));
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

  /** What is on screen, filter included, as a text file. */
  protected export(): void {
    const text = this.visible()
      .map((line) => (line.dropped ? `--- ${line.dropped} linhas descartadas pelo processo ---` : `${this.clock(line)}  ${line.text}`))
      .join('\n');
    const link = this.document.createElement('a');
    link.href = URL.createObjectURL(new Blob([text + '\n'], { type: 'text/plain;charset=utf-8' }));
    link.download = `trace-${this.pid()}-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.txt`;
    link.click();
    URL.revokeObjectURL(link.href);
  }

  private open(): void {
    // Relative to <base href>, like everything else.
    const source = new EventSource(new URL(`api/trace/${encodeURIComponent(this.pid())}`, this.document.baseURI).toString(), { withCredentials: true });
    this.source = source;
    let fresh = true;

    source.addEventListener('state', (event) => {
      const state = JSON.parse((event as MessageEvent).data) as TraceState;
      this.state.set(state.state);
      this.message.set(state.message);
    });

    source.addEventListener('lines', (event) => {
      const arrived = JSON.parse((event as MessageEvent).data) as TraceLine[];
      // Each connection starts with what the service kept from before, which replaces what is here.
      const base = fresh ? [] : this.lines();
      fresh = false;
      const last = base.length ? base[base.length - 1].n : 0;
      this.lines.set([...base, ...arrived.filter((line) => line.n > last)].slice(-KEPT));
    });

    source.onopen = () => {
      fresh = true;
    };

    source.onerror = () => {
      this.state.set('offline');
      this.message.set(null);
      // The browser retries while the connection is only interrupted; once it gives up, start over.
      if (source.readyState === EventSource.CLOSED) {
        this.close();
        this.retry = setTimeout(() => this.open(), 3000);
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
