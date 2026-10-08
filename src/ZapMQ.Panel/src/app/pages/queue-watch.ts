import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { NumPipe } from '../core/format';
import { TapEvent } from '../core/models';
import { showJson } from '../shared/dialogs';

/** A message as it is being followed: the steps seen of it, put together. */
interface Followed {
  id: string;
  at: number;
  body: string | null;
  truncated: boolean;
  rpc: boolean;
  publisher: string | null;
  consumer: string | null;
  response: string | null;
  reason: string | null;
  state: 'published' | 'delivered' | 'confirmed' | 'responded' | 'dead';
  /** From being published to being confirmed or answered, when both were seen. */
  took: number | null;
}

const STATES: Record<Followed['state'], { label: string; tone: string }> = {
  published: { label: 'esperando', tone: 'warn' },
  delivered: { label: 'entregue', tone: 'info' },
  confirmed: { label: 'confirmada', tone: 'ok' },
  responded: { label: 'respondida', tone: 'ok' },
  dead: { label: 'morta', tone: 'danger' },
};

const REASONS: Record<string, string> = { Expired: 'venceu', NotConsumed: 'ninguém consumiu', Unconfirmed: 'não foi confirmada' };
const ORDER = ['published', 'delivered', 'confirmed', 'responded', 'dead'];
const KEPT = 500;
const CLOCK = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });

/**
 * The messages going through one queue, as they go: who published each one, who got it, what
 * became of it, and its content. Looking takes nothing from the queue. A queue only tells
 * what goes through it while somebody is here, unless it is set up to keep its last messages.
 */
@Component({
  selector: 'zap-queue-watch',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './queue-watch.html',
  styleUrl: './queue-watch.scss',
})
export class QueueWatchPage implements OnInit, OnDestroy {
  private readonly document = inject(DOCUMENT);
  private readonly dialog = inject(MatDialog);
  private source: EventSource | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  private readonly byId = new Map<string, Followed>();

  /** From the route. */
  readonly name = input.required<string>();

  protected readonly connected = signal(false);
  protected readonly messages = signal<Followed[]>([]);
  private readonly frozen = signal<Followed[] | null>(null);
  protected readonly paused = computed(() => this.frozen() !== null);
  protected readonly search = signal('');
  protected readonly seen = signal(0);

  /** The newest first. */
  protected readonly visible = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    const messages = this.frozen() ?? this.messages();
    return wanted
      ? messages.filter((message) => `${message.body ?? ''} ${message.response ?? ''} ${message.publisher ?? ''} ${message.consumer ?? ''} ${message.id}`.toLowerCase().includes(wanted))
      : messages;
  });

  protected readonly waiting = computed(() => (this.frozen() ? Math.max(0, this.seen() - this.seenAtPause) : 0));
  private seenAtPause = 0;

  ngOnInit(): void {
    this.open();
  }

  ngOnDestroy(): void {
    this.close();
  }

  protected state(message: Followed) {
    return STATES[message.state];
  }

  protected reason(message: Followed): string {
    return REASONS[message.reason ?? ''] ?? message.reason ?? '';
  }

  protected clock(at: number): string {
    return CLOCK.format(at);
  }

  protected took(message: Followed): string {
    return message.took === null ? '' : message.took >= 1000 ? `${(message.took / 1000).toFixed(1).replace('.', ',')} s` : `${Math.round(message.took)} ms`;
  }

  protected preview(message: Followed): string {
    const text = (message.body ?? '').replace(/\s+/g, ' ');
    return text.length > 200 ? text.slice(0, 200) + '…' : text;
  }

  protected show(message: Followed): void {
    const parse = (text: string | null) => {
      try {
        return text === null ? null : JSON.parse(text);
      } catch {
        // Cut short, or not JSON at all: shown as it came.
        return text;
      }
    };
    showJson(this.dialog, {
      title: message.truncated ? 'Mensagem (conteúdo cortado: era grande demais)' : 'Mensagem',
      subtitle: `${message.id} · ${this.clock(message.at)}${message.publisher ? ' · de ' + message.publisher : ''}`,
      json: parse(message.body),
      // A body that was cut cannot be published again as it was.
      queue: message.truncated ? undefined : this.name(),
      more: message.response === null ? undefined : { title: 'Resposta', json: parse(message.response) },
    });
  }

  protected publish(): void {
    void import('../shared/publish').then((module) => module.openPublish(this.dialog, { queue: this.name() }));
  }

  protected togglePause(): void {
    this.seenAtPause = this.seen();
    this.frozen.set(this.paused() ? null : this.messages());
  }

  protected clear(): void {
    this.byId.clear();
    this.messages.set([]);
    if (this.paused()) {
      this.frozen.set([]);
    }
  }

  private open(): void {
    // Relative to <base href>, like everything else.
    const source = new EventSource(new URL(`api/queues/${encodeURIComponent(this.name())}/watch`, this.document.baseURI).toString(), { withCredentials: true });
    this.source = source;
    source.onopen = () => this.connected.set(true);
    source.onmessage = (message) => this.take(JSON.parse(message.data) as TapEvent[]);
    source.onerror = () => {
      this.connected.set(false);
      if (source.readyState === EventSource.CLOSED) {
        this.close();
        this.retry = setTimeout(() => this.open(), 3000);
      }
    };
  }

  /** Puts the steps that arrived into the messages they are of. */
  private take(steps: TapEvent[]): void {
    for (const step of steps) {
      const at = Date.parse(step.at);
      let message = this.byId.get(step.id);
      if (!message) {
        // A step of a message published before the watch began is still worth showing.
        message = { id: step.id, at, body: null, truncated: false, rpc: false, publisher: null, consumer: null, response: null, reason: null, state: step.kind, took: null };
        this.byId.set(step.id, message);
        this.seen.update((count) => count + 1);
      } else {
        // Each change is a new object, so the screen sees it.
        message = { ...message };
        this.byId.set(step.id, message);
      }
      if (ORDER.indexOf(step.kind) >= ORDER.indexOf(message.state)) {
        message.state = step.kind;
      }
      if (step.kind === 'published') {
        message.at = at;
        message.body = step.body ?? null;
        message.rpc = step.rpc ?? false;
        message.publisher = step.who ?? null;
      } else if (step.kind === 'delivered') {
        message.consumer = step.who ?? message.consumer;
      } else if (step.kind === 'responded') {
        message.response = step.response ?? null;
        message.consumer = step.who ?? message.consumer;
      } else if (step.kind === 'confirmed') {
        message.consumer = step.who ?? message.consumer;
      } else if (step.kind === 'dead') {
        message.reason = step.reason ?? null;
      }
      if ((step.kind === 'confirmed' || step.kind === 'responded') && message.body !== null) {
        message.took = Math.max(0, at - message.at);
      }
      message.truncated ||= step.truncated ?? false;
    }

    const all = [...this.byId.values()].sort((a, b) => b.at - a.at);
    for (const old of all.slice(KEPT)) {
      this.byId.delete(old.id);
    }
    this.messages.set(all.slice(0, KEPT));
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
