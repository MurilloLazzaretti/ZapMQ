import { QueueTabs } from '../shared/queue-tabs';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { NumPipe } from '../core/format';
import { Live } from '../core/live';
import { QueueRow } from '../core/models';
import { NewQueueDialog } from './new-queue';

type Column = 'name' | 'pending' | 'processing' | 'consumers' | 'published' | 'deadLetters';
type Scope = 'work' | 'all';

/** Queues that exist only to talk to one process: keep-alive, safe stop and trace of a worker. */
const INTERNAL = /^(\d+)(SS|TR)?$|^WorkerControl/;

@Component({
  selector: 'zap-queues',
  imports: [QueueTabs, FormsModule, RouterLink, MatFormFieldModule, MatInputModule, MatIconModule, MatButtonModule, MatButtonToggleModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Filas</h1>
          <p class="muted">{{ rows().length }} de {{ all().length }} · atualizado ao vivo</p>
        </div>
        <span class="spacer"></span>
        <zap-queue-tabs />
        <button mat-flat-button (click)="define()"><mat-icon svgIcon="tune" /> Definir fila</button>
      </header>

      <section class="panel">
        <div class="tools">
          <mat-form-field class="search">
            <mat-icon matPrefix svgIcon="search" />
            <input matInput placeholder="Buscar pelo nome" [ngModel]="filter()" (ngModelChange)="filter.set($event)" aria-label="Buscar fila" />
          </mat-form-field>
          <mat-button-toggle-group [value]="scope()" (change)="scope.set($event.value)" hideSingleSelectionIndicator aria-label="Quais filas">
            <mat-button-toggle value="work">De trabalho</mat-button-toggle>
            <mat-button-toggle value="all">Todas</mat-button-toggle>
          </mat-button-toggle-group>
        </div>

        @if (rows().length) {
          <div class="table-wrap">
            <table class="data stack">
              <thead>
                <tr>
                  @for (column of columns; track column.key) {
                    <th [class.right]="column.key !== 'name'" class="sortable" (click)="sortBy(column.key)">
                      {{ column.label }}
                      @if (sort().column === column.key) {
                        <mat-icon [svgIcon]="sort().ascending ? 'arrow_upward' : 'arrow_downward'" />
                      }
                    </th>
                  }
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (queue of rows(); track queue.name) {
                  <tr class="clickable" (click)="open(queue)">
                    <td class="wide">
                      <span class="name">
                        <a [routerLink]="['/filas', queue.name]" (click)="$event.stopPropagation()">{{ queue.name }}</a>
                        @if (queue.paused) {
                          <span class="pill info"><mat-icon svgIcon="pause" /> pausada</span>
                        }
                        @if (!queue.exists) {
                          <span class="pill">só definida</span>
                        } @else if (queue.defined) {
                          <span class="pill primary">definida</span>
                        }
                      </span>
                    </td>
                    <td class="right num" data-label="Pendentes" [class.hot]="queue.pending > 0">{{ queue.pending | num }}</td>
                    <td class="right num" data-label="Processando">{{ queue.processing | num }}</td>
                    <td class="right num" data-label="Consumidores">
                      @if (queue.consumers === 0 && queue.pending > 0) {
                        <span class="pill warn">nenhum</span>
                      } @else {
                        {{ queue.consumers | num }}
                      }
                    </td>
                    <td class="right num" data-label="Publicadas">{{ queue.published | num: 'compact' }}</td>
                    <td class="right num" data-label="Mortas">
                      @if (queue.deadLetters > 0) {
                        <span class="pill danger">{{ queue.deadLetters | num }}</span>
                      } @else {
                        <span class="muted">0</span>
                      }
                    </td>
                    <td class="right chevron"><mat-icon svgIcon="chevron_right" /></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <div class="empty">
            <mat-icon svgIcon="inbox" />
            <strong>{{ all().length ? 'Nenhuma fila com esse nome' : 'Nenhuma fila no momento' }}</strong>
            <span>Uma fila aparece aqui quando recebe a primeira mensagem ou quando alguém a define.</span>
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .head .spacer { flex: 1; }
    .tools { display: flex; gap: 12px; padding: 16px 20px; align-items: center; flex-wrap: wrap; }
    .search { flex: 1; min-width: min(100%, 220px); }
    .name { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-weight: 550; word-break: break-all; }
    .hot { color: var(--zap-warn); font-weight: 650; }
    .chevron { width: 40px; color: var(--mat-sys-on-surface-variant); }
    @media (max-width: 760px) { .chevron { display: none; } }
  `,
})
export class QueuesPage {
  private readonly live = inject(Live);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);

  protected readonly columns: { key: Column; label: string }[] = [
    { key: 'name', label: 'Fila' },
    { key: 'pending', label: 'Pendentes' },
    { key: 'processing', label: 'Processando' },
    { key: 'consumers', label: 'Consumidores' },
    { key: 'published', label: 'Publicadas' },
    { key: 'deadLetters', label: 'Mortas' },
  ];

  protected readonly filter = signal('');
  protected readonly scope = signal<Scope>('work');
  protected readonly sort = signal<{ column: Column; ascending: boolean }>({ column: 'name', ascending: true });
  protected readonly all = this.live.queues;

  protected readonly rows = computed(() => {
    const text = this.filter().trim().toLowerCase();
    const { column, ascending } = this.sort();
    const scope = this.scope();
    return this.all()
      .filter((queue) => (scope === 'all' || !INTERNAL.test(queue.name)) && (!text || queue.name.toLowerCase().includes(text)))
      .sort((a, b) => {
        const order = column === 'name' ? a.name.localeCompare(b.name, 'pt-BR', { numeric: true }) : a[column] - b[column];
        return (ascending ? order : -order) || a.name.localeCompare(b.name);
      });
  });

  protected sortBy(column: Column): void {
    this.sort.update((current) => ({ column, ascending: current.column === column ? !current.ascending : column === 'name' }));
  }

  protected open(queue: QueueRow): void {
    void this.router.navigate(['/filas', queue.name]);
  }

  protected define(): void {
    this.dialog.open(NewQueueDialog, { maxWidth: '480px', width: 'calc(100vw - 32px)' }).afterClosed().subscribe((name?: string) => {
      if (name) {
        void this.router.navigate(['/filas', name], { queryParams: { definir: 1 } });
      }
    });
  }
}
