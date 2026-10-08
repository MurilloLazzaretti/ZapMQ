import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { NumPipe, SincePipe } from '../core/format';
import { Live } from '../core/live';
import { MetricsPoint } from '../core/models';
import { Series, TimeChart } from '../shared/chart';
import { Stat } from '../shared/stat';

type Range = 'hour' | 'day';

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
            <p class="muted">Serviço no ar há {{ now.startedAt | since }} · versão {{ now.version }}</p>
          }
        </div>
      </header>

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
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected pick(range: Range): void {
    this.range.set(range);
    this.load();
  }

  private load(): void {
    this.api.series(this.range()).subscribe({ next: (series) => this.points.set(series.points), error: () => undefined });
  }
}
