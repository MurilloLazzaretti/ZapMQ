import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { Api } from '../core/api';
import { WhenPipe } from '../core/format';
import { WorkerEvent } from '../core/models';
import { WorkerTabs } from '../shared/worker-tabs';

/** What each kind of event is called on screen, and how much attention it asks for. */
export const KINDS: Record<string, { label: string; tone: string }> = {
  ServiceStarted: { label: 'Worker Control iniciado', tone: 'info' },
  ServiceStopped: { label: 'Worker Control parado', tone: 'info' },
  ConfigApplied: { label: 'Configuração aplicada', tone: '' },
  ConfigRefused: { label: 'Configuração recusada', tone: 'danger' },
  ManualAction: { label: 'Ação manual', tone: 'primary' },
  WorkerStarted: { label: 'Processo iniciado', tone: '' },
  WorkerStartFailed: { label: 'Falha ao iniciar', tone: 'danger' },
  WorkerAdopted: { label: 'Processo reconhecido', tone: '' },
  WorkerUp: { label: 'No ar', tone: 'ok' },
  WorkerCrashed: { label: 'Caiu', tone: 'danger' },
  WorkerHung: { label: 'Travou', tone: 'danger' },
  WorkerStartTimedOut: { label: 'Não respondeu ao iniciar', tone: 'danger' },
  SafeStopRequested: { label: 'Parada pedida', tone: '' },
  SafeStopTimedOut: { label: 'Não parou no prazo', tone: 'warn' },
  WorkerStopped: { label: 'Parou', tone: '' },
  WorkerKilled: { label: 'Encerrado à força', tone: 'warn' },
  GroupUnstable: { label: 'Grupo instável', tone: 'danger' },
  GroupStable: { label: 'Grupo estabilizou', tone: 'ok' },
  GroupRemoved: { label: 'Grupo removido', tone: '' },
  BoostStarted: { label: 'Boost iniciou', tone: 'primary' },
  BoostEnded: { label: 'Boost terminou', tone: '' },
  ScaleChanged: { label: 'Escala pela fila', tone: 'primary' },
  RecycleStarted: { label: 'Substituição iniciada', tone: 'info' },
  RecycleFinished: { label: 'Substituição concluída', tone: 'info' },
  FrontendPublished: { label: 'Publicação', tone: 'primary' },
  FrontendDown: { label: 'Módulo fora do ar', tone: 'danger' },
  FrontendUp: { label: 'Módulo voltou', tone: 'ok' },
  MonitoredStarted: { label: 'Serviço rodando', tone: 'ok' },
  MonitoredStopped: { label: 'Serviço parou', tone: '' },
  MonitoredCrashed: { label: 'Serviço caiu', tone: 'danger' },
  MonitoredRestarting: { label: 'Reinício automático', tone: 'primary' },
  MonitoredStopTimedOut: { label: 'Serviço não parou no prazo', tone: 'warn' },
  MonitoredActionFailed: { label: 'Ação recusada pelo Windows', tone: 'danger' },
  MonitoredCheckFailed: { label: 'Verificação falhou', tone: 'danger' },
  MonitoredCheckRecovered: { label: 'Verificação voltou', tone: 'ok' },
};

@Component({
  selector: 'zap-worker-events',
  imports: [MatFormFieldModule, MatSelectModule, MatIconModule, WorkerTabs, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Histórico</h1>
          <p class="muted">O que aconteceu com os processos, do mais recente para o mais antigo.</p>
        </div>
        <span class="spacer"></span>
        <zap-worker-tabs />
      </header>

      <section class="panel">
        <div class="tools">
          <mat-form-field>
            <mat-label>Grupo ou serviço</mat-label>
            <mat-select [value]="group()" (selectionChange)="group.set($event.value); load()">
              <mat-option value="">Todos</mat-option>
              @for (name of groups(); track name) {
                <mat-option [value]="name">{{ origin(name) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Tipo</mat-label>
            <mat-select [value]="kind()" (selectionChange)="kind.set($event.value); load()">
              <mat-option value="">Todos</mat-option>
              @for (item of kinds; track item.key) {
                <mat-option [value]="item.key">{{ item.label }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field class="short">
            <mat-label>Mostrar</mat-label>
            <mat-select [value]="limit()" (selectionChange)="limit.set($event.value); load()">
              <mat-option [value]="100">100</mat-option>
              <mat-option [value]="300">300</mat-option>
              <mat-option [value]="1000">1.000</mat-option>
            </mat-select>
          </mat-form-field>
        </div>

        @if (problem()) {
          <div class="empty">
            <mat-icon svgIcon="cloud_off" />
            <strong>Sem resposta do Worker Control</strong>
            <span>{{ problem() }}</span>
          </div>
        } @else if (events().length) {
          <div class="table-wrap">
            <table class="data stack">
              <thead>
                <tr>
                  <th>Quando</th>
                  <th>O quê</th>
                  <th>Grupo ou serviço</th>
                  <th class="right">Processo</th>
                  <th>Detalhe</th>
                </tr>
              </thead>
              <tbody>
                @for (event of events(); track event.Id) {
                  <tr>
                    <td class="nowrap num" data-label="Quando">{{ event.At | when }}</td>
                    <td data-label="O quê"><span class="pill" [class]="describe(event).tone">{{ describe(event).label }}</span></td>
                    <td data-label="Grupo">{{ origin(event.Group) }}</td>
                    <td class="right mono" data-label="Processo">{{ event.ProcessId ?? '—' }}</td>
                    <td class="wide detail" data-label="Detalhe">{{ event.Detail }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <div class="empty">
            <mat-icon svgIcon="history" />
            <strong>Nada registrado com esse filtro</strong>
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px 16px; flex-wrap: wrap; }
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .head .spacer { flex: 1; }
    .tools { display: flex; gap: 12px; padding: 16px 20px; flex-wrap: wrap; }
    .tools mat-form-field { flex: 1 1 200px; max-width: 320px; }
    .tools .short { flex: 0 1 140px; }
    .detail { color: var(--mat-sys-on-surface-variant); word-break: break-word; }
  `,
})
export class WorkerEventsPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly kinds = Object.entries(KINDS).map(([key, value]) => ({ key, label: value.label })).sort((a, b) => a.label.localeCompare(b.label, 'pt-BR'));
  /** From the query string, when another screen sends here already filtered. */
  readonly grupo = input<string>();

  protected readonly group = signal('');
  protected readonly kind = signal('');
  protected readonly limit = signal(100);
  protected readonly events = signal<WorkerEvent[]>([]);
  protected readonly problem = signal('');
  private readonly known = signal<string[]>([]);
  protected readonly groups = computed(() => this.known());

  ngOnInit(): void {
    this.api.workerStatus().subscribe({ next: (status) => this.known.set([...status.Groups.map((item) => item.Name).sort(), ...(status.Services ?? []).map((item) => 'service:' + item.Name).sort()]), error: () => undefined });
    this.group.set(this.grupo() ?? '');
    this.load();
    this.timer = setInterval(() => this.load(), 5000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  /** A group as it is; a service without the mark that tells it from a group. */
  protected origin(group: string | null): string {
    return !group ? '—' : group.startsWith('service:') ? group.slice(8) + ' (serviço)' : group.startsWith('frontend:') ? group.slice(9) + ' (web)' : group;
  }

  protected describe(event: WorkerEvent) {
    return KINDS[event.Kind] ?? { label: event.Kind, tone: '' };
  }

  protected load(): void {
    this.api.workerEvents({ group: this.group(), kind: this.kind(), limit: this.limit() }).subscribe({
      next: (answer) => {
        this.events.set(answer.Events);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }
}
