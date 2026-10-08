import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, of } from 'rxjs';
import { Api } from '../core/api';
import { alertText, size } from '../core/database';
import { AgoPipe, NumPipe, SincePipe, span } from '../core/format';
import { DatabaseActivity, DatabaseAlert, DatabasePoint, DatabaseQuery, DatabaseState } from '../core/models';
import { Series, TimeChart } from '../shared/chart';
import { DatabaseTabs } from '../shared/database-tabs';
import { Stat } from '../shared/stat';

type Cost = 'cpu' | 'time' | 'reads';

const PERIODS = [
  { minutes: 60, label: '1 h' },
  { minutes: 1440, label: '24 h' },
  { minutes: 10080, label: '7 dias' },
];

/** The parts of a look, as the screen calls them. */
const PARTS: Record<string, string> = {
  Instance: 'a identificação da instância',
  Machine: 'o tempo no ar e a máquina',
  Cpu: 'o uso de processador',
  Memory: 'o uso de memória',
  Counters: 'os contadores de desempenho',
  Databases: 'a lista de bancos',
  Sessions: 'as sessões',
  Activity: 'o que está em execução',
  Volumes: 'os discos',
  Backups: 'os backups',
  Jobs: 'os jobs',
};

const DECIMAL = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });
const WHOLE = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

/**
 * How the database instance of the environment is doing: whether it answers, how loaded it
 * is, what is running on it and what asks for attention. Read by the Worker Control from
 * what the instance says about itself; no row of any table ever comes here.
 */
@Component({
  selector: 'zap-database',
  imports: [MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, DatabaseTabs, Stat, TimeChart, NumPipe, AgoPipe, SincePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './database.html',
  styleUrl: './database.scss',
})
export class DatabasePage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private timer: ReturnType<typeof setInterval> | null = null;
  private historyTimer: ReturnType<typeof setInterval> | null = null;

  protected readonly periods = PERIODS;
  protected readonly minutes = signal(60);
  protected readonly state = signal<DatabaseState | null>(null);
  private readonly points = signal<DatabasePoint[]>([]);
  protected readonly queries = signal<DatabaseQuery[] | null>(null);
  protected readonly queriesProblem = signal('');
  protected readonly queriesLoading = signal(false);
  protected readonly cost = signal<Cost>('cpu');
  protected readonly problem = signal('');
  protected readonly outdated = signal(false);
  /** The statement shown whole. */
  protected readonly open = signal<string | null>(null);
  protected readonly systemToo = signal(false);

  protected readonly alertText = alertText;
  protected readonly size = size;

  protected readonly unread = computed(() =>
    Object.entries(this.state()?.Problems ?? {}).map(([part, message]) => ({ part: PARTS[part] ?? part, message })),
  );

  protected readonly grave = computed(() => (this.state()?.Alerts ?? []).some((alert) => alert.Severity === 'danger'));

  protected readonly sessions = computed(() => {
    const groups = this.state()?.Sessions ?? [];
    return {
      all: groups.reduce((total, group) => total + group.Sessions, 0),
      running: groups.reduce((total, group) => total + group.Running, 0),
      list: [...groups].sort((a, b) => b.Sessions - a.Sessions || a.Program.localeCompare(b.Program, 'pt-BR')),
    };
  });

  /** What is running, the blocked and whoever blocks them first. */
  protected readonly activity = computed(() => {
    const activity = this.state()?.Activity ?? [];
    const blockers = new Set(activity.filter((item) => item.BlockedBy > 0).map((item) => item.BlockedBy));
    return activity
      .map((item) => ({ ...item, blocks: blockers.has(item.SessionId) }))
      .sort((a, b) => Number(b.blocks) - Number(a.blocks) || Number(b.BlockedBy > 0) - Number(a.BlockedBy > 0) || b.ElapsedMs - a.ElapsedMs);
  });

  protected readonly blocked = computed(() => (this.state()?.Activity ?? []).filter((item) => item.BlockedBy > 0).length);

  protected readonly databases = computed(() => {
    const state = this.state();
    const backups = new Map((state?.Backups ?? []).map((backup) => [backup.Database.toLowerCase(), backup]));
    return (state?.DatabaseList ?? [])
      .filter((database) => this.systemToo() || !database.System)
      .map((database) => ({ ...database, backup: backups.get(database.Name.toLowerCase()) ?? null, watched: state!.Databases.some((name) => name.toLowerCase() === database.Name.toLowerCase()) }));
  });

  protected readonly hasBackups = computed(() => this.state()?.Backups !== null && this.state()?.Backups !== undefined);

  protected readonly volumes = computed(() =>
    (this.state()?.Volumes ?? []).map((volume) => ({ ...volume, used: volume.TotalBytes ? 100 - (volume.FreeBytes / volume.TotalBytes) * 100 : 0, low: this.state()!.Alerts.some((alert) => alert.Kind === 'Disk' && alert.Subject === volume.Mount) })),
  );

  protected readonly jobs = computed(() => [...(this.state()?.Jobs ?? [])].sort((a, b) => Number(b.Enabled && b.Outcome === 0) - Number(a.Enabled && a.Outcome === 0) || a.Name.localeCompare(b.Name, 'pt-BR')));

  protected readonly costly = computed(() => {
    const cost = this.cost();
    const value = (query: DatabaseQuery) => (cost === 'cpu' ? query.CpuMs : cost === 'time' ? query.ElapsedMs : query.Reads);
    const list = (this.queries() ?? []).filter((query) => (cost === 'cpu' ? query.TopCpu : cost === 'time' ? query.TopTime : query.TopReads)).sort((a, b) => value(b) - value(a));
    const most = list.length ? value(list[0]) : 0;
    return list.map((query) => ({ ...query, share: most ? (value(query) / most) * 100 : 0, key: query.Database + query.Object + query.Text }));
  });

  protected readonly processor = computed<Series[]>(() => {
    const point = (value: (item: DatabasePoint) => number | null) => this.points().filter((item) => value(item) !== null).map((item) => [Date.parse(item.At), Math.round(value(item)! * 10) / 10] as [number, number]);
    return [
      { name: 'SQL Server', color: '#6d5efc', area: true, data: point((item) => item.CpuPercent) },
      { name: 'Outros processos da máquina', color: '#22c1dc', data: point((item) => item.OtherCpuPercent) },
    ];
  });

  protected readonly load = computed<Series[]>(() => {
    const point = (value: (item: DatabasePoint) => number | null) => this.points().filter((item) => value(item) !== null).map((item) => [Date.parse(item.At), Math.round(value(item)! * 10) / 10] as [number, number]);
    return [
      { name: 'Sessões', color: '#6d5efc', area: true, data: point((item) => item.Sessions) },
      { name: 'Em execução', color: '#22c1dc', data: point((item) => item.Running) },
      { name: 'Bloqueadas', color: '#e5484d', data: point((item) => item.Blocked) },
    ];
  });

  /** The stretches in which the instance did not answer. */
  protected readonly outages = computed(() => {
    const marks: { at: number; label: string }[] = [];
    let down = false;
    for (const point of this.points()) {
      if (!point.Online && !down) {
        marks.push({ at: Date.parse(point.At), label: 'fora do ar' });
      }
      down = !point.Online;
    }
    return marks;
  });

  ngOnInit(): void {
    this.refresh();
    this.history();
    this.loadQueries();
    this.timer = setInterval(() => this.refresh(), 10000);
    this.historyTimer = setInterval(() => this.history(), 60000);
  }

  ngOnDestroy(): void {
    for (const timer of [this.timer, this.historyTimer]) {
      if (timer) {
        clearInterval(timer);
      }
    }
  }

  protected period(minutes: number): void {
    this.minutes.set(minutes);
    this.history();
  }

  protected toggle(key: string): void {
    this.open.set(this.open() === key ? null : key);
  }

  protected ms(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : value >= 1000 ? `${DECIMAL.format(value / 1000)} s` : `${WHOLE.format(value)} ms`;
  }

  protected percent(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : `${WHOLE.format(value)}%`;
  }

  protected lasted(milliseconds: number): string {
    return milliseconds < 1000 ? `${WHOLE.format(milliseconds)} ms` : span(milliseconds);
  }

  protected minutesAgo(minutes: number | null | undefined): string {
    return minutes === null || minutes === undefined ? '—' : `há ${span(minutes * 60000)}`;
  }

  protected uptime(seconds: number): string {
    return span(seconds * 1000);
  }

  protected outcome(job: { Enabled: boolean; Outcome: number | null }): { text: string; tone: string } {
    if (!job.Enabled) {
      return { text: 'desligado', tone: '' };
    }
    switch (job.Outcome) {
      case null:
        return { text: 'nunca rodou', tone: '' };
      case 0:
        return { text: 'falhou', tone: 'danger' };
      case 1:
        return { text: 'ok', tone: 'ok' };
      case 3:
        return { text: 'cancelado', tone: 'warn' };
      default:
        return { text: 'em andamento', tone: 'info' };
    }
  }

  protected tone(alert: DatabaseAlert): string {
    return alert.Severity === 'danger' ? 'danger' : 'warn';
  }

  protected who(item: DatabaseActivity): string {
    return [item.Program || item.Login, item.Host].filter(Boolean).join(' · ');
  }

  protected loadQueries(): void {
    this.queriesLoading.set(true);
    this.api.databaseQueries().subscribe({
      next: (answer) => {
        this.queries.set(answer.Queries);
        this.queriesProblem.set('');
        this.queriesLoading.set(false);
      },
      error: (failure: HttpErrorResponse) => {
        this.queriesLoading.set(false);
        // Nothing to say when there is no database or no Worker Control: the rest of the screen says it.
        this.queriesProblem.set(failure.status === 502 ? (failure.error?.error ?? '') : '');
      },
    });
  }

  private history(): void {
    this.api
      .databaseHistory(this.minutes())
      .pipe(catchError(() => of({ Points: [] as DatabasePoint[] })))
      .subscribe((answer) => this.points.set(answer.Points));
  }

  private refresh(): void {
    this.api.database().subscribe({
      next: (state) => {
        this.state.set(state);
        // Asked for before the instance could be reached: asked for again now that it can.
        if (state.Online && this.queries() === null && !this.queriesLoading()) {
          this.loadQueries();
        }
        this.problem.set('');
        this.outdated.set(false);
      },
      error: (failure: HttpErrorResponse) => {
        if (failure.status === 501) {
          this.outdated.set(true);
        } else if (failure.status !== 401) {
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }
}
