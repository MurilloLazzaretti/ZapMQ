import { QueueTabs } from '../shared/queue-tabs';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { NumPipe, WhenPipe } from '../core/format';
import { DeadLetter, DeadReason, DeadSummary } from '../core/models';
import { confirm, showJson } from '../shared/dialogs';

const REASONS: Record<DeadReason, { label: string; tone: string; help: string }> = {
  unconfirmed: { label: 'não confirmada', tone: 'danger', help: 'Foi entregue e o consumidor caiu antes de confirmar' },
  expired: { label: 'vencida', tone: 'warn', help: 'A validade (TTL) acabou antes de alguém consumir' },
  'not-consumed': { label: 'não consumida', tone: 'warn', help: 'Ninguém consumiu dentro da retenção da fila' },
};

/** What was not delivered or not confirmed, queue by queue, with what can be done about it. */
@Component({
  selector: 'zap-dead-letters',
  imports: [QueueTabs, RouterLink, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, NumPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Mensagens mortas</h1>
          <p class="muted">O que deixou de ser entregue ou confirmado. Nada aqui volta para a fila sozinho.</p>
        </div>
        <span class="spacer"></span>
        <zap-queue-tabs />
      </header>

      @if (!summary().length) {
        <section class="panel empty">
          <mat-icon svgIcon="verified" />
          <strong>Nenhuma mensagem morta</strong>
          <span>Tudo o que foi publicado foi entregue e confirmado, ou ainda está dentro do prazo.</span>
        </section>
      } @else {
        <div class="layout">
          <section class="panel queues">
            <div class="panel-head"><h2>Filas</h2></div>
            <ul>
              @for (item of summary(); track item.queue) {
                <li>
                  <button type="button" [class.active]="item.queue === selected()" (click)="pick(item.queue)">
                    <span class="truncate name">{{ item.queue }}</span>
                    <span class="counts">
                      @if (item.unconfirmed) {
                        <span class="pill danger" matTooltip="Não confirmadas">{{ item.unconfirmed | num }}</span>
                      }
                      @if (item.expired) {
                        <span class="pill warn" matTooltip="Vencidas">{{ item.expired | num }}</span>
                      }
                      @if (item.notConsumed) {
                        <span class="pill warn" matTooltip="Não consumidas">{{ item.notConsumed | num }}</span>
                      }
                    </span>
                  </button>
                </li>
              }
            </ul>
          </section>

          <section class="panel letters">
            <div class="panel-head">
              <h2 class="truncate">{{ selected() || 'Escolha uma fila' }}</h2>
              <span class="spacer"></span>
              @if (selected()) {
                <a mat-button [routerLink]="['/filas', selected()]">Ver a fila</a>
                <button mat-stroked-button class="danger" (click)="discardAll()" [disabled]="!letters().length">
                  <mat-icon svgIcon="delete" /> Descartar todas
                </button>
              }
            </div>

            @if (letters().length) {
              <div class="table-wrap">
                <table class="data stack">
                  <thead>
                    <tr>
                      <th>Morreu em</th>
                      <th>Motivo</th>
                      <th>Quem tinha recebido</th>
                      <th>Conteúdo</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (letter of letters(); track letter.id) {
                      <tr>
                        <td class="nowrap num" data-label="Morreu em">{{ letter.diedAt | when }}</td>
                        <td data-label="Motivo">
                          <span class="pill" [class]="reason(letter).tone" [matTooltip]="reason(letter).help">{{ reason(letter).label }}</span>
                          @if (letter.rpc) {
                            <span class="pill info">RPC</span>
                          }
                        </td>
                        <td class="wide" data-label="Quem tinha recebido">{{ letter.consumer || '—' }}</td>
                        <td class="wide mono cut" data-label="Conteúdo">{{ preview(letter.body) }}</td>
                        <td class="right actions wide">
                          <button mat-icon-button (click)="show(letter)" matTooltip="Ver conteúdo" aria-label="Ver conteúdo"><mat-icon svgIcon="visibility" /></button>
                          <button mat-icon-button (click)="requeue(letter)" matTooltip="Reenviar para a fila" aria-label="Reenviar"><mat-icon svgIcon="replay" /></button>
                          <button mat-icon-button (click)="discard(letter)" matTooltip="Descartar" aria-label="Descartar"><mat-icon svgIcon="delete" /></button>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else if (selected()) {
              <div class="empty">
                <mat-icon svgIcon="check_circle" />
                <strong>Esta fila não tem mais mensagens mortas</strong>
              </div>
            }
          </section>
        </div>
      }
    </div>
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px 16px; flex-wrap: wrap; }
    .head .spacer { flex: 1; }
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .layout { display: grid; grid-template-columns: minmax(240px, 320px) minmax(0, 1fr); gap: var(--zap-gap); align-items: start; }
    .queues ul { list-style: none; margin: 0; padding: 12px; display: grid; gap: 2px; max-height: 70vh; overflow: auto; }
    .queues button {
      width: 100%;
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 12px;
      border: none;
      border-radius: 12px;
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .queues button:hover { background: var(--mat-sys-surface-container-high); }
    .queues button.active { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .queues .name { flex: 1; font-weight: 550; }
    .queues .counts { display: flex; gap: 4px; flex: none; }
    .letters .panel-head { padding-bottom: 12px; }
    .actions { white-space: nowrap; }
    .danger:not(:disabled) {
      --mat-button-outlined-label-text-color: var(--zap-danger);
      --mat-button-outlined-outline-color: color-mix(in srgb, var(--zap-danger) 50%, transparent);
    }
    @media (max-width: 959.98px) {
      .layout { grid-template-columns: 1fr; }
      .queues ul { max-height: 240px; }
    }
    @media (max-width: 760px) { .actions { text-align: right !important; } }
  `,
})
export class DeadLettersPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);
  private timer: ReturnType<typeof setInterval> | null = null;

  /** From the address: the queue to open. */
  readonly fila = input<string>();

  protected readonly summary = signal<DeadSummary[]>([]);
  protected readonly letters = signal<DeadLetter[]>([]);
  protected readonly selected = computed(() => this.fila() ?? '');

  constructor() {
    effect(() => {
      this.selected();
      this.letters.set([]);
      this.loadLetters();
    });
  }

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 5000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected reason(letter: DeadLetter) {
    return REASONS[letter.reason];
  }

  protected pick(queue: string): void {
    void this.router.navigate([], { queryParams: { fila: queue }, replaceUrl: true });
  }

  protected preview(body: unknown): string {
    const text = JSON.stringify(body);
    return text.length > 240 ? text.slice(0, 240) + '…' : text;
  }

  protected show(letter: DeadLetter): void {
    showJson(this.dialog, { title: 'Mensagem morta', subtitle: letter.id, json: letter.body });
  }

  protected async requeue(letter: DeadLetter): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Reenviar a mensagem?',
      message: `Uma cópia volta para a fila "${letter.queue}" e esta mensagem morta é removida.`,
      warning:
        letter.reason === 'unconfirmed'
          ? 'Esta mensagem chegou a ser entregue. Ela pode já ter sido processada, total ou parcialmente; reenviar pode processá-la de novo.'
          : undefined,
      action: 'Reenviar',
      danger: letter.reason === 'unconfirmed',
    });
    if (!sure) {
      return;
    }
    try {
      await firstValueFrom(this.api.requeue(letter.queue, letter.id));
      this.snack.open('Mensagem reenviada para a fila', undefined, { duration: 2500 });
    } catch {
      this.snack.open('A mensagem não existe mais', 'Fechar', { duration: 5000 });
    }
    this.refresh();
  }

  protected async discard(letter: DeadLetter): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Descartar a mensagem?',
      message: 'Ela é apagada de vez e não pode mais ser inspecionada nem reenviada.',
      action: 'Descartar',
      danger: true,
    });
    if (!sure) {
      return;
    }
    await firstValueFrom(this.api.discard(letter.queue, letter.id)).catch(() => undefined);
    this.refresh();
  }

  protected async discardAll(): Promise<void> {
    const queue = this.selected();
    const sure = await confirm(this.dialog, {
      title: 'Descartar todas?',
      message: `As ${this.letters().length} mensagens mortas da fila "${queue}" são apagadas de vez.`,
      action: 'Descartar todas',
      danger: true,
    });
    if (!sure) {
      return;
    }
    const result = await firstValueFrom(this.api.discardAll(queue));
    this.snack.open(`${result.discarded} mensagens descartadas`, undefined, { duration: 3000 });
    this.refresh();
  }

  private refresh(): void {
    this.api.deadSummary().subscribe({
      next: (summary) => {
        this.summary.set(summary);
        // Nothing chosen yet: open the first queue that has something.
        if (!this.selected() && summary.length) {
          this.pick(summary[0].queue);
        }
      },
      error: () => undefined,
    });
    this.loadLetters();
  }

  private loadLetters(): void {
    const queue = this.selected();
    if (!queue) {
      return;
    }
    this.api.deadLetters(queue).subscribe({ next: (letters) => queue === this.selected() && this.letters.set(letters), error: () => undefined });
  }
}
