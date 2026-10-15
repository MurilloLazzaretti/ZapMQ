import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { uploadProblem } from '../core/settings';
import { AgoPipe, WhenPipe } from '../core/format';
import { PackageDraft, PackageSummary } from '../core/models';
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
            <h2>Abertos</h2><span class="hint">sendo montados aqui; fechados, viram uma versão</span>
            <span class="spacer"></span>
            <button mat-flat-button (click)="create()"><mat-icon svgIcon="add" /> Novo pacote</button>
          </div>
          @if (drafts().length) {
            <div class="drafts">
              @for (draft of drafts(); track draft.id) {
                <a class="draft" [routerLink]="['/transporte/aberto', draft.id]" [class.current]="transport.current()?.id === draft.id">
                  <mat-icon svgIcon="edit_note" />
                  <span class="what"><strong>{{ draft.name }}</strong> <span class="pill primary">v{{ draft.version }}</span>
                    @if (transport.current()?.id === draft.id) { <span class="pill" matTooltip="Vem marcado quando o painel pergunta em qual pacote incluir">último escolhido</span> }
                    <span class="muted">{{ draft.items ?? 0 }} {{ draft.items === 1 ? 'item' : 'itens' }} · {{ draft.createdBy }} · {{ draft.createdAt | ago }}</span></span>
                </a>
              }
            </div>
          } @else {
            <p class="muted none">Nenhum pacote aberto. Para levar algo a outro ambiente, crie um pacote e inclua nele objetos do banco, scripts e aplicações.</p>
          }
        </section>

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
                      <td class="wide" data-label="Pacote">
                        <strong>{{ item.name }}</strong> <span class="pill primary">v{{ item.version }}</span>
                        @if (older()[item.line]; as before) { <span class="muted versions" [matTooltip]="'Versões anteriores aqui: ' + before.join(', ')">+{{ before.length }} {{ before.length === 1 ? 'versão anterior' : 'versões anteriores' }}</span> }
                        @if (item.description) { <div class="muted description">{{ item.description }}</div> }
                      </td>
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
              <span>Crie um pacote acima, ou importe o arquivo de um pacote feito em outro ambiente.</span>
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
    .drafts { display: grid; gap: 8px; padding: 0 20px 16px; }
    .draft { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border: 1px solid var(--zap-border); border-radius: 14px; color: inherit; text-decoration: none; }
    .draft:hover { background: var(--mat-sys-surface-container-low); }
    .draft.current { border-color: var(--mat-sys-primary); }
    .draft mat-icon { flex: none; color: var(--mat-sys-primary); }
    .draft .what { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; min-width: 0; }
    .draft .muted { font-size: 12.5px; }
    .none { margin: 0; padding: 0 20px 16px; }
    .versions { margin-left: 6px; font-size: 12.5px; }
  `,
})
export class TransportPackagesPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  protected readonly transport = inject(Transport);
  private timer: ReturnType<typeof setInterval> | null = null;

  /** Only the newest version of each package; the older ones are reached from it. */
  protected readonly packages = signal<PackageSummary[] | null>(null);
  /** By package: the older versions this environment has. */
  protected readonly older = signal<Record<string, string[]>>({});
  protected readonly drafts = signal<PackageDraft[]>([]);
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
      this.snack.open(uploadProblem(failure, 'O arquivo não pôde ser importado'), 'Fechar', { duration: 12000 });
    } finally {
      this.busy.set(false);
    }
  }

  /** A new package: it opens, and what is included from the screens goes in it. */
  protected async create(): Promise<void> {
    const draft = await this.transport.draftFor(true);
    if (draft) {
      void this.router.navigate(['/transporte/aberto', draft.id]);
    }
  }

  private load(): void {
    this.api.drafts().subscribe({ next: (answer) => this.drafts.set(answer.drafts), error: () => undefined });
    this.api.packages().subscribe({
      next: (answer) => {
        const newest = new Map<string, PackageSummary>();
        const older: Record<string, string[]> = {};
        for (const item of answer.packages) {
          const known = newest.get(item.line);
          if (!known || item.version > known.version) {
            if (known) {
              (older[item.line] ??= []).push('v' + known.version);
            }
            newest.set(item.line, item);
          } else {
            (older[item.line] ??= []).push('v' + item.version);
          }
        }
        this.older.set(older);
        this.packages.set(answer.packages.filter((item) => newest.get(item.line) === item));
        this.environment.set(answer.environment);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }
}
