import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { NumPipe, SincePipe } from '../core/format';
import { Worker, WorkerControlStatus, WorkerGroup } from '../core/models';
import { confirm } from '../shared/dialogs';
import { Stat } from '../shared/stat';
import { WorkerHealthDialog } from '../shared/worker-health';
import { WorkerTabs } from '../shared/worker-tabs';

const STATES: Record<string, { label: string; tone: string }> = {
  Starting: { label: 'iniciando', tone: 'info' },
  Up: { label: 'no ar', tone: 'ok' },
  Stopping: { label: 'parando', tone: 'warn' },
  Killing: { label: 'encerrando', tone: 'danger' },
};

/** The groups the Worker Control keeps running, and their processes, as they are right now. */
@Component({
  selector: 'zap-workers',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatSlideToggleModule, MatTooltipModule, Stat, WorkerTabs, NumPipe, SincePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workers.html',
  styleUrl: './workers.scss',
})
export class WorkersPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly status = signal<WorkerControlStatus | null>(null);
  /** Why there is no status, when there is none. */
  protected readonly problem = signal('');
  protected readonly busy = signal('');

  protected readonly totals = computed(() => {
    const groups = this.status()?.Groups ?? [];
    const workers = groups.flatMap((group) => group.Workers);
    return {
      groups: groups.length,
      enabled: groups.filter((group) => group.Enabled).length,
      unstable: groups.filter((group) => group.Unstable).length,
      desired: groups.reduce((total, group) => total + group.DesiredWorkers, 0),
      up: workers.filter((worker) => worker.State === 'Up').length,
      starting: workers.filter((worker) => worker.State === 'Starting').length,
      memory: workers.reduce((total, worker) => total + (worker.MemoryBytes ?? 0), 0),
      cpu: workers.reduce((total, worker) => total + (worker.CpuPercent ?? 0), 0),
    };
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 3000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected state(worker: Worker) {
    return STATES[worker.State] ?? { label: worker.State, tone: '' };
  }

  protected up(group: WorkerGroup): number {
    return group.Workers.filter((worker) => worker.State === 'Up').length;
  }

  protected executable(path: string): string {
    return path.split(/[\\/]/).pop() ?? path;
  }

  protected megabytes(bytes: number | null): number | null {
    return bytes === null ? null : Math.round(bytes / 1048576);
  }

  protected async toggle(group: WorkerGroup): Promise<void> {
    const enabling = !group.Enabled;
    if (!enabling) {
      const sure = await confirm(this.dialog, {
        title: `Desabilitar ${group.Name}?`,
        message: `Os ${group.Workers.length} processos do grupo são avisados para terminar o que estão fazendo e sair.`,
        action: 'Desabilitar',
        danger: true,
      });
      if (!sure) {
        // The switch already moved; a fresh status puts it back.
        this.status.update((current) => (current ? { ...current } : current));
        this.refresh();
        return;
      }
    }
    await this.run(this.api.setGroupEnabled(group.Name, enabling), enabling ? 'Grupo habilitado' : 'Grupo desabilitado', group.Name);
  }

  protected async resize(group: WorkerGroup, change: number): Promise<void> {
    const wanted = Math.max(0, group.TotalWorkers + change);
    if (wanted !== group.TotalWorkers) {
      await this.run(this.api.setGroupWorkers(group.Name, wanted), `${group.Name}: ${wanted} ${wanted === 1 ? 'processo' : 'processos'}`, group.Name);
    }
  }

  protected async restartGroup(group: WorkerGroup): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: `Reiniciar ${group.Name}?`,
      message: 'Os processos são substituídos um a um: um novo é iniciado e, quando está no ar, um antigo é avisado para sair. O grupo não fica desfalcado.',
      action: 'Reiniciar grupo',
    });
    if (sure) {
      await this.run(this.api.restartGroup(group.Name), 'Reinício do grupo pedido', group.Name);
    }
  }

  protected async restartWorker(group: WorkerGroup, worker: Worker): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: `Reiniciar o processo ${worker.ProcessId}?`,
      message: `Um substituto é iniciado no grupo ${group.Name} e, quando estiver no ar, este processo é avisado para sair.`,
      action: 'Reiniciar processo',
    });
    if (sure) {
      await this.run(this.api.restartWorker(worker.ProcessId), 'Reinício do processo pedido', group.Name);
    }
  }

  protected health(group: WorkerGroup, worker: Worker): void {
    this.dialog.open(WorkerHealthDialog, { data: { group: group.Name, pid: worker.ProcessId }, maxWidth: '760px', width: 'calc(100vw - 32px)' });
  }

  /** For changing the Worker Control itself: it leaves, and what it was running stays. */
  protected async detach(): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Parar o Worker Control?',
      message:
        'O serviço para e os processos continuam rodando, sem ninguém cuidando deles: quem cair não é reiniciado. Ao ser iniciado de novo, na máquina dele, o serviço reconhece os processos e volta a acompanhá-los. O painel não consegue iniciá-lo.',
      action: 'Parar o serviço',
      danger: true,
    });
    if (sure) {
      await this.run(this.api.detachWorkerControl(), 'O Worker Control está parando; os processos continuam', '');
    }
  }

  private async run(action: Observable<unknown>, done: string, group: string): Promise<void> {
    this.busy.set(group);
    try {
      await firstValueFrom(action);
      this.snack.open(done, undefined, { duration: 2500 });
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou o pedido', 'Fechar', { duration: 7000 });
    } finally {
      this.busy.set('');
      this.refresh();
    }
  }

  private refresh(): void {
    this.api.workerStatus().subscribe({
      next: (status) => {
        this.status.set(status);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => {
        if (failure.status !== 401) {
          this.status.set(null);
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }
}
