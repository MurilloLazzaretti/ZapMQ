import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { forkJoin } from 'rxjs';
import { Api } from '../core/api';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { TrafficError, TrafficRoute, TrafficSummary, TrafficTally } from '../core/models';
import { Series, TimeChart } from '../shared/chart';
import { Stat } from '../shared/stat';

type Kind = 'api' | 'static' | '';
type Sort = 'count' | 'slow' | 'errors' | 'worse';

const PERIODS = [
  { minutes: 60, label: '1 h' },
  { minutes: 1440, label: '24 h' },
  { minutes: 10080, label: '7 dias' },
  { minutes: 43200, label: '30 dias' },
];

const DECIMAL = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });
const WHOLE = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

/**
 * What goes through the reverse proxy: how much, how fast and with how many errors, in all
 * and for each application, endpoint and instance. Read by the Worker Control from the access
 * log; nothing is asked of the applications.
 */
@Component({
  selector: 'zap-traffic',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, Stat, TimeChart, NumPipe, WhenPipe, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './traffic.html',
  styleUrl: './traffic.scss',
})
export class TrafficPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private timer: ReturnType<typeof setInterval> | null = null;
  private typing: ReturnType<typeof setTimeout> | null = null;

  protected readonly periods = PERIODS;
  protected readonly minutes = signal(60);
  protected readonly kind = signal<Kind>('api');
  /** The application the screen is narrowed to; empty for all of them. */
  protected readonly app = signal('');
  protected readonly sort = signal<Sort>('count');
  protected readonly search = signal('');

  protected readonly summary = signal<TrafficSummary | null>(null);
  protected readonly routes = signal<TrafficRoute[]>([]);
  protected readonly errors = signal<TrafficError[]>([]);
  protected readonly problem = signal('');
  protected readonly outdated = signal(false);
  protected readonly loading = signal(true);

  protected readonly totals = computed(() => this.summary()?.Totals ?? null);

  /** Requests per minute, in the whole period. */
  protected readonly perMinute = computed(() => {
    const totals = this.totals();
    return totals ? totals.Count / this.minutes() : 0;
  });

  protected readonly requests = computed<Series[]>(() => {
    const summary = this.summary();
    if (!summary) {
      return [];
    }
    // Per minute whatever the step, so that the periods can be compared.
    const perMinute = 60 / summary.StepSeconds;
    const point = (value: (item: TrafficTally) => number) => summary.Series.map((item) => [Date.parse(item.At), Math.round(value(item) * perMinute * 10) / 10] as [number, number]);
    return [
      { name: 'Requisições', color: '#6d5efc', area: true, data: point((item) => item.Count) },
      { name: 'Erros do cliente (4xx)', color: '#e0a02d', data: point((item) => item.S4) },
      { name: 'Erros do servidor (5xx)', color: '#e5484d', data: point((item) => item.S5) },
    ];
  });

  protected readonly latency = computed<Series[]>(() => {
    const summary = this.summary();
    if (!summary) {
      return [];
    }
    const point = (value: (item: TrafficTally) => number | null) =>
      summary.Series.map((item) => [Date.parse(item.At), value(item)] as [number, number | null]).filter((item): item is [number, number] => item[1] !== null);
    return [
      { name: 'Mediana', color: '#2fb380', data: point((item) => item.P50) },
      { name: 'P95', color: '#22c1dc', area: true, data: point((item) => item.P95) },
    ];
  });

  protected readonly apps = computed(() => {
    const apps = this.summary()?.Apps ?? [];
    const most = Math.max(1, ...apps.map((item) => item.Count));
    return apps.map((item) => ({ ...item, share: (item.Count / most) * 100 }));
  });

  /** The instances behind the proxy, application by application, with the share each one took. */
  protected readonly upstreams = computed(() => {
    const all = this.summary()?.Upstreams ?? [];
    const total = new Map<string, number>();
    for (const item of all) {
      total.set(item.App, (total.get(item.App) ?? 0) + item.Count);
    }
    return all.map((item, index) => ({
      ...item,
      first: index === 0 || all[index - 1].App !== item.App,
      share: (item.Count / Math.max(1, total.get(item.App) ?? 1)) * 100,
      alone: all.filter((other) => other.App === item.App).length === 1,
    }));
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 15000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
    if (this.typing) {
      clearTimeout(this.typing);
    }
  }

  protected ms(value: number | null): string {
    return value === null ? '—' : value >= 1000 ? `${DECIMAL.format(value / 1000)} s` : `${(value >= 100 ? WHOLE : DECIMAL).format(value)} ms`;
  }

  protected rate(value: number): string {
    return (value >= 100 ? WHOLE : DECIMAL).format(value);
  }

  protected percent(part: number, whole: number): string {
    return whole ? DECIMAL.format((part / whole) * 100) + '%' : '0%';
  }

  /** How much slower or faster than yesterday at the same time; empty when there is no telling. */
  protected change(route: TrafficRoute): { text: string; tone: string } | null {
    if (route.P95 === null || route.PreviousP95 === null || (route.PreviousCount ?? 0) < 5 || route.Count < 5) {
      return null;
    }
    const difference = route.P95 - route.PreviousP95;
    if (Math.abs(difference) < Math.max(5, route.PreviousP95 * 0.15)) {
      return { text: 'igual a ontem', tone: '' };
    }
    return { text: `${difference > 0 ? '+' : '−'}${this.ms(Math.abs(difference))} que ontem`, tone: difference > 0 ? 'warn' : 'ok' };
  }

  protected choose<T>(target: { set(value: T): void }, value: T): void {
    target.set(value);
    this.loading.set(true);
    this.refresh();
  }

  protected typed(text: string): void {
    this.search.set(text);
    if (this.typing) {
      clearTimeout(this.typing);
    }
    this.typing = setTimeout(() => this.refresh(), 350);
  }

  private refresh(): void {
    const filter = { minutes: this.minutes(), kind: this.kind(), app: this.app() };
    forkJoin({
      summary: this.api.traffic(filter),
      routes: this.api.trafficRoutes({ ...filter, search: this.search(), sort: this.sort(), limit: 60 }),
      errors: this.api.trafficErrors({ app: this.app(), limit: 30 }),
    }).subscribe({
      next: ({ summary, routes, errors }) => {
        this.summary.set(summary);
        this.routes.set(routes.Routes);
        this.errors.set(errors.Errors);
        this.problem.set('');
        this.outdated.set(false);
        this.loading.set(false);
      },
      error: (failure: HttpErrorResponse) => {
        this.loading.set(false);
        if (failure.status === 501) {
          this.outdated.set(true);
        } else if (failure.status !== 401) {
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }
}
