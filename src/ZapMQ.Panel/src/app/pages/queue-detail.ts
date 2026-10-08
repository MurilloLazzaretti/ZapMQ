import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { AgoPipe, NumPipe, SincePipe, WhenPipe, seconds } from '../core/format';
import { PendingMessage, QueueDetail, QueueSettings } from '../core/models';
import { confirm, showJson } from '../shared/dialogs';
import { Stat } from '../shared/stat';

/** The form of the definition: empty means "use the default of the service". */
interface Form {
  retentionSeconds: number | null;
  redeliverUnconfirmed: boolean;
  deadLimit: number | null;
  deadAgeHours: number | null;
}

@Component({
  selector: 'zap-queue-detail',
  imports: [
    FormsModule, RouterLink, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule, MatTooltipModule,
    Stat, NumPipe, AgoPipe, SincePipe, WhenPipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './queue-detail.html',
  styleUrl: './queue-detail.scss',
})
export class QueueDetailPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private timer: ReturnType<typeof setInterval> | null = null;

  /** From the address. */
  readonly name = input.required<string>();
  /** Set when coming from "Definir fila": opens the form right away. */
  readonly definir = input<string>();

  protected readonly detail = signal<QueueDetail | null>(null);
  protected readonly messages = signal<PendingMessage[]>([]);
  protected readonly failed = signal(false);
  protected readonly editing = signal(false);
  protected readonly saving = signal(false);
  protected form: Form = { retentionSeconds: null, redeliverUnconfirmed: false, deadLimit: null, deadAgeHours: null };

  protected readonly snapshot = computed(() => this.detail()?.snapshot ?? null);
  protected readonly dead = computed(() => {
    const letters = this.detail()?.deadLetters;
    return letters ? letters.expired + letters.notConsumed + letters.unconfirmed : 0;
  });

  /** What is in force, whether it came from the definition or from the defaults. */
  protected readonly effective = computed(() => {
    const detail = this.detail();
    if (!detail) {
      return null;
    }
    const own = detail.settings;
    return {
      retention: seconds(own?.retentionSeconds ?? detail.defaults.retentionSeconds),
      retentionOwn: own?.retentionSeconds != null,
      redeliver: own?.redeliverUnconfirmed ?? false,
      deadLimit: own?.deadLetters?.maxMessagesPerQueue ?? detail.defaults.deadLetters.maxMessagesPerQueue,
      deadLimitOwn: own?.deadLetters?.maxMessagesPerQueue != null,
      deadAge: own?.deadLetters?.maxAgeHours ?? detail.defaults.deadLetters.maxAgeHours,
      deadAgeOwn: own?.deadLetters?.maxAgeHours != null,
    };
  });

  constructor() {
    // The same screen is reused when the address changes from one queue to another.
    effect(() => {
      this.name();
      this.detail.set(null);
      this.messages.set([]);
      this.editing.set(false);
      this.refresh();
    });
  }

  ngOnInit(): void {
    this.timer = setInterval(() => !this.editing() && this.refresh(), 3000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected refresh(): void {
    const name = this.name();
    this.api.queue(name).subscribe({
      next: (detail) => {
        if (name !== this.name()) {
          return;
        }
        const first = this.detail() === null;
        this.detail.set(detail);
        this.failed.set(false);
        if (first && this.definir() && !detail.settings) {
          this.edit();
        }
      },
      error: () => this.failed.set(true),
    });
    this.api.messages(name).subscribe({ next: (messages) => name === this.name() && this.messages.set(messages), error: () => undefined });
  }

  protected edit(): void {
    const own = this.detail()?.settings;
    this.form = {
      retentionSeconds: own?.retentionSeconds ?? null,
      redeliverUnconfirmed: own?.redeliverUnconfirmed ?? false,
      deadLimit: own?.deadLetters?.maxMessagesPerQueue ?? null,
      deadAgeHours: own?.deadLetters?.maxAgeHours ?? null,
    };
    this.editing.set(true);
  }

  protected async save(): Promise<void> {
    const blank = (value: number | null) => (value === null || (value as unknown) === '' || Number.isNaN(Number(value)) ? null : Number(value));
    const settings: QueueSettings = {
      retentionSeconds: blank(this.form.retentionSeconds),
      redeliverUnconfirmed: this.form.redeliverUnconfirmed,
      deadLetters:
        blank(this.form.deadLimit) === null && blank(this.form.deadAgeHours) === null
          ? null
          : { maxMessagesPerQueue: blank(this.form.deadLimit), maxAgeHours: blank(this.form.deadAgeHours) },
    };

    if (settings.redeliverUnconfirmed && !this.detail()?.settings?.redeliverUnconfirmed) {
      const sure = await confirm(this.dialog, {
        title: 'Reentregar mensagens não confirmadas?',
        message: `Com isso ligado, uma mensagem da fila "${this.name()}" entregue a um consumidor que caiu antes de confirmar volta para a fila e é entregue de novo.`,
        warning: 'A mesma mensagem pode ser processada duas vezes. Ligue só em filas em que isso não causa dano.',
        action: 'Ligar reentrega',
        danger: true,
      });
      if (!sure) {
        return;
      }
    }

    this.saving.set(true);
    try {
      await firstValueFrom(this.api.saveSettings(this.name(), settings));
      this.editing.set(false);
      this.snack.open('Definição gravada', undefined, { duration: 2500 });
      this.refresh();
    } catch {
      this.snack.open('Não foi possível gravar a definição', 'Fechar', { duration: 6000 });
    } finally {
      this.saving.set(false);
    }
  }

  protected async reset(): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Voltar ao padrão?',
      message: `A fila "${this.name()}" deixa de ter definição própria e passa a usar os valores gerais do serviço.`,
      action: 'Voltar ao padrão',
    });
    if (!sure) {
      return;
    }
    await firstValueFrom(this.api.clearSettings(this.name()));
    this.editing.set(false);
    this.snack.open('A fila voltou ao padrão', undefined, { duration: 2500 });
    this.refresh();
  }

  protected async togglePause(): Promise<void> {
    const paused = !!this.snapshot()?.paused || !!this.detail()?.settings?.paused;
    if (!paused) {
      const sure = await confirm(this.dialog, {
        title: 'Pausar a fila?',
        message: `A fila "${this.name()}" continua recebendo mensagens e deixa de entregá-las, a qualquer consumidor, até ser retomada.`,
        warning: 'As mensagens paradas continuam contando o prazo de retenção e a validade delas.',
        action: 'Pausar',
      });
      if (!sure) {
        return;
      }
    }
    await firstValueFrom(this.api.pause(this.name(), !paused));
    this.snack.open(paused ? 'Fila retomada' : 'Fila pausada', undefined, { duration: 2500 });
    this.refresh();
  }

  protected async purge(): Promise<void> {
    const pending = this.snapshot()?.pending ?? 0;
    const sure = await confirm(this.dialog, {
      title: 'Esvaziar a fila?',
      message: `As ${pending} mensagens pendentes da fila "${this.name()}" serão apagadas. As que já estão em processamento não são afetadas.`,
      warning: 'Não há como desfazer: as mensagens apagadas não vão para as mensagens mortas.',
      action: 'Esvaziar',
      danger: true,
    });
    if (!sure) {
      return;
    }
    const result = await firstValueFrom(this.api.purge(this.name()));
    this.snack.open(`${result.purged} mensagens apagadas`, undefined, { duration: 3000 });
    this.refresh();
  }

  protected show(message: PendingMessage): void {
    showJson(this.dialog, { title: 'Mensagem pendente', subtitle: message.id, json: message.body });
  }

  protected preview(body: unknown): string {
    const text = JSON.stringify(body);
    return text.length > 240 ? text.slice(0, 240) + '…' : text;
  }
}
