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
import { kindIcon, kindName, size } from '../core/database';
import { diff } from '../core/diff';
import { NumPipe, WhenPipe } from '../core/format';
import { ItemCheck, PackageDetail, PackageItem, PackageItemDetail, PackageResult } from '../core/models';
import { Transport, historyText, itemAction, itemTone, statusName, statusTone, targetIcon, targetName } from '../core/transport';
import { confirm } from '../shared/dialogs';
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
  unknown: { text: 'não foi possível comparar', tone: 'warn', hint: '' },
  missing: { text: 'não existe aqui', tone: 'danger', hint: 'Este ambiente não tem esse alvo. Instalar o que ainda não existe não é feito pelo transporte, por ora.' },
};

/**
 * One package: what it carries, how it stands against this environment, the decision to be
 * taken about it and what happened when it was applied.
 */
@Component({
  selector: 'zap-transport-package',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, DiffView, NumPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-package.html',
  styleUrl: './transport-package.scss',
})
export class TransportPackagePage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly transport = inject(Transport);
  private timer: ReturnType<typeof setInterval> | null = null;
  private id = '';

  protected readonly detail = signal<PackageDetail | null>(null);
  protected readonly checks = signal<ItemCheck[] | null>(null);
  protected readonly checkProblem = signal('');
  protected readonly problem = signal('');
  protected readonly missing = signal(false);
  protected readonly busy = signal(false);
  /** The item whose content is open, and what was read of it. */
  protected readonly open = signal<number | null>(null);
  protected readonly content = signal<PackageItemDetail | null>(null);
  protected readonly scheduling = signal(false);
  protected readonly refusing = signal(false);
  protected when = '';
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

  /** What somebody should know before approving: conflicts, what cannot be applied, what is missing. */
  protected readonly warnings = computed(() => {
    const checks = this.checks() ?? [];
    return {
      blocked: checks.filter((check) => check.state === 'blocked' || check.state === 'missing').length,
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
    this.id = this.route.snapshot.paramMap.get('id') ?? '';
    this.load(true);
    this.timer = setInterval(() => ['Approved', 'Applying', 'Reverting'].includes(this.status() ?? '') && this.load(false), 2000);
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
    const notes = [
      warnings.blocked ? `${warnings.blocked} item(ns) não podem ser aplicados como estão e vão falhar` : '',
      warnings.conflicts ? `${warnings.conflicts} item(ns) diferentes do ponto de partida` : '',
      warnings.missing ? `${warnings.missing} item(ns) usam algo que não existe aqui nem vem no pacote` : '',
      warnings.drops ? `${warnings.drops} objeto(s) serão apagados` : '',
    ].filter(Boolean);
    const at = scheduled ? new Date(this.when) : null;
    if (scheduled && (!this.when || Number.isNaN(at!.getTime()))) {
      return;
    }
    const sure = await confirm(this.dialog, {
      title: scheduled ? 'Agendar a aplicação?' : 'Aplicar agora?',
      message: `${this.detail()!.items.length} item(ns) serão aplicados no banco de ${this.detail()!.environment}${scheduled ? ' em ' + at!.toLocaleString('pt-BR') : ''}. Se um item falhar, a aplicação para ali e nada é desfeito sozinho.`,
      warning: notes.length ? notes.join('; ') + '.' : undefined,
      action: scheduled ? 'Agendar' : 'Aplicar',
      danger: notes.length > 0,
    });
    if (sure) {
      await this.act(this.api.approvePackage(this.id, at ? at.toISOString() : null));
      this.scheduling.set(false);
    }
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
      await this.act(this.api.revertPackage(this.id));
    }
  }

  /** Only for what was made here: it goes away, file and all. */
  protected async remove(): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Excluir o pacote?',
      message: `"${this.detail()!.package.name}" é apagado deste ambiente, com o arquivo. Os itens que ele levava voltam para a expedição.`,
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
      void this.router.navigate(['/transporte']);
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

  private say(failure: unknown): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 8000 });
  }

  private load(first: boolean): void {
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
      },
      error: (failure: HttpErrorResponse) => {
        this.checks.set(null);
        this.checkProblem.set(failure.error?.error ?? 'Não foi possível comparar com este ambiente.');
      },
    });
  }
}
