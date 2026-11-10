import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { endpointKey } from '../core/openapi';
import { kindIcon, kindName, size } from '../core/database';
import { diff } from '../core/diff';
import { Auth } from '../core/auth';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { ApplicationDefaults, EndpointUse, EnvironmentCheck, InstallChoice, ItemCheck, PackageDetail, PackageItem, PackageItemDetail, PackageResult } from '../core/models';
import { Transport, groupItems, historyText, itemAction, itemTone, statusName, statusTone, targetIcon, targetName } from '../core/transport';
import { confirm } from '../shared/dialogs';
import { InstallForm } from '../shared/install-form';
import { PackageSend } from '../shared/package-send';
import { DiffView } from '../shared/diff-view';

/** How an item stands against this environment, in the words of the panel. */
const STATES: Record<string, { text: string; tone: string; hint: string }> = {
  new: { text: 'novo aqui', tone: 'ok', hint: 'O objeto não existe neste ambiente e será criado.' },
  same: { text: 'já está igual', tone: '', hint: 'O que o pacote traz é o que já está aqui.' },
  clean: { text: 'altera', tone: 'info', hint: 'O objeto aqui está como estava na origem antes da mudança.' },
  changes: { text: 'altera', tone: 'info', hint: 'O objeto existe aqui e é diferente do que o pacote traz. Não se sabe de que versão a mudança partiu.' },
  conflict: { text: 'diferente do ponto de partida', tone: 'warn', hint: 'O objeto aqui não é o que a origem tinha antes da mudança: alguém o alterou aqui, ou falta um pacote anterior.' },
  blocked: { text: 'já existe: precisa de script', tone: 'danger', hint: 'Uma tabela ou um type que já existe não é recriado. A mudança precisa vir como um script de alteração.' },
  drops: { text: 'será apagado', tone: 'danger', hint: 'O objeto existe aqui e será apagado.' },
  absent: { text: 'já não existe', tone: '', hint: 'O objeto a apagar não existe aqui: nada será feito.' },
  script: { text: 'script', tone: 'primary', hint: 'Roda como está, dentro de uma transação.' },
  ran: { text: 'já rodou aqui', tone: '', hint: 'Este mesmo script rodou neste ambiente numa versão anterior deste pacote: ele é pulado, não roda de novo.' },
  unknown: { text: 'não foi possível comparar', tone: 'warn', hint: '' },
  missing: { text: 'não existe aqui', tone: 'danger', hint: 'Este ambiente não tem esse alvo, e o pacote não diz como instalá-lo (veio de uma versão anterior, ou o tipo não é instalado pelo transporte).' },
  mismatch: { text: 'tipo diferente', tone: 'danger', hint: 'Aqui a aplicação web com esse nome é de outro tipo (monolito ou micro frontends). Um tipo nunca vira o outro: cadastre a aplicação do outro tipo com outro nome e transporte para ela.' },
  install: { text: 'será instalado', tone: 'primary', hint: 'Este ambiente não tem essa aplicação: ela é criada aqui com o que for informado em Instalar aqui.' },
};

const FORCING = 'O que não parar no tempo de espera terá o processo encerrado à força: o que ele estiver fazendo nesse momento é interrompido.';

/**
 * One package: what it carries, how it stands against this environment, the decision to be
 * taken about it and what happened when it was applied.
 */
@Component({
  selector: 'zap-transport-package',
  imports: [FormsModule, RouterLink, NgTemplateOutlet, MatButtonModule, MatIconModule, MatTooltipModule, DiffView, AgoPipe, NumPipe, WhenPipe, PackageSend, InstallForm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-package.html',
  styleUrl: './transport-package.scss',
})
export class TransportPackagePage implements OnInit, OnDestroy {
  protected readonly endpointKey = endpointKey;
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly transport = inject(Transport);
  private timer: ReturnType<typeof setInterval> | null = null;
  private id = '';

  protected readonly detail = signal<PackageDetail | null>(null);
  protected readonly auth = inject(Auth);

  /** The items by who put them in and by kind, or in the order they are applied; as chosen last in this browser. */
  protected readonly byPerson = signal(remembered(SEEING) !== 'order');
  protected readonly groups = computed(() => groupItems(this.detail()?.items ?? [], (item) => item.addedBy, this.detail()?.package.releases ?? []));

  protected seeByPerson(yes: boolean): void {
    this.byPerson.set(yes);
    try {
      localStorage.setItem(SEEING, yes ? 'person' : 'order');
    } catch {
      // Only a convenience.
    }
  }

  protected mine(user: string): boolean {
    return user.toLowerCase() === (this.auth.user() ?? '').toLowerCase();
  }

  /** All the items of a person may go on: theirs, or, for the master, somebody else's. */
  protected async release(user: string): Promise<void> {
    await this.releasing(() => this.api.releasePackage(this.id, this.mine(user) ? null : user));
  }

  protected async withdraw(user: string): Promise<void> {
    await this.releasing(() => this.api.withdrawPackage(this.id, this.mine(user) ? null : user));
  }

  private async releasing(ask: () => ReturnType<Api['releasePackage']>): Promise<void> {
    this.busy.set(true);
    try {
      this.detail.set(await firstValueFrom(ask()));
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }
  protected readonly checks = signal<ItemCheck[] | null>(null);
  protected readonly checkProblem = signal('');
  protected readonly problem = signal('');
  protected readonly missing = signal(false);
  protected readonly busy = signal(false);
  /** The item whose content is open, and what was read of it. */
  protected readonly open = signal<number | null>(null);
  /** What this environment does not have and the package says how to install, and how it was said to be. */
  protected readonly installing = computed(() => {
    const numbers = new Set((this.checks() ?? []).filter((check) => check.state === 'install').map((check) => check.number));
    return (this.detail()?.items ?? []).filter((item) => numbers.has(item.number) && item.install);
  });
  protected readonly defaults = signal<ApplicationDefaults | null>(null);
  private installs: Record<number, InstallChoice> = {};
  /** The configuration files of the environment that were chosen to be replaced, by "item|path". Nothing is replaced unless chosen. */
  protected readonly replacing = signal<Record<string, boolean>>({});
  /** Whether the package is waiting for somebody to decide it here, which is when the files of the environment can be chosen. */
  protected readonly deciding = computed(() => {
    const package_ = this.detail()?.package;
    return !!package_ && (package_.status === 'Pending' || package_.status === 'Reverted' || (package_.status === 'Closed' && !package_.received));
  });
  protected readonly content = signal<PackageItemDetail | null>(null);
  protected readonly scheduling = signal(false);
  protected readonly refusing = signal(false);
  protected when = '';
  /** What does not stop in time is ended by force, instead of the item failing. */
  protected force = false;
  protected reason = '';

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly size = size;
  protected readonly statusName = statusName;
  protected readonly statusTone = statusTone;
  protected readonly itemAction = itemAction;
  protected readonly itemTone = itemTone;
  protected readonly historyText = historyText;
  protected readonly targetName = targetName;
  protected readonly targetIcon = targetIcon;

  protected readonly status = computed(() => this.detail()?.package.status ?? null);

  /** True where forcing has a meaning: something that runs is stopped, and there is still something to ask for. */
  protected readonly forcible = computed(() => {
    const detail = this.detail();
    return !!detail && detail.items.some((item) => ['worker', 'service', 'api'].includes(item.kind))
      && ['Closed', 'Pending', 'Failed', 'Reverted', 'Applied', 'Partial', 'RevertedPartly', 'Paused'].includes(detail.package.status);
  });

  /** A package of which nothing was applied, for sure, can be asked for again. */
  protected readonly retriable = computed(() => this.detail()?.results.every((result) => result.status === 'failed') ?? false);

  /** What somebody should know before approving: conflicts, what cannot be applied, what is missing. */
  protected readonly warnings = computed(() => {
    const checks = this.checks() ?? [];
    return {
      blocked: checks.filter((check) => check.state === 'blocked' || check.state === 'missing' || check.state === 'mismatch' || check.missing.length > 0).length,
      conflicts: checks.filter((check) => check.state === 'conflict').length,
      drops: checks.filter((check) => check.state === 'drops').length,
      missing: checks.filter((check) => check.missing.length > 0).length,
    };
  });

  /** The two sides of the open item: what is here against what comes, or what was here against what is here now. */
  protected readonly compared = computed(() => {
    const content = this.content();
    if (!content || content.item.kind !== 'database') {
      return null;
    }
    const result = this.result(content.item.number);
    const done = result?.status === 'applied' && result.reverted !== 'reverted';
    const before = done ? content.previous : content.current;
    const after = content.item.action === 'Drop' ? null : content.script;
    const both = before !== null && after !== null;
    const lines = diff(before ?? '', after ?? '');
    return {
      both,
      lines,
      added: lines.filter((line) => line.kind === '+').length,
      removed: lines.filter((line) => line.kind === '-').length,
      title: content.item.action === 'Script' ? 'O script' : both ? (done ? 'O que mudou aqui' : 'O que vai mudar aqui') : before !== null ? (done ? 'O que foi apagado' : 'O que será apagado') : 'O que será criado',
    };
  });

  ngOnInit(): void {
    // From one version to another of the same package, the screen stays and what it shows changes.
    this.route.paramMap.subscribe((parameters) => {
      this.id = parameters.get('id') ?? '';
      this.detail.set(null);
      this.checks.set(null);
      this.load(true);
    });
    this.timer = setInterval(() => ['Approved', 'Applying', 'Reverting', 'Paused'].includes(this.status() ?? '') && this.load(false), 2000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected state(item: PackageItem): { text: string; tone: string; hint: string } | null {
    const check = this.checks()?.find((candidate) => candidate.number === item.number);
    if (!check) {
      return null;
    }
    if (item.kind !== 'database' && check.state === 'changes') {
      // How much of it changes, which for a folder says more than "changes".
      const count = (amount: number, one: string, many: string) => (amount ? `${amount} ${amount === 1 ? one : many}` : '');
      const parts = [count(check.added, 'novo', 'novos'), count(check.changed, 'alterado', 'alterados'), count(check.removed, 'removido', 'removidos')].filter(Boolean);
      return { text: parts.join(' · '), tone: 'info', hint: `Aqui está na versão ${check.currentFingerprint ?? 'desconhecida'}. O alvo é parado, a pasta é guardada, os arquivos são trocados e ele é iniciado de novo.` };
    }
    return { ...STATES[check.state], hint: check.problem ?? STATES[check.state].hint };
  }

  /** The files of the open item that are not the same here, the ones that go away last. */
  protected readonly fileChanges = computed(() => {
    const changes = this.content()?.changes ?? [];
    const rank = { changed: 0, added: 1, removed: 2, same: 3 } as const;
    return {
      shown: changes.filter((change) => change.state !== 'same').sort((a, b) => rank[a.state] - rank[b.state] || a.path.localeCompare(b.path)),
      same: changes.filter((change) => change.state === 'same').length,
    };
  });

  /** The configuration files of the environment an item brings, against the ones here. */
  protected environmentOf(item: PackageItem): EnvironmentCheck[] {
    return this.checks()?.find((candidate) => candidate.number === item.number)?.environment ?? [];
  }

  protected isReplacing(item: PackageItem, path: string): boolean {
    return !!this.replacing()[`${item.number}|${path}`];
  }

  protected replace(item: PackageItem, path: string, on: boolean): void {
    this.replacing.update((chosen) => ({ ...chosen, [`${item.number}|${path}`]: on }));
  }

  /** What was chosen to be replaced, by item, among the files that still differ. */
  private chosenEnvironment(): Record<number, string[]> {
    const chosen: Record<number, string[]> = {};
    for (const check of this.checks() ?? []) {
      const paths = check.environment.filter((file) => file.state === 'different' && this.replacing()[`${check.number}|${file.path}`]).map((file) => file.path);
      if (paths.length) {
        chosen[check.number] = paths;
      }
    }
    return chosen;
  }

  /** The configuration files that will be put here without being asked, because the application has none. */
  private createdEnvironment(): string[] {
    return (this.checks() ?? []).flatMap((check) => check.environment.filter((file) => file.state === 'new').map((file) => file.path));
  }

  protected lacking(item: PackageItem): string[] {
    return this.checks()?.find((candidate) => candidate.number === item.number)?.missing ?? [];
  }

  protected result(number: number): PackageResult | null {
    return this.detail()?.results.find((candidate) => candidate.number === number) ?? null;
  }

  protected toggle(item: PackageItem): void {
    if (this.open() === item.number) {
      this.open.set(null);
      return;
    }
    this.open.set(item.number);
    this.content.set(null);
    this.api.packageItem(this.id, item.number).subscribe({ next: (content) => this.open() === item.number && this.content.set(content), error: (failure) => this.say(failure) });
  }

  protected download(): void {
    const link = document.createElement('a');
    link.href = `api/transport/packages/${this.id}/download`;
    link.click();
    setTimeout(() => this.load(false), 1500);
  }

  protected async approve(scheduled: boolean): Promise<void> {
    const warnings = this.warnings();
    const environment = this.chosenEnvironment();
    const replacements = Object.values(environment).flat();
    const created = this.createdEnvironment();
    const notes = [
      warnings.blocked ? `${warnings.blocked} item(ns) não podem ser aplicados como estão e vão falhar` : '',
      warnings.conflicts ? `${warnings.conflicts} item(ns) diferentes do ponto de partida` : '',
      warnings.missing ? `${warnings.missing} item(ns) usam algo que não existe aqui nem vem antes no pacote` : '',
      warnings.drops ? `${warnings.drops} objeto(s) serão apagados` : '',
      this.installing().length ? `${this.installing().length} aplicação(ões) serão instaladas aqui` : '',
      replacements.length ? `${replacements.length} arquivo(s) de ambiente serão substituídos pelos do pacote (${replacements.join(', ')}); o que veio em branco, como senhas, precisa ser preenchido depois, e o anterior fica na cópia guardada` : '',
      created.length ? `${created.length} arquivo(s) de ambiente não existem aqui e serão criados (${created.join(', ')}): são arquivos que podem variar de um ambiente para o outro, confira o conteúdo` : '',
    ].filter(Boolean);
    const at = scheduled ? new Date(this.when) : null;
    if (scheduled && (!this.when || Number.isNaN(at!.getTime()))) {
      return;
    }
    const sure = await confirm(this.dialog, {
      title: scheduled ? 'Agendar a aplicação?' : 'Aplicar agora?',
      message: `${this.detail()!.items.length} item(ns) serão aplicados em ${this.detail()!.environment}${scheduled ? ' em ' + at!.toLocaleString('pt-BR') : ''}. Se um item falhar, a aplicação para ali e nada é desfeito sozinho.`,
      warning: [...notes.map((note) => note + '.'), this.forcing() ? FORCING : ''].filter(Boolean).join(' ') || undefined,
      action: scheduled ? 'Agendar' : 'Aplicar',
      danger: notes.length > 0 || this.forcing(),
    });
    if (sure) {
      const installs = Object.fromEntries(this.installing().map((item) => [item.number, this.installs[item.number]]));
      await this.act(this.api.approvePackage(this.id, at ? at.toISOString() : null, this.forcing(), this.installing().length ? installs : undefined, replacements.length ? environment : undefined));
      this.scheduling.set(false);
    }
  }

  /** The package stopped at an item that went wrong: the ones behind it go on, or the application ends here. */
  protected async decide(proceed: boolean): Promise<void> {
    await this.act(this.api.decidePackage(this.id, proceed));
  }

  /** Somebody knows it went wrong: it stops being counted in the menu. */
  protected async acknowledge(): Promise<void> {
    await this.act(this.api.acknowledgePackage(this.id));
  }

  private forcing(): boolean {
    return this.force && this.forcible();
  }

  /** Puts back what the package did here, after saying what goes back and what does not. */
  protected async revert(): Promise<void> {
    this.busy.set(true);
    let steps;
    try {
      steps = (await firstValueFrom(this.api.revertPlan(this.id))).steps;
    } catch (failure) {
      this.say(failure);
      return;
    } finally {
      this.busy.set(false);
    }
    const going = steps.filter((step) => step.state !== 'cannot');
    const staying = steps.filter((step) => step.state === 'cannot');
    const changed = steps.filter((step) => step.state === 'changed');
    const name = (number: number) => {
      const item = this.detail()!.items.find((candidate) => candidate.number === number)!;
      return `${number} (${item.action === 'Script' ? item.title : item.name})`;
    };
    if (!going.length) {
      this.snack.open(`Nada do que este pacote fez pode ser revertido pelo painel. ${staying.map((step) => `Item ${name(step.number)}: ${step.reason}`).join(' ')}`, 'Fechar', { duration: 15000 });
      return;
    }
    const notes = [
      ...(this.forcing() ? [FORCING] : []),
      ...changed.map((step) => `Item ${name(step.number)}: ${step.reason}.`),
      ...staying.map((step) => `Item ${name(step.number)} não volta: ${step.reason}.`),
    ];
    const sure = await confirm(this.dialog, {
      title: 'Reverter o pacote?',
      message: `${going.length === 1 ? 'O item ' + name(going[0].number) + ' volta' : going.length + ' itens voltam'} a ser como ${going.length === 1 ? 'era' : 'eram'} em ${this.detail()!.environment} antes deste pacote, do último aplicado para o primeiro. Aplicações são paradas e iniciadas de novo; os arquivos de configuração do ambiente ficam como estão agora. Se um item falhar, a reversão para ali.`,
      warning: notes.length ? notes.join(' ') : undefined,
      action: 'Reverter',
      danger: true,
    });
    if (sure) {
      await this.act(this.api.revertPackage(this.id, this.forcing()));
    }
  }

  /** Only for what was made here: it goes away, file and all. */
  protected async remove(): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Excluir o pacote?',
      message: `A versão ${this.detail()!.package.version} de "${this.detail()!.package.name}" é apagada deste ambiente, com o arquivo, e volta a ser um pacote aberto, com os itens que levava.`,
      warning: 'Se o arquivo já foi levado a outro ambiente, lá ele continua existindo.',
      action: 'Excluir',
      danger: true,
    });
    if (!sure) {
      return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.deletePackage(this.id));
      this.transport.refresh();
      const reopened = (await firstValueFrom(this.api.drafts())).drafts.find((draft) => draft.line === this.detail()!.package.line);
      void this.router.navigate(reopened ? ['/transporte/aberto', reopened.id] : ['/transporte/pacotes']);
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  protected async reject(): Promise<void> {
    await this.act(this.api.rejectPackage(this.id, this.reason));
    this.refusing.set(false);
    this.reason = '';
  }

  private async act(request: ReturnType<Api['approvePackage']>): Promise<void> {
    this.busy.set(true);
    try {
      this.detail.set(await firstValueFrom(request));
      this.transport.refresh();
    } catch (failure) {
      this.say(failure);
      this.load(false);
    } finally {
      this.busy.set(false);
    }
  }

  /** The next version: it opens with everything this one carries, to be corrected and closed. */
  protected async nextVersion(): Promise<void> {
    this.busy.set(true);
    try {
      const draft = await firstValueFrom(this.api.newVersion(this.id));
      this.transport.choose(draft);
      void this.router.navigate(['/transporte/aberto', draft.id]);
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  private say(failure: unknown): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 8000 });
  }

  protected chosen(number: number, choice: InstallChoice): void {
    this.installs[number] = choice;
  }

  protected reload(): void {
    this.load(false);
  }

  /** By item of the database: the endpoints of this environment that use what it changes. */
  protected readonly affected = signal<Record<string, EndpointUse[]>>({});

  private load(first: boolean): void {
    if (first) {
      // Only where the APIs bring the map of their endpoints; without it the package is as it was.
      this.api.packageEndpoints(this.id).subscribe({ next: (answer) => this.affected.set(answer.items), error: () => undefined });
    }
    this.api.package(this.id).subscribe({
      next: (detail) => {
        const before = this.status();
        this.detail.set(detail);
        this.problem.set('');
        // Against what is here, while there is still a decision to take; and once more when it is over.
        if (first || (before !== detail.package.status && !['Approved', 'Applying', 'Reverting'].includes(detail.package.status))) {
          this.check();
          this.transport.refresh();
        }
      },
      error: (failure: HttpErrorResponse) => {
        if (failure.status === 404) {
          this.missing.set(true);
        } else if (failure.status !== 401) {
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }

  private check(): void {
    this.api.packageCheck(this.id).subscribe({
      next: (answer) => {
        this.checks.set(answer.checks);
        this.checkProblem.set('');
        if (answer.checks.some((check) => check.state === 'install') && !this.defaults()) {
          // What is usual on this machine, to suggest the folder and the names.
          this.api.applicationDefaults().subscribe({ next: (defaults) => this.defaults.set(defaults), error: () => this.defaults.set(null) });
        }
      },
      error: (failure: HttpErrorResponse) => {
        this.checks.set(null);
        this.checkProblem.set(failure.error?.error ?? 'Não foi possível comparar com este ambiente.');
      },
    });
  }
}

const SEEING = 'zapmq.transport.items';

function remembered(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}
