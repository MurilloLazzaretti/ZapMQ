import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { catchError, forkJoin, of } from 'rxjs';
import { NumPipe, SincePipe } from '../core/format';
import { Live } from '../core/live';
import { alertText } from '../core/database';
import { DatabaseState, MetricsPoint, TrafficSummary, WebApplication, WebPublication, WorkerControlStatus } from '../core/models';
import { Series, TimeChart } from '../shared/chart';
import { Stat } from '../shared/stat';

type Range = 'hour' | 'day';

/** Something that is not as it should be, and where to go to see it. */
interface Concern {
  tone: 'danger' | 'warn';
  text: string;
  link: string;
}

const DECIMAL = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });

/** Where things stand right now, and how the last hour or day went. */
@Component({
  selector: 'zap-overview',
  imports: [RouterLink, MatIconModule, MatButtonToggleModule, Stat, TimeChart, NumPipe, SincePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Visão geral</h1>
          @if (overview(); as now) {
            <p class="muted">O ambiente inteiro em uma tela · ZapMQ {{ now.version }}, no ar há {{ now.startedAt | since }}</p>
          }
        </div>
      </header>

      <section class="panel attention" [class.calm]="!concerns().length">
        @if (concerns().length) {
          <div class="title"><mat-icon svgIcon="warning" /> <strong>{{ concerns().length === 1 ? 'Um ponto pede atenção' : concerns().length + ' pontos pedem atenção' }}</strong></div>
          <ul>
            @for (concern of concerns(); track concern.text) {
              <li><a [routerLink]="concern.link"><span class="dot" [class]="'dot ' + concern.tone"></span> {{ concern.text }} <mat-icon svgIcon="chevron_right" /></a></li>
            }
          </ul>
        } @else {
          <div class="title"><mat-icon svgIcon="verified" /> <strong>Tudo como esperado</strong> <span class="muted">mensageria, processos, serviços, tráfego e aplicação web</span></div>
        }
      </section>

      <section class="blocks">
        <a class="panel block" routerLink="/workers">
          <span class="name"><mat-icon svgIcon="precision_manufacturing" /> Processos e serviços <mat-icon class="go" svgIcon="chevron_right" /></span>
          @if (workers(); as now) {
            <span class="figures">
              <span><b class="num">{{ processes().up }}<small>de {{ processes().desired }}</small></b> processos no ar</span>
              <span><b class="num">{{ services().running }}<small>de {{ services().all }}</small></b> serviços rodando</span>
              <span><b class="num" [class.bad]="processes().unstable">{{ processes().unstable }}</b> grupos instáveis</span>
            </span>
          } @else {
            <span class="muted none">{{ workersProblem() || 'Consultando o Worker Control…' }}</span>
          }
        </a>

        <a class="panel block" routerLink="/trafego">
          <span class="name"><mat-icon svgIcon="monitoring" /> Tráfego HTTP <small class="muted">última hora, APIs</small> <mat-icon class="go" svgIcon="chevron_right" /></span>
          @if (traffic(); as now) {
            @if (now.Configured) {
              <span class="figures">
                <span><b class="num">{{ perMinute() }}</b> requisições por minuto</span>
                <span><b class="num" [class.bad]="now.Totals.S5">{{ share(now.Totals.S5, now.Totals.Count) }}</b> erros do servidor</span>
                <span><b class="num">{{ ms(now.Totals.P95) }}</b> tempo de 95% das respostas</span>
              </span>
            } @else {
              <span class="muted none">Nenhum log de acesso configurado.</span>
            }
          } @else {
            <span class="muted none">{{ workersProblem() ? 'Sem o Worker Control não há medição.' : 'Consultando…' }}</span>
          }
        </a>

        <a class="panel block" routerLink="/web">
          <span class="name"><mat-icon svgIcon="web" /> Aplicação web <mat-icon class="go" svgIcon="chevron_right" /></span>
          @if (web(); as now) {
            @if (now.modules) {
              <span class="figures">
                <span><b class="num" [class.bad]="now.up < now.modules">{{ now.up }}<small>de {{ now.modules }}</small></b> módulos no ar</span>
                <span><b>{{ brief(now.latest) }}</b> última publicação</span>
                <span><b class="num">{{ now.published }}</b> publicações em 7 dias</span>
              </span>
            } @else {
              <span class="muted none">Nenhuma aplicação web configurada.</span>
            }
          } @else {
            <span class="muted none">{{ workersProblem() ? 'Sem o Worker Control não há leitura.' : 'Consultando…' }}</span>
          }
        </a>

        @if (database()?.Configured) {
          <a class="panel block" routerLink="/banco">
            <span class="name"><mat-icon svgIcon="database" /> Banco de dados <small class="muted">{{ database()!.Name }}</small> <mat-icon class="go" svgIcon="chevron_right" /></span>
            @if (database(); as now) {
              @if (now.Online === false) {
                <span class="figures"><span><b class="bad">Fora do ar</b> há {{ now.Since | since }}</span></span>
              } @else if (now.Online) {
                <span class="figures">
                  <span><b class="num">{{ now.Resources?.CpuPercent ?? '—' }}<small>%</small></b> de processador</span>
                  <span><b class="num">{{ databaseSessions() }}</b> sessões</span>
                  <span><b class="num" [class.bad]="databaseBlocked()">{{ databaseBlocked() }}</b> bloqueadas</span>
                </span>
              } @else {
                <span class="muted none">Aguardando a primeira leitura.</span>
              }
            }
          </a>
        }
      </section>

      <h2 class="section">Mensageria</h2>

      @if (overview(); as now) {
        <section class="grid stats">
          <zap-stat label="Publicadas" icon="bolt" [value]="now.rates.published | num: 'decimal'" unit="/s" [hint]="(now.totals.published | num: 'compact') + ' desde o início'" />
          <zap-stat label="Entregues" icon="speed" tone="info" [value]="now.rates.delivered | num: 'decimal'" unit="/s" [hint]="(now.totals.delivered | num: 'compact') + ' desde o início'" />
          <zap-stat label="Pendentes" icon="hourglass_empty" [tone]="now.pending > 0 ? 'warn' : 'neutral'" [value]="now.pending | num" [hint]="now.withoutConsumer > 0 ? now.withoutConsumer + ' fila(s) sem consumidor' : 'aguardando entrega'" />
          <zap-stat label="Em processamento" icon="pending" tone="info" [value]="now.processing | num" hint="entregues e não confirmadas" />
          <zap-stat label="Mensagens mortas" icon="skull" [tone]="dead() > 0 ? 'danger' : 'ok'" [value]="dead() | num" [hint]="deadHint()" />
          <zap-stat label="Filas" icon="stacks" tone="neutral" [value]="now.queues | num" [hint]="now.paused > 0 ? now.paused + ' pausada(s)' : 'ativas agora'" />
          <zap-stat label="Aplicações conectadas" icon="lan" tone="ok" [value]="now.applications | num" [hint]="now.connections + ' conexões v2 · ' + now.v1Clients + ' cliente(s) v1'" />
        </section>
      } @else {
        <section class="grid stats">
          @for (placeholder of [1, 2, 3, 4, 5, 6, 7]; track placeholder) {
            <div class="panel skeleton"></div>
          }
        </section>
      }

      <section class="panel">
        <div class="panel-head">
          <h2>Vazão</h2>
          <span class="hint">mensagens por segundo</span>
          <span class="spacer"></span>
          <mat-button-toggle-group [value]="range()" (change)="pick($event.value)" hideSingleSelectionIndicator aria-label="Período">
            <mat-button-toggle value="hour">Última hora</mat-button-toggle>
            <mat-button-toggle value="day">24 horas</mat-button-toggle>
          </mat-button-toggle-group>
        </div>
        <div class="panel-body">
          <zap-time-chart [series]="throughput()" unit="/s" />
        </div>
      </section>

      <div class="grid two">
        <section class="panel">
          <div class="panel-head">
            <h2>Fila de espera</h2>
            <span class="hint">pendentes e em processamento</span>
          </div>
          <div class="panel-body">
            <zap-time-chart [series]="backlog()" />
          </div>
        </section>

        <section class="panel">
          <div class="panel-head">
            <h2>Filas mais carregadas</h2>
            <span class="spacer"></span>
            <a routerLink="/filas">ver todas</a>
          </div>
          @if (busiest().length) {
            <ul class="ranking">
              @for (queue of busiest(); track queue.name) {
                <li>
                  <a [routerLink]="['/filas', queue.name]" class="truncate">{{ queue.name }}</a>
                  <span class="bar"><span [style.width.%]="queue.share"></span></span>
                  <span class="num">{{ queue.pending | num }}</span>
                  @if (queue.consumers === 0) {
                    <span class="pill warn">sem consumidor</span>
                  } @else if (queue.paused) {
                    <span class="pill info">pausada</span>
                  } @else {
                    <span class="pill">{{ queue.consumers }} consumidor{{ queue.consumers === 1 ? '' : 'es' }}</span>
                  }
                </li>
              }
            </ul>
          } @else {
            <div class="empty">
              <mat-icon svgIcon="check_circle" />
              <strong>Nenhuma fila com mensagens esperando</strong>
              <span>Tudo o que chegou foi entregue.</span>
            </div>
          }
        </section>
      </div>
    </div>
  `,
  styles: `
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .section { margin: 8px 0 -4px; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-label-large); letter-spacing: 0.06em; text-transform: uppercase; }

    .attention { padding: 14px 18px; border-color: color-mix(in srgb, var(--zap-danger) 45%, transparent); }
    .attention.calm { border-color: var(--zap-border); }
    .attention .title { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; color: var(--zap-danger); }
    .attention.calm .title { color: var(--zap-ok); }
    .attention .title .muted { font: var(--mat-sys-body-small); }
    .attention ul { list-style: none; margin: 10px 0 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr)); gap: 2px 16px; }
    .attention li a { display: flex; align-items: center; gap: 10px; padding: 8px; border-radius: 10px; color: inherit; text-decoration: none; }
    .attention li a:hover { background: var(--mat-sys-surface-container-high); }
    .attention li mat-icon { width: 18px; height: 18px; margin-left: auto; flex: none; color: var(--mat-sys-on-surface-variant); }

    .blocks { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 300px), 1fr)); gap: var(--zap-gap); }
    .block { display: grid; align-content: start; gap: 14px; padding: 18px 20px; color: inherit; text-decoration: none !important; transition: transform 0.15s, border-color 0.15s; }
    .block:hover { transform: translateY(-1px); border-color: var(--mat-sys-primary); }
    .block .name { display: flex; align-items: center; gap: 10px; font: var(--mat-sys-title-medium); }
    .block .name > mat-icon:first-child { color: var(--mat-sys-primary); }
    .block .name small { font: var(--mat-sys-body-small); }
    .block .go { margin-left: auto; color: var(--mat-sys-on-surface-variant); }
    .block .figures { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
    .block .figures span { display: grid; gap: 2px; min-width: 0; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-small); }
    .block .figures b { color: var(--mat-sys-on-surface); font: var(--mat-sys-title-large); font-weight: 650; letter-spacing: -0.02em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .block .figures b small { margin-left: 4px; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-label-medium); letter-spacing: 0; }
    .block .figures b.bad { color: var(--zap-danger); }
    .block .none { font: var(--mat-sys-body-medium); }
    .skeleton { height: 82px; opacity: 0.5; }
    .ranking { list-style: none; margin: 0; padding: 8px 20px 16px; display: grid; gap: 4px; }
    .ranking li {
      display: grid;
      grid-template-columns: minmax(0, 1.4fr) minmax(40px, 1fr) auto auto;
      align-items: center;
      gap: 12px;
      padding: 8px 0;
    }
    .bar { height: 8px; border-radius: 999px; background: var(--mat-sys-surface-container-high); overflow: hidden; }
    .bar span { display: block; height: 100%; border-radius: inherit; background: linear-gradient(90deg, #6d5efc, #22c1dc); }
    @media (max-width: 520px) {
      .ranking li { grid-template-columns: minmax(0, 1fr) auto; }
      .ranking .bar { grid-column: 1 / -1; order: 3; }
      .ranking .pill { display: none; }
    }
  `,
})
export class OverviewPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly live = inject(Live);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly overview = this.live.overview;
  protected readonly range = signal<Range>('hour');

  // The rest of the environment, as the Worker Control tells it. Each part stands without the others.
  protected readonly workers = signal<WorkerControlStatus | null>(null);
  protected readonly workersProblem = signal('');
  protected readonly traffic = signal<TrafficSummary | null>(null);
  private readonly frontends = signal<{ Frontends: WebApplication[]; Publications: WebPublication[] } | null>(null);
  protected readonly database = signal<DatabaseState | null>(null);
  protected readonly databaseSessions = computed(() => (this.database()?.Sessions ?? []).reduce((total, group) => total + group.Sessions, 0));
  protected readonly databaseBlocked = computed(() => (this.database()?.Activity ?? []).filter((item) => item.BlockedBy > 0).length);
  private environmentTimer: ReturnType<typeof setInterval> | null = null;

  protected readonly processes = computed(() => {
    const groups = this.workers()?.Groups ?? [];
    return {
      desired: groups.reduce((total, group) => total + group.DesiredWorkers, 0),
      up: groups.reduce((total, group) => total + group.Workers.filter((worker) => worker.State === 'Up').length, 0),
      unstable: groups.filter((group) => group.Unstable).length,
    };
  });

  protected readonly services = computed(() => {
    const services = this.workers()?.Services ?? [];
    return { all: services.length, running: services.filter((service) => service.State === 'Running').length };
  });

  protected readonly perMinute = computed(() => {
    const totals = this.traffic()?.Totals;
    return totals ? DECIMAL.format(totals.Count / 60) : '—';
  });

  protected readonly web = computed(() => {
    const answer = this.frontends();
    if (!answer) {
      return null;
    }
    const modules = answer.Frontends.flatMap((application) => application.Modules);
    const week = Date.now() - 7 * 86400000;
    return {
      modules: modules.length,
      up: modules.filter((module) => module.State === 'Up').length,
      latest: modules.map((module) => module.PublishedAt).filter((at): at is string => !!at).sort().pop() ?? null,
      published: answer.Publications.filter((publication) => publication.Kind !== 'first-seen' && Date.parse(publication.At) >= week).length,
    };
  });

  /** Everything, anywhere in the environment, that is not as it should be: the worst first. */
  protected readonly concerns = computed<Concern[]>(() => {
    const concerns: Concern[] = [];
    const broker = this.overview();
    if (broker?.withoutConsumer) {
      concerns.push({ tone: 'danger', text: `${broker.withoutConsumer} ${broker.withoutConsumer === 1 ? 'fila com mensagens e sem consumidor' : 'filas com mensagens e sem consumidor'}`, link: '/filas' });
    }
    if (this.workersProblem()) {
      concerns.push({ tone: 'danger', text: 'O Worker Control não está respondendo', link: '/workers' });
    }
    for (const group of this.workers()?.Groups ?? []) {
      const up = group.Workers.filter((worker) => worker.State === 'Up').length;
      if (group.Unstable) {
        concerns.push({ tone: 'danger', text: `Grupo ${group.Name} instável: os processos caem logo depois de iniciar`, link: '/workers' });
      } else if (group.Enabled && up < group.DesiredWorkers) {
        concerns.push({ tone: 'warn', text: `Grupo ${group.Name} com ${up} de ${group.DesiredWorkers} processos no ar`, link: '/workers' });
      }
    }
    for (const service of this.workers()?.Services ?? []) {
      const name = service.DisplayName || service.Name;
      if (service.State === 'Missing') {
        concerns.push({ tone: 'danger', text: `Serviço ${name} não está instalado`, link: '/workers/servicos' });
      } else if (service.Unstable) {
        concerns.push({ tone: 'danger', text: `Serviço ${name} instável`, link: '/workers/servicos' });
      } else if (service.State === 'Stopped' && (service.ExitCode ?? 0) !== 0) {
        concerns.push({ tone: 'danger', text: `Serviço ${name} caiu (código ${service.ExitCode})`, link: '/workers/servicos' });
      } else if (service.Check?.Ok === false) {
        concerns.push({ tone: 'danger', text: `Serviço ${name} rodando, mas ${service.Check.Target} não responde`, link: '/workers/servicos' });
      }
    }
    const traffic = this.traffic();
    if (traffic?.Configured) {
      if (!traffic.Source.Found || traffic.Source.Problem) {
        concerns.push({ tone: 'warn', text: 'O log de acesso do proxy não está sendo lido', link: '/trafego' });
      }
      // A handful of requests says little; a share of many does.
      if (traffic.Totals.Count >= 20 && traffic.Totals.S5 / traffic.Totals.Count >= 0.02) {
        concerns.push({ tone: 'danger', text: `${this.share(traffic.Totals.S5, traffic.Totals.Count)} das chamadas às APIs com erro do servidor na última hora`, link: '/trafego' });
      }
    }
    for (const application of this.frontends()?.Frontends ?? []) {
      if (application.Problem) {
        concerns.push({ tone: 'danger', text: `Aplicação web ${application.Name}: a pasta ou o manifesto não pôde ser lido`, link: '/web' });
      }
      const wrong = application.Modules.filter((module) => module.State !== 'Up');
      if (wrong.length) {
        concerns.push({ tone: 'danger', text: `${wrong.length === 1 ? 'Módulo ' + (wrong[0].Title || wrong[0].Name) + ' fora do ar ou incompleto' : wrong.length + ' módulos fora do ar ou incompletos'}`, link: '/web' });
      }
      if (application.Orphans.length) {
        concerns.push({ tone: 'warn', text: `${application.Orphans.length === 1 ? 'Uma pasta de módulo' : application.Orphans.length + ' pastas de módulo'} fora do manifesto`, link: '/web' });
      }
    }
    const database = this.database();
    if (database?.Configured) {
      if (database.Online === false) {
        concerns.push({ tone: 'danger', text: `Banco de dados ${database.Name} não responde`, link: '/banco' });
      }
      for (const alert of database.Alerts) {
        concerns.push({ tone: alert.Severity === 'danger' ? 'danger' : 'warn', text: alertText(alert), link: '/banco' });
      }
    }
    return concerns.sort((a, b) => (a.tone === b.tone ? 0 : a.tone === 'danger' ? -1 : 1));
  });

  /** How long ago in one unit only, to fit beside other figures: "5 min", "3 h", "12 d". */
  protected brief(moment: string | null): string {
    if (!moment) {
      return '—';
    }
    const minutes = Math.max(0, Math.round((Date.now() - Date.parse(moment)) / 60000));
    return minutes < 1 ? 'agora' : minutes < 60 ? `${minutes} min` : minutes < 48 * 60 ? `${Math.round(minutes / 60)} h` : `${Math.round(minutes / 1440)} d`;
  }

  protected ms(value: number | null): string {
    return value === null ? '—' : value >= 1000 ? `${DECIMAL.format(value / 1000)} s` : `${Math.round(value)} ms`;
  }

  protected share(part: number, whole: number): string {
    return whole ? DECIMAL.format((part / whole) * 100) + '%' : '0%';
  }
  private readonly points = signal<MetricsPoint[]>([]);

  protected readonly dead = computed(() => {
    const letters = this.overview()?.deadLetters;
    return letters ? letters.expired + letters.notConsumed + letters.unconfirmed : 0;
  });

  protected readonly deadHint = computed(() => {
    const letters = this.overview()?.deadLetters;
    if (!letters || this.dead() === 0) {
      return 'nenhuma guardada';
    }
    return [
      letters.unconfirmed ? `${letters.unconfirmed} não confirmada(s)` : '',
      letters.expired ? `${letters.expired} vencida(s)` : '',
      letters.notConsumed ? `${letters.notConsumed} não consumida(s)` : '',
    ].filter(Boolean).join(' · ');
  });

  protected readonly throughput = computed<Series[]>(() => {
    const points = this.points();
    const at = (point: MetricsPoint) => new Date(point.at).getTime();
    return [
      { name: 'Publicadas', color: '#6d5efc', area: true, data: points.map((point) => [at(point), point.publishedPerSecond]) },
      { name: 'Entregues', color: '#22c1dc', data: points.map((point) => [at(point), point.deliveredPerSecond]) },
      { name: 'Confirmadas', color: '#2fb380', data: points.map((point) => [at(point), point.confirmedPerSecond]) },
      { name: 'Mortas', color: '#e5484d', data: points.map((point) => [at(point), point.deadPerSecond]) },
    ];
  });

  protected readonly backlog = computed<Series[]>(() => {
    const points = this.points();
    const at = (point: MetricsPoint) => new Date(point.at).getTime();
    return [
      { name: 'Pendentes', color: '#f5a524', area: true, data: points.map((point) => [at(point), point.pending]) },
      { name: 'Em processamento', color: '#22c1dc', data: points.map((point) => [at(point), point.processing]) },
    ];
  });

  protected readonly busiest = computed(() => {
    const loaded = this.live.queues().filter((queue) => queue.pending > 0).sort((a, b) => b.pending - a.pending).slice(0, 6);
    const most = loaded[0]?.pending ?? 1;
    return loaded.map((queue) => ({ ...queue, share: Math.max(4, (queue.pending / most) * 100) }));
  });

  ngOnInit(): void {
    this.load();
    this.timer = setInterval(() => this.load(), 10000);
    this.loadEnvironment();
    this.environmentTimer = setInterval(() => this.loadEnvironment(), 15000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
    if (this.environmentTimer) {
      clearInterval(this.environmentTimer);
    }
  }

  private loadEnvironment(): void {
    this.api.workerStatus().subscribe({
      next: (status) => {
        this.workers.set(status);
        this.workersProblem.set('');
      },
      error: (failure) => {
        if (failure.status !== 401) {
          this.workers.set(null);
          this.workersProblem.set(failure.error?.error ?? 'Sem resposta do Worker Control.');
        }
      },
    });
    // An older Worker Control knows nothing of these two; the blocks say so by staying empty.
    forkJoin({
      traffic: this.api.traffic({ minutes: 60, kind: 'api' }).pipe(catchError(() => of(null))),
      frontends: this.api.frontends().pipe(catchError(() => of(null))),
      database: this.api.database().pipe(catchError(() => of(null))),
    }).subscribe(({ traffic, frontends, database }) => {
      this.database.set(database);
      this.traffic.set(traffic);
      this.frontends.set(frontends);
    });
  }

  protected pick(range: Range): void {
    this.range.set(range);
    this.load();
  }

  private load(): void {
    this.api.series(this.range()).subscribe({ next: (series) => this.points.set(series.points), error: () => undefined });
  }
}
