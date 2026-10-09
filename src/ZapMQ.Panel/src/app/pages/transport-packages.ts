import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { AgoPipe, WhenPipe } from '../core/format';
import { PackageSummary } from '../core/models';
import { Transport, statusName, statusTone } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/** The packages this environment knows: the ones made here and the ones that arrived. */
@Component({
  selector: 'zap-transport-packages',
  imports: [RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, TransportTabs, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Transporte</h1>
          <p class="muted">Os pacotes de <strong>{{ environment() || 'este ambiente' }}</strong>: os montados aqui e os que chegaram para ser aplicados.</p>
        </div>
        <span class="spacer"></span>
        <zap-transport-tabs />
      </header>

      @if (problem()) {
        <section class="panel empty"><mat-icon svgIcon="cloud_off" /><strong>Não foi possível ler os pacotes</strong><span>{{ problem() }}</span></section>
      } @else if (packages(); as list) {
        <section class="panel">
          <div class="panel-head">
            <h2>Pacotes</h2><span class="hint">{{ list.length }}</span>
            <span class="spacer"></span>
            <button mat-button (click)="file.click()" [disabled]="busy()"><mat-icon svgIcon="upload" /> Importar pacote</button>
            <input #file type="file" accept=".zpkg,.zip" hidden (change)="bring($event)" />
          </div>
          @if (list.length) {
            <div class="table-wrap">
              <table class="data stack">
                <thead><tr><th>Pacote</th><th>Origem</th><th class="right">Itens</th><th>Situação</th><th class="right">Última mudança</th></tr></thead>
                <tbody>
                  @for (item of list; track item.id) {
                    <tr class="clickable" [routerLink]="['/transporte/pacotes', item.id]" tabindex="0">
                      <td class="wide" data-label="Pacote"><strong>{{ item.name }}</strong>@if (item.description) { <div class="muted description">{{ item.description }}</div> }</td>
                      <td data-label="Origem">{{ item.origin }} <span class="muted">· {{ item.createdBy }}</span></td>
                      <td class="right num" data-label="Itens">{{ item.items }}</td>
                      <td data-label="Situação">
                        <span class="pill" [class]="'pill ' + statusTone(item.status)">{{ statusName(item.status) }}</span>
                        @if (item.status === 'Approved' && item.applyAt) { <span class="muted when">para {{ item.applyAt | when }}</span> }
                      </td>
                      <td class="right" data-label="Última mudança" [matTooltip]="item.changedAt | when">{{ item.changedAt | ago }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <div class="empty">
              <mat-icon svgIcon="inventory_2" />
              <strong>Nenhum pacote ainda</strong>
              <span>Monte um na <a routerLink="/transporte">expedição</a>, ou importe o arquivo de um pacote feito em outro ambiente.</span>
            </div>
          }
        </section>
      } @else {
        <section class="panel empty"><span>Lendo…</span></section>
      }
    </div>
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px 16px; flex-wrap: wrap; }
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .spacer { flex: 1; }
    tr.clickable { cursor: pointer; }
    tr.clickable:hover, tr.clickable:focus-visible { background: var(--mat-sys-surface-container-low); outline: none; }
    .description { max-width: 70ch; font-size: 12.5px; overflow-wrap: anywhere; }
    .when { margin-left: 6px; font-size: 12.5px; }
    td.right { white-space: nowrap; }
  `,
})
export class TransportPackagesPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  private readonly transport = inject(Transport);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly packages = signal<PackageSummary[] | null>(null);
  protected readonly environment = signal('');
  protected readonly problem = signal('');
  protected readonly busy = signal(false);

  protected readonly statusName = statusName;
  protected readonly statusTone = statusTone;

  ngOnInit(): void {
    this.load();
    this.timer = setInterval(() => this.load(), 15000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  /** The file of a package made in another environment. */
  protected async bring(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }
    this.busy.set(true);
    try {
      const received = await firstValueFrom(this.api.importPackage(file));
      this.transport.refresh();
      void this.router.navigate(['/transporte/pacotes', received.package.id]);
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O arquivo não pôde ser importado', 'Fechar', { duration: 9000 });
    } finally {
      this.busy.set(false);
    }
  }

  private load(): void {
    this.api.packages().subscribe({
      next: (answer) => {
        this.packages.set(answer.packages);
        this.environment.set(answer.environment);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }
}
