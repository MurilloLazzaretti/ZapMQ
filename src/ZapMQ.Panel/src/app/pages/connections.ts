import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { AgoPipe, NumPipe, SincePipe } from '../core/format';
import { Connection, V1Client } from '../core/models';
import { WorkerTabs } from '../shared/worker-tabs';

/** Queues that exist only to talk to one process. */
const INTERNAL = /^(\d+)(SS|TR)?$|^WorkerControl\.Canary/;

interface Application {
  name: string;
  host: string;
  wrapper: string;
  instances: { pid: number; connections: Connection[]; busy: boolean; since: string }[];
  queues: string[];
}

/** Who is connected: applications over v2, grouped by name, and whoever still talks v1. */
@Component({
  selector: 'zap-connections',
  imports: [FormsModule, RouterLink, WorkerTabs, MatFormFieldModule, MatInputModule, MatIconModule, MatTooltipModule, NumPipe, AgoPipe, SincePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Conexões</h1>
          <p class="muted">{{ applications().length }} aplicações em {{ processes() }} processos · {{ v2().length }} conexões v2 · {{ v1().length }} cliente(s) v1</p>
        </div>
        <span class="spacer"></span>
        <zap-worker-tabs />
      </header>

      <div class="head">
        <mat-form-field class="search">
          <mat-icon matPrefix svgIcon="search" />
          <input matInput placeholder="Buscar aplicação ou fila" [ngModel]="filter()" (ngModelChange)="filter.set($event)" aria-label="Buscar" />
        </mat-form-field>
      </div>

      @if (shown().length) {
        <section class="cards">
          @for (application of shown(); track application.name + application.host) {
            <article class="panel app">
              <header>
                <span class="avatar">{{ application.name.slice(0, 1).toUpperCase() }}</span>
                <div class="who">
                  <strong class="truncate">{{ application.name }}</strong>
                  <span class="muted truncate">{{ application.host }} · {{ application.wrapper }}</span>
                </div>
                <span class="pill ok">{{ application.instances.length }} {{ application.instances.length === 1 ? 'processo' : 'processos' }}</span>
              </header>

              <div class="queues">
                @for (queue of application.queues; track queue) {
                  <a class="pill primary" [routerLink]="['/filas', queue]">{{ queue }}</a>
                } @empty {
                  <span class="muted">Só publica; não consome nenhuma fila.</span>
                }
              </div>

              <ul class="instances">
                @for (instance of application.instances; track instance.pid) {
                  <li>
                    <span class="dot" [class.ok]="!instance.busy" [class.warn]="instance.busy" [matTooltip]="instance.busy ? 'Processando uma mensagem' : 'Livre'"></span>
                    <span class="mono">pid {{ instance.pid }}</span>
                    <span class="muted">{{ instance.connections.length }} {{ instance.connections.length === 1 ? 'conexão' : 'conexões' }}</span>
                    <span class="muted nowrap">há {{ instance.since | since }}</span>
                  </li>
                }
              </ul>
            </article>
          }
        </section>
      } @else {
        <section class="panel empty">
          <mat-icon svgIcon="lan" />
          <strong>{{ v2().length ? 'Nada com esse nome' : 'Nenhuma aplicação conectada por v2' }}</strong>
          <span>Uma aplicação aparece aqui quando usa o wrapper 2.0.</span>
        </section>
      }

      @if (v1().length) {
        <section class="panel">
          <div class="panel-head">
            <h2>Clientes v1</h2>
            <span class="hint">O protocolo antigo não diz quem é a aplicação; só o endereço de onde ela fala.</span>
          </div>
          <div class="table-wrap">
            <table class="data stack">
              <thead>
                <tr>
                  <th>Endereço</th>
                  <th>Visto</th>
                  <th>Consome</th>
                  <th>Publica em</th>
                </tr>
              </thead>
              <tbody>
                @for (client of v1(); track client.address) {
                  <tr>
                    <td class="mono" data-label="Endereço">{{ client.address }}</td>
                    <td class="nowrap" data-label="Visto">{{ client.lastSeen | ago }}</td>
                    <td class="wide" data-label="Consome">
                      <span class="chips">
                        @for (queue of work(client.consumes); track queue) {
                          <a class="pill" [routerLink]="['/filas', queue]">{{ queue }}</a>
                        }
                        @if (internal(client.consumes); as hidden) {
                          <span class="pill" matTooltip="Filas de keep-alive, parada e trace de processos">+{{ hidden | num }} internas</span>
                        }
                        @if (!client.consumes.length) {
                          <span class="muted">—</span>
                        }
                      </span>
                    </td>
                    <td class="wide" data-label="Publica em">
                      <span class="chips">
                        @for (queue of work(client.publishes); track queue) {
                          <a class="pill" [routerLink]="['/filas', queue]">{{ queue }}</a>
                        }
                        @if (internal(client.publishes); as hidden) {
                          <span class="pill" matTooltip="Filas de keep-alive, parada e trace de processos">+{{ hidden | num }} internas</span>
                        }
                        @if (!client.publishes.length) {
                          <span class="muted">—</span>
                        }
                      </span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }
    </div>
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .head .spacer { flex: 1; }
    .search { width: min(100%, 320px); }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr)); gap: var(--zap-gap); }
    .app { padding: 18px 20px; display: grid; gap: 14px; align-content: start; }
    .app header { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: 12px; }
    .avatar {
      width: 40px;
      height: 40px;
      border-radius: 12px;
      display: grid;
      place-items: center;
      font-weight: 700;
      color: #fff;
      background: linear-gradient(135deg, #6d5efc, #22c1dc);
    }
    .who { display: grid; min-width: 0; }
    .who .muted { font: var(--mat-sys-body-small); }
    .queues, .chips { display: flex; gap: 6px; flex-wrap: wrap; }
    .queues a, .chips a { text-decoration: none; }
    .instances { list-style: none; margin: 0; padding: 0; display: grid; }
    .instances li {
      display: grid;
      grid-template-columns: auto auto minmax(0, 1fr) auto;
      align-items: center;
      gap: 10px;
      padding: 8px 0;
      border-top: 1px solid var(--zap-border);
      font: var(--mat-sys-body-small);
    }
  `,
})
export class ConnectionsPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly v2 = signal<Connection[]>([]);
  protected readonly v1 = signal<V1Client[]>([]);
  protected readonly filter = signal('');

  protected readonly applications = computed<Application[]>(() => {
    const byName = new Map<string, Application>();
    for (const connection of this.v2()) {
      const key = `${connection.application}@${connection.host}`;
      let application = byName.get(key);
      if (!application) {
        application = { name: connection.application, host: connection.host, wrapper: connection.wrapper, instances: [], queues: [] };
        byName.set(key, application);
      }
      let instance = application.instances.find((item) => item.pid === connection.pid);
      if (!instance) {
        instance = { pid: connection.pid, connections: [], busy: false, since: connection.connectedAt };
        application.instances.push(instance);
      }
      instance.connections.push(connection);
      instance.busy ||= connection.busy;
      if (connection.connectedAt < instance.since) {
        instance.since = connection.connectedAt;
      }
      for (const queue of connection.queues) {
        if (!INTERNAL.test(queue) && !application.queues.includes(queue)) {
          application.queues.push(queue);
        }
      }
    }
    return [...byName.values()]
      .map((application) => ({ ...application, queues: application.queues.sort(), instances: application.instances.sort((a, b) => a.pid - b.pid) }))
      .sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
  });

  protected readonly processes = computed(() => this.applications().reduce((total, application) => total + application.instances.length, 0));

  protected readonly shown = computed(() => {
    const text = this.filter().trim().toLowerCase();
    return text
      ? this.applications().filter((application) => application.name.toLowerCase().includes(text) || application.queues.some((queue) => queue.toLowerCase().includes(text)))
      : this.applications();
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 4000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected work(queues: string[]): string[] {
    return queues.filter((queue) => !INTERNAL.test(queue));
  }

  protected internal(queues: string[]): number {
    return queues.filter((queue) => INTERNAL.test(queue)).length;
  }

  private refresh(): void {
    this.api.connections().subscribe({
      next: (connections) => {
        this.v2.set(connections.v2);
        this.v1.set(connections.v1);
      },
      error: () => undefined,
    });
  }
}
