import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, WritableSignal, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { uploadProblem } from '../core/settings';
import { AgoPipe, WhenPipe } from '../core/format';
import { Auth } from '../core/auth';
import { PackageDraft, PackageStatus, PackageSummary } from '../core/models';
import { TARGET_KINDS, Transport, statusName, statusTone, targetName } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/** The packages this environment knows: the ones made here and the ones that arrived. */
@Component({
  selector: 'zap-transport-packages',
  imports: [RouterLink, FormsModule, MatButtonModule, MatIconModule, MatTooltipModule, TransportTabs, AgoPipe, WhenPipe],
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
            @if (transport.packages()) {
              <button mat-flat-button (click)="create()"><mat-icon svgIcon="add" /> Novo pacote</button>
            }
          </div>
          @if (drafts().length) {
            <div class="drafts">
              @for (draft of drafts(); track draft.id) {
                <a class="draft" [routerLink]="['/transporte/aberto', draft.id]" [class.current]="transport.current()?.id === draft.id">
                  <mat-icon svgIcon="edit_note" />
                  <span class="what"><strong>{{ draft.name }}</strong> <span class="pill primary">v{{ draft.version }}</span>
                    @if (transport.current()?.id === draft.id) { <span class="pill" matTooltip="Vem marcado quando o painel pergunta em qual pacote incluir">último escolhido</span> }
                    <span class="muted">{{ draft.items ?? 0 }} {{ draft.items === 1 ? 'item' : 'itens' }} · {{ draft.createdBy }} · {{ draft.createdAt | ago }}</span>
                    @if (draft.contributors?.length) {
                      <span class="muted">· liberado por {{ draft.releases.length }} de {{ draft.contributors!.length }}</span>
                    }</span>
                </a>
              }
            </div>
          } @else if (transport.packages()) {
            <p class="muted none">Nenhum pacote aberto. Para levar algo a outro ambiente, crie um pacote e inclua nele objetos do banco, scripts e aplicações.</p>
          } @else {
            <p class="muted none">Neste ambiente não se criam pacotes: os que chegam de outros ambientes são aprovados, aplicados e seguem adiante.</p>
          }
        </section>

        <section class="panel">
          <div class="panel-head">
            <h2>Pacotes</h2><span class="hint">{{ filtered() ? shown().length + ' de ' + list.length : list.length }}</span>
            <span class="spacer"></span>
            <button mat-button (click)="file.click()" [disabled]="busy()"><mat-icon svgIcon="upload" /> Importar pacote</button>
            <input #file type="file" accept=".zpkg,.zip" hidden (change)="bring($event)" />
          </div>
          @if (list.length) {
            <div class="tools">
              <label class="search">
                <mat-icon svgIcon="search" />
                <input type="search" placeholder="Nome, descrição, item (dbo.spX, brief), pessoa, origem, v3…" [ngModel]="search()" (ngModelChange)="set(search, $event)" aria-label="Buscar pacote" />
              </label>
              <select class="pick" [ngModel]="state()" (ngModelChange)="set(state, $event)" aria-label="Situação">
                <option value="">Qualquer situação</option>
                @for (group of states; track group.value) { <option [value]="group.value">{{ group.label }}</option> }
              </select>
              <select class="pick" [ngModel]="kind()" (ngModelChange)="set(kind, $event)" aria-label="Tipo de item">
                <option value="">Qualquer tipo de item</option>
                @for (item of kinds; track item) { <option [value]="item">{{ kindName(item) }}</option> }
              </select>
              <select class="pick" [ngModel]="days()" (ngModelChange)="set(days, $event)" aria-label="Período">
                @for (item of periods; track item.days) { <option [value]="item.days">{{ item.label }}</option> }
              </select>
            </div>
            <div class="views" role="group" aria-label="Atalhos">
              @for (item of views; track item.value) {
                <button type="button" class="chip" [class.on]="view() === item.value" [attr.aria-pressed]="view() === item.value" (click)="set(view, view() === item.value ? '' : item.value)" [matTooltip]="item.hint">
                  {{ item.label }} @if (counts()[item.value]; as count) { <span class="count">{{ count }}</span> }
                </button>
              }
              @if (filtered()) { <button type="button" class="clear" (click)="clear()"><mat-icon svgIcon="close" /> Limpar filtros</button> }
            </div>
          }
          @if (list.length && !shown().length) {
            <div class="empty">
              <mat-icon svgIcon="search" />
              <strong>Nenhum pacote com esses filtros</strong>
              <button mat-button (click)="clear()">Limpar filtros</button>
            </div>
          } @else if (list.length) {
            <div class="table-wrap">
              <table class="data stack">
                <thead><tr><th>Pacote</th><th>Origem</th><th class="right">Itens</th><th>Situação</th><th class="right">Última mudança</th></tr></thead>
                <tbody>
                  @for (item of shown(); track item.id) {
                    <tr class="clickable" [routerLink]="['/transporte/pacotes', item.id]" tabindex="0">
                      <td class="wide" data-label="Pacote">
                        <strong>{{ item.name }}</strong> <span class="pill primary">v{{ item.version }}</span>
                        @if (older()[item.line]; as before) { <span class="muted versions" [matTooltip]="'Versões anteriores aqui: ' + before.join(', ')">+{{ before.length }} {{ before.length === 1 ? 'versão anterior' : 'versões anteriores' }}</span> }
                        @if (item.description) { <div class="muted description">{{ item.description }}</div> }
                        @if (matched(item); as found) { <div class="muted found"><mat-icon svgIcon="search" /> contém {{ found }}</div> }
                      </td>
                      <td data-label="Origem">{{ item.origin }} <span class="muted">· {{ item.createdBy }}</span>
                        @if (item.contributors.length > 1) { <div class="muted people" [matTooltip]="'Incluíram itens: ' + item.contributors.join(', ')">{{ item.contributors.length }} pessoas</div> }
                      </td>
                      <td class="right num" data-label="Itens">{{ item.items }}</td>
                      <td data-label="Situação">
                        <span class="pill" [class]="'pill ' + statusTone(item.status)">{{ statusName(item.status) }}</span>
                        @if (item.status === 'Approved' && item.applyAt) { <span class="muted when">para {{ item.applyAt | when }}</span> }
                        @if (item.waiting.length && item.status !== 'Superseded') {
                          <div><span class="pill warn" [matTooltip]="'Só segue a outro ambiente quando todos liberarem. Falta: ' + item.waiting.join(', ')">aguardando liberação · {{ item.waiting.join(', ') }}</span></div>
                        }
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
    .tools { display: flex; align-items: center; gap: 8px 12px; flex-wrap: wrap; padding: 0 20px 10px; }
    .search { display: flex; align-items: center; gap: 8px; flex: 1 1 260px; max-width: 480px; padding: 0 14px; height: 40px; border-radius: 999px; background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface-variant); }
    .search mat-icon { width: 20px; height: 20px; flex: none; }
    .search input { flex: 1; min-width: 0; border: none; outline: none; background: transparent; color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium); }
    .search:focus-within { outline: 2px solid var(--mat-sys-primary); }
    .pick { height: 40px; max-width: 100%; padding: 0 12px; border: 1px solid var(--zap-border); border-radius: 999px; background: var(--zap-surface); color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium); }
    .views { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; padding: 0 20px 14px; }
    .chip { display: inline-flex; align-items: center; gap: 6px; height: 32px; padding: 0 12px; border: 1px solid var(--zap-border); border-radius: 999px; background: transparent; color: var(--mat-sys-on-surface); font: var(--mat-sys-label-large); cursor: pointer; }
    .chip:hover { background: var(--mat-sys-surface-container-low); }
    .chip.on { border-color: var(--mat-sys-primary); background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .chip .count { min-width: 18px; padding: 0 6px; border-radius: 999px; background: var(--mat-sys-surface-container-highest); font-size: 11.5px; text-align: center; }
    .clear { display: inline-flex; align-items: center; gap: 4px; border: none; background: none; color: var(--mat-sys-primary); font: var(--mat-sys-label-large); cursor: pointer; }
    .clear mat-icon { width: 18px; height: 18px; }
    .found { display: flex; align-items: center; gap: 4px; font-size: 12.5px; overflow-wrap: anywhere; }
    .found mat-icon { width: 14px; height: 14px; flex: none; }
    .people { font-size: 12.5px; }
  `,
})
export class TransportPackagesPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(Auth);
  protected readonly transport = inject(Transport);
  private timer: ReturnType<typeof setInterval> | null = null;

  // ── What the list is narrowed to, kept in the address so that it can be shared and comes back ──
  protected readonly search = signal('');
  protected readonly state = signal('');
  protected readonly kind = signal('');
  protected readonly days = signal('0');
  protected readonly view = signal('');

  protected readonly states: { value: string; label: string; statuses: PackageStatus[] }[] = [
    { value: 'closed', label: 'Fechado (montado aqui)', statuses: ['Closed'] },
    { value: 'pending', label: 'Aguardando aprovação', statuses: ['Pending'] },
    { value: 'scheduled', label: 'Aprovado ou aplicando', statuses: ['Approved', 'Applying'] },
    { value: 'applied', label: 'Aplicado', statuses: ['Applied'] },
    { value: 'problem', label: 'Com problema', statuses: ['Partial', 'Failed', 'RevertedPartly'] },
    { value: 'reverted', label: 'Revertido', statuses: ['Reverting', 'Reverted', 'RevertedPartly'] },
    { value: 'rejected', label: 'Recusado ou substituído', statuses: ['Rejected', 'Superseded'] },
  ];
  protected readonly kinds = ['database', ...TARGET_KINDS];
  protected readonly periods = [
    { days: '0', label: 'Qualquer data' },
    { days: '1', label: 'Últimas 24 horas' },
    { days: '7', label: 'Últimos 7 dias' },
    { days: '30', label: 'Últimos 30 dias' },
    { days: '90', label: 'Últimos 90 dias' },
  ];
  protected readonly views = [
    { value: 'attention', label: 'Pedem atenção', hint: 'Aguardando aprovação, com problema sem ciência, ou esperando a sua liberação' },
    { value: 'release', label: 'Esperam minha liberação', hint: 'Montados aqui, com itens seus que você ainda não liberou' },
    { value: 'mine', label: 'Com meus itens', hint: 'Em que você incluiu itens, montou ou aprovou' },
    { value: 'blocked', label: 'Aguardando liberação', hint: 'Montados aqui que ainda esperam a liberação de alguém para seguir' },
    { value: 'here', label: 'Montados aqui', hint: 'Feitos neste ambiente' },
    { value: 'received', label: 'Recebidos', hint: 'Chegaram de outro ambiente' },
  ];

  protected readonly filtered = computed(() => !!(this.search().trim() || this.state() || this.kind() || this.days() !== '0' || this.view()));

  /** How many packages each shortcut would show, with the other filters as they are. */
  protected readonly counts = computed(() => {
    const list = this.packages() ?? [];
    const others = list.filter((item) => this.passes(item, false));
    return Object.fromEntries(this.views.map((view) => [view.value, others.filter((item) => this.inView(item, view.value)).length]));
  });

  protected readonly shown = computed(() => (this.packages() ?? []).filter((item) => this.passes(item, true)));

  protected kindName(kind: string): string {
    return kind === 'database' ? 'Banco de dados' : targetName(kind, true);
  }

  protected set(which: WritableSignal<string>, value: string): void {
    which.set(value ?? '');
    this.remember();
  }

  protected clear(): void {
    this.search.set('');
    this.state.set('');
    this.kind.set('');
    this.days.set('0');
    this.view.set('');
    this.remember();
  }

  private remember(): void {
    void this.router.navigate([], {
      queryParams: { busca: this.search().trim() || null, situacao: this.state() || null, tipo: this.kind() || null, dias: this.days() !== '0' ? this.days() : null, ver: this.view() || null },
      replaceUrl: true,
    });
  }

  private me(): string {
    return (this.auth.user() ?? '').toLowerCase();
  }

  private inView(item: PackageSummary, view: string): boolean {
    const me = this.me();
    const has = (users: string[]) => users.some((user) => user.toLowerCase() === me);
    switch (view) {
      case 'attention':
        return item.status === 'Pending' || (['Partial', 'Failed', 'RevertedPartly'].includes(item.status) && !item.acknowledgedBy) || (has(item.waiting) && item.status !== 'Superseded');
      case 'release':
        return has(item.waiting) && item.status !== 'Superseded';
      case 'mine':
        return has(item.contributors) || item.createdBy.toLowerCase() === me || (item.approvedBy ?? '').toLowerCase() === me;
      case 'blocked':
        return item.waiting.length > 0 && item.status !== 'Superseded';
      case 'here':
        return !item.received;
      case 'received':
        return item.received;
      default:
        return true;
    }
  }

  /**
   * Whether a package is kept by the filters. Every word of the search has to be somewhere in it:
   * its name, description, origin, the people of it, the names of its items, its version (v3) or
   * the beginning of its identity.
   */
  private passes(item: PackageSummary, withView: boolean): boolean {
    if (withView && this.view() && !this.inView(item, this.view())) {
      return false;
    }
    const group = this.states.find((candidate) => candidate.value === this.state());
    if (group && !group.statuses.includes(item.status)) {
      return false;
    }
    if (this.kind() && !item.kinds.includes(this.kind())) {
      return false;
    }
    const days = Number(this.days());
    if (days > 0 && Date.parse(item.changedAt) < Date.now() - days * 86400000) {
      return false;
    }
    const words = fold(this.search()).split(/\s+/).filter((word) => word.length);
    if (!words.length) {
      return true;
    }
    const text = fold([item.name, item.description, item.origin, item.createdBy, item.approvedBy ?? '', ...item.contributors, ...item.names, 'v' + item.version, item.id, statusName(item.status)].join(' \n '));
    return words.every((word) => text.includes(word));
  }

  /** The items whose names the search found, when it was by them that the package was kept. */
  protected matched(item: PackageSummary): string {
    const words = fold(this.search()).split(/\s+/).filter((word) => word.length >= 2);
    if (!words.length) {
      return '';
    }
    const found = item.names.filter((name) => words.some((word) => fold(name).includes(word)));
    return found.length ? found.slice(0, 3).join(', ') + (found.length > 3 ? ` e mais ${found.length - 3}` : '') : '';
  }

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
    const query = this.route.snapshot.queryParamMap;
    this.search.set(query.get('busca') ?? '');
    this.state.set(query.get('situacao') ?? '');
    this.kind.set(query.get('tipo') ?? '');
    this.days.set(query.get('dias') ?? '0');
    this.view.set(query.get('ver') ?? '');
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

/** Without case or accents, to find "Liberação" by "liberacao". */
function fold(text: string): string {
  return text.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
}
