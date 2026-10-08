import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { BoostWindow, GroupConfig, WorkerConfig } from '../core/models';
import { confirm } from '../shared/dialogs';
import { WorkerTabs } from '../shared/worker-tabs';

const DAYS = [
  { key: 'mon', label: 'Seg' }, { key: 'tue', label: 'Ter' }, { key: 'wed', label: 'Qua' }, { key: 'thu', label: 'Qui' },
  { key: 'fri', label: 'Sex' }, { key: 'sat', label: 'Sáb' }, { key: 'sun', label: 'Dom' },
];

/** The file also takes the days written in full ("monday"). */
const short = (day: string) => day.slice(0, 3).toLowerCase();

/**
 * ConfigWorkers.json, edited as forms or as the text it is. The whole file is sent back; the
 * Worker Control validates it before putting it in force, and keeps the previous version.
 */
@Component({
  selector: 'zap-worker-config',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatIconModule, MatSlideToggleModule, WorkerTabs],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './worker-config.html',
  styleUrl: './worker-config.scss',
})
export class WorkerConfigPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  protected readonly days = DAYS;
  protected readonly mode = signal<'form' | 'text'>('form');
  /** The copy being edited. Null until the file arrives. */
  protected readonly config = signal<WorkerConfig | null>(null);
  protected readonly selected = signal(-1);
  protected readonly dirty = signal(false);
  protected readonly saving = signal(false);
  protected readonly problem = signal('');
  /** What the Worker Control said was wrong with the last attempt to save. */
  protected readonly refusal = signal('');
  protected text = '';
  private original = '';

  protected readonly group = computed<GroupConfig | null>(() => this.config()?.WorkerGroups[this.selected()] ?? null);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api.workerConfig().subscribe({
      next: (answer) => {
        this.original = JSON.stringify(answer.Config, null, 4);
        this.text = this.original;
        this.config.set(structuredClone(answer.Config));
        this.selected.update((index) => Math.min(index, answer.Config.WorkerGroups.length - 1));
        this.dirty.set(false);
        this.problem.set('');
        this.refusal.set('');
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }

  /** Any field of the forms changed. */
  protected touch(): void {
    this.dirty.set(true);
    // The object was edited in place; a new reference makes the screen notice.
    this.config.update((current) => (current ? { ...current } : current));
  }

  protected switchMode(mode: 'form' | 'text'): void {
    const config = this.config();
    if (mode === 'text' && config) {
      this.text = JSON.stringify(config, null, 4);
    } else if (mode === 'form') {
      try {
        this.config.set(JSON.parse(this.text) as WorkerConfig);
      } catch {
        this.snack.open('O texto não é um JSON válido; corrija antes de voltar aos formulários', 'Fechar', { duration: 6000 });
        return;
      }
    }
    this.mode.set(mode);
  }

  protected addGroup(): void {
    const config = this.config();
    if (!config) {
      return;
    }
    config.WorkerGroups.push({
      Enabled: false,
      Name: this.freeName(config),
      ApplicationFullPath: '',
      TotalWorkers: 1,
      MonitoringRate: 30000,
      TimeoutKeepAlive: 15000,
      Boost: { Enabled: false, BoostWorkers: 0, StartTime: '00:00:00', EndTime: '00:00:00' },
    });
    this.selected.set(config.WorkerGroups.length - 1);
    this.touch();
  }

  protected async removeGroup(): Promise<void> {
    const config = this.config();
    const group = this.group();
    if (!config || !group) {
      return;
    }
    const sure = await confirm(this.dialog, {
      title: `Remover o grupo ${group.Name}?`,
      message: 'Ao gravar, os processos do grupo são avisados para sair e o grupo deixa de existir.',
      action: 'Remover',
      danger: true,
    });
    if (sure) {
      config.WorkerGroups.splice(this.selected(), 1);
      this.selected.set(-1);
      this.touch();
    }
  }

  protected addWindow(group: GroupConfig): void {
    (group.BoostWindows ??= []).push({ Workers: 1, StartTime: '08:00:00', EndTime: '18:00:00' });
    this.touch();
  }

  protected removeWindow(group: GroupConfig, index: number): void {
    group.BoostWindows?.splice(index, 1);
    if (!group.BoostWindows?.length) {
      delete group.BoostWindows;
    }
    this.touch();
  }

  protected hasDay(owner: { Days?: string[] }, day: string): boolean {
    return !owner.Days?.length || owner.Days.some((given) => short(given) === day);
  }

  /** No day listed means every day; listing all seven goes back to listing none. */
  protected toggleDay(owner: BoostWindow | { Time: string; Days?: string[] }, day: string): void {
    const chosen = new Set(owner.Days?.length ? owner.Days.map(short) : DAYS.map((item) => item.key));
    if (chosen.has(day)) {
      chosen.delete(day);
    } else {
      chosen.add(day);
    }
    if (chosen.size === 0 || chosen.size === DAYS.length) {
      delete owner.Days;
    } else {
      owner.Days = DAYS.map((item) => item.key).filter((key) => chosen.has(key));
    }
    this.touch();
  }

  protected toggleScaling(group: GroupConfig, on: boolean): void {
    if (on) {
      group.QueueScaling = { Queue: '', PendingPerWorker: 50, MaxWorkers: Math.max(group.TotalWorkers + 1, 2), CooldownMs: 120000 };
    } else {
      delete group.QueueScaling;
    }
    this.touch();
  }

  protected toggleRecycle(group: GroupConfig, on: boolean): void {
    if (on) {
      group.Recycle = { Time: '03:00:00' };
    } else {
      delete group.Recycle;
    }
    this.touch();
  }

  /** Empty optional fields leave the file instead of going as empty text or zero. */
  protected optional(owner: Record<string, unknown>, key: string, value: unknown): void {
    if (value === '' || value === null || value === undefined) {
      delete owner[key];
    } else {
      owner[key] = value;
    }
    this.touch();
  }

  protected async save(): Promise<void> {
    let payload: WorkerConfig | string;
    if (this.mode() === 'text') {
      try {
        JSON.parse(this.text);
      } catch (error) {
        this.refusal.set('O texto não é um JSON válido: ' + (error as Error).message);
        return;
      }
      payload = this.text;
    } else {
      payload = this.config()!;
    }

    this.saving.set(true);
    this.refusal.set('');
    try {
      await firstValueFrom(this.api.saveWorkerConfig(payload));
      this.snack.open('Configuração gravada e em vigor', undefined, { duration: 3000 });
      this.load();
    } catch (failure) {
      const response = failure as HttpErrorResponse;
      this.refusal.set(response.error?.error ?? 'O Worker Control não respondeu.');
    } finally {
      this.saving.set(false);
    }
  }

  protected async discard(): Promise<void> {
    if (await confirm(this.dialog, { title: 'Descartar as alterações?', message: 'A configuração volta a ser a que está em vigor.', action: 'Descartar' })) {
      this.load();
    }
  }

  private freeName(config: WorkerConfig): string {
    for (let number = 1; ; number++) {
      const name = `Novo grupo ${number}`;
      if (!config.WorkerGroups.some((group) => group.Name === name)) {
        return name;
      }
    }
  }
}
