import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { AgoPipe, NumPipe, SincePipe } from '../core/format';
import { MonitoredService, ServiceConfig, WorkerConfig, WorkerControlStatus } from '../core/models';
import { confirm } from '../shared/dialogs';
import { AddServiceDialog, ServiceChoice, ServiceSettingsDialog } from '../shared/service-dialogs';
import { Stat } from '../shared/stat';
import { WorkerHealthDialog } from '../shared/worker-health';
import { WorkerTabs } from '../shared/worker-tabs';

const STATES: Record<MonitoredService['State'], { label: string; tone: string }> = {
  Running: { label: 'rodando', tone: 'ok' },
  Stopped: { label: 'parado', tone: '' },
  Starting: { label: 'iniciando', tone: 'info' },
  Stopping: { label: 'parando', tone: 'warn' },
  Paused: { label: 'pausado', tone: 'warn' },
  Missing: { label: 'não instalado', tone: 'danger' },
};

/**
 * The Windows services the Worker Control watches without having started them: how each one
 * is, and what can be asked of it.
 */
@Component({
  selector: 'zap-services',
  imports: [RouterLink, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, Stat, WorkerTabs, NumPipe, SincePipe, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './services.html',
  styleUrl: './services.scss',
})
export class ServicesPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly status = signal<WorkerControlStatus | null>(null);
  protected readonly problem = signal('');
  protected readonly busy = signal('');

  /** Null while there is no answer, or when the Worker Control is from before services. */
  protected readonly services = computed(() => this.status()?.Services ?? null);
  protected readonly outdated = computed(() => this.status() !== null && this.status()!.Services === undefined);

  protected readonly totals = computed(() => {
    const services = this.services() ?? [];
    return {
      all: services.length,
      running: services.filter((service) => service.State === 'Running').length,
      attention: services.filter((service) => this.wrong(service)).length,
      memory: services.reduce((total, service) => total + (service.MemoryBytes ?? 0), 0),
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

  protected state(service: MonitoredService) {
    return STATES[service.State];
  }

  /** Something about it asks for a look. */
  protected wrong(service: MonitoredService): boolean {
    return service.State === 'Missing' || service.Unstable || service.Check?.Ok === false || (service.State === 'Stopped' && (service.ExitCode ?? 0) !== 0);
  }

  protected megabytes(bytes: number | null): number | null {
    return bytes === null ? null : Math.round(bytes / 1048576);
  }

  protected executable(path: string | null): string {
    return path ? path.split(/[\\/]/).pop()! : '';
  }

  /** What the service runs, when it is only what starts the program that does the work. */
  protected hosted(service: MonitoredService): string {
    const names = [...new Set(service.Children ?? [])];
    return names.length ? ' → ' + names.join(', ') : '';
  }

  protected async act(service: MonitoredService, action: 'start' | 'stop' | 'restart'): Promise<void> {
    const name = service.DisplayName || service.Name;
    if (action !== 'start') {
      const sure = await confirm(this.dialog, {
        title: `${action === 'stop' ? 'Parar' : 'Reiniciar'} ${name}?`,
        message:
          action === 'stop'
            ? 'O serviço é parado pelo Windows e fica parado até alguém iniciá-lo.'
            : 'O serviço é parado pelo Windows e iniciado de novo assim que terminar de parar.',
        warning: 'Quem estiver usando o serviço é interrompido.',
        action: action === 'stop' ? 'Parar' : 'Reiniciar',
        danger: true,
      });
      if (!sure) {
        return;
      }
    }
    this.busy.set(service.Name);
    try {
      await firstValueFrom(this.api.serviceAction(service.Name, action));
      this.snack.open({ start: 'Início pedido', stop: 'Parada pedida', restart: 'Reinício pedido' }[action], undefined, { duration: 2500 });
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou o pedido', 'Fechar', { duration: 8000 });
    } finally {
      this.busy.set('');
      this.refresh();
    }
  }

  protected health(service: MonitoredService): void {
    this.dialog.open(WorkerHealthDialog, { data: { group: service.DisplayName || service.Name, pid: service.ProcessId }, maxWidth: '760px', width: 'calc(100vw - 32px)' });
  }

  protected async add(): Promise<void> {
    const choice = await firstValueFrom(
      this.dialog.open<AddServiceDialog, unknown, ServiceChoice>(AddServiceDialog, { maxWidth: '760px', width: 'calc(100vw - 32px)' }).afterClosed(),
    );
    if (!choice) {
      return;
    }
    await this.change(
      (config) => {
        const section = (config.Services ??= {});
        section.SuggestFrom = choice.suggestFrom;
        const items = (section.Items ??= []);
        for (const name of choice.names) {
          if (!items.some((item) => item.Name.toLowerCase() === name.toLowerCase())) {
            items.push({ Name: name });
          }
        }
      },
      choice.names.length ? (choice.names.length === 1 ? 'Serviço adicionado' : `${choice.names.length} serviços adicionados`) : 'Pastas gravadas',
    );
  }

  protected async settings(service: MonitoredService): Promise<void> {
    let config: WorkerConfig;
    try {
      config = (await firstValueFrom(this.api.workerConfig())).Config;
    } catch {
      this.snack.open('O Worker Control não respondeu', 'Fechar', { duration: 6000 });
      return;
    }
    const current = config.Services?.Items?.find((item) => item.Name.toLowerCase() === service.Name.toLowerCase()) ?? { Name: service.Name };
    const changed = await firstValueFrom(
      this.dialog
        .open<ServiceSettingsDialog, unknown, ServiceConfig>(ServiceSettingsDialog, {
          data: { title: service.DisplayName || service.Name, config: current },
          maxWidth: '520px',
          width: 'calc(100vw - 32px)',
        })
        .afterClosed(),
    );
    if (changed) {
      await this.change((latest) => {
        const items = ((latest.Services ??= {}).Items ??= []);
        const index = items.findIndex((item) => item.Name.toLowerCase() === service.Name.toLowerCase());
        if (index >= 0) {
          items[index] = changed;
        } else {
          items.push(changed);
        }
      }, 'Configuração do serviço gravada');
    }
  }

  protected async remove(service: MonitoredService): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: `Deixar de acompanhar ${service.DisplayName || service.Name}?`,
      message: 'O serviço continua como está no Windows; só sai deste painel. O histórico dele fica guardado.',
      action: 'Deixar de acompanhar',
    });
    if (sure) {
      await this.change((config) => {
        if (config.Services?.Items) {
          config.Services.Items = config.Services.Items.filter((item) => item.Name.toLowerCase() !== service.Name.toLowerCase());
        }
      }, 'O serviço saiu do painel');
    }
  }

  /** Reads the configuration as it is now, changes it and writes it back, validated by the Worker Control. */
  private async change(mutate: (config: WorkerConfig) => void, done: string): Promise<void> {
    try {
      const config = (await firstValueFrom(this.api.workerConfig())).Config;
      mutate(config);
      await firstValueFrom(this.api.saveWorkerConfig(config));
      this.snack.open(done, undefined, { duration: 2500 });
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou a mudança', 'Fechar', { duration: 8000 });
    } finally {
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
