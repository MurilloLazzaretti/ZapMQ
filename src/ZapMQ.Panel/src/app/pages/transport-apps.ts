import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { Observable, catchError, firstValueFrom, of } from 'rxjs';
import { Api } from '../core/api';
import { uploadProblem } from '../core/settings';
import { ask, confirm } from '../shared/dialogs';
import { MatDialog } from '@angular/material/dialog';
import { AgoPipe } from '../core/format';
import { AreaItem, TransportTarget, WebApplication } from '../core/models';
import { targetLabel, TARGET_KINDS, Transport, targetIcon, targetName } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/** A web application with its modules under it. */
interface WebGroup {
  name: string;
  /** The whole application of micro frontends, or the monolith; null for modules of no known application. */
  whole: TransportTarget | null;
  mode: 'mfe' | 'monolith';
  modules: TransportTarget[];
  /** How many modules it has, whatever the search shows. */
  total?: number;
  address: string | null;
  active: boolean | null;
}

/**
 * What runs on the machine of this environment and can be carried in a package: by kind, with
 * the ways a version of each gets into the area.
 */
@Component({
  selector: 'zap-transport-apps',
  imports: [NgTemplateOutlet, FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, TransportTabs, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-apps.html',
  styleUrl: './transport-apps.scss',
})
export class TransportAppsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly transport = inject(Transport);

  protected readonly targets = signal<TransportTarget[] | null>(null);
  protected readonly inboxes = signal<Record<string, string>>({});
  protected readonly unmatched = signal<string[]>([]);
  protected readonly problem = signal('');
  protected readonly busy = signal(false);
  protected readonly search = signal('');
  /** The kind being looked at; empty for all of them. */
  protected readonly kind = signal('');

  protected readonly kinds = TARGET_KINDS;
  protected readonly targetName = targetName;
  protected readonly targetLabel = targetLabel;
  protected readonly targetIcon = targetIcon;

  protected readonly counts = computed(() =>
    Object.fromEntries(TARGET_KINDS.map((kind) => [kind, kind === 'frontend' ? this.groups().length : this.matching().filter((target) => target.Kind === kind).length])),
  );

  /** The registry of the web applications: which one answers by which name. */
  private readonly web = signal<WebApplication[]>([]);
  /** The applications whose modules are shown. */
  protected readonly expanded = signal<ReadonlySet<string>>(new Set());

  /**
   * The web applications, each with its modules under it: the whole application of micro
   * frontends on top, then its shell and its modules; a monolith alone. While searching, an
   * application shows only the modules that match, unless its own name does.
   */
  protected readonly groups = computed<WebGroup[]>(() => {
    const all = (this.targets() ?? []).filter((target) => target.Kind === 'frontend');
    const heads = all.filter((target) => target.Mode);
    const registry = new Map(this.web().map((app) => [app.Name, app]));
    const groups = new Map<string, WebGroup>();
    for (const head of heads) {
      const known = registry.get(head.Name);
      groups.set(head.Name, { name: head.Name, whole: head, mode: head.Mode!, modules: [], address: known?.Address ?? null, active: known ? known.Active !== false : null });
    }
    const single = heads.filter((head) => head.Mode === 'mfe');
    for (const module of all.filter((target) => !target.Mode)) {
      // One application alone names its modules without itself.
      const owner = module.Name.includes('/') ? module.Name.split('/')[0] : single.length === 1 ? single[0].Name : '';
      let group = groups.get(owner);
      if (!group) {
        group = { name: owner || 'Módulos', whole: null, mode: 'mfe', modules: [], address: null, active: null };
        groups.set(group.name, group);
      }
      group.modules.push(module);
    }
    const wanted = this.search().trim().toLowerCase();
    const matches = (target: TransportTarget) => target.Name.toLowerCase().includes(wanted) || target.Paths.some((path) => path.toLowerCase().includes(wanted));
    return [...groups.values()]
      .map((group) => {
        const modules = group.modules.sort((a, b) => Number(this.isShell(b)) - Number(this.isShell(a)) || this.moduleLabel(a).localeCompare(this.moduleLabel(b), 'pt-BR'));
        const own = !wanted || group.name.toLowerCase().includes(wanted) || (group.whole !== null && matches(group.whole));
        return { ...group, modules: own ? modules : modules.filter(matches), total: modules.length };
      })
      .filter((group) => !wanted || group.modules.length || group.name.toLowerCase().includes(wanted) || (group.whole !== null && matches(group.whole)))
      .sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
  });

  private isShell(target: TransportTarget): boolean {
    return target.Name === 'shell' || target.Name.endsWith('/shell');
  }

  /** A module under its application: without the name of the application before it. */
  protected moduleLabel(target: TransportTarget): string {
    return this.isShell(target) ? 'Casca (shell)' : target.Name.slice(target.Name.indexOf('/') + 1);
  }

  protected isOpen(group: WebGroup): boolean {
    return this.expanded().has(group.name) || (!!this.search().trim() && group.modules.length > 0);
  }

  protected toggleOpen(group: WebGroup): void {
    this.expanded.update((all) => {
      const next = new Set(all);
      if (!next.delete(group.name)) {
        next.add(group.name);
      }
      return next;
    });
  }

  /** Picking the application is picking all of it: the whole application goes as one item. */
  protected groupPicked(group: WebGroup): boolean {
    return group.whole ? this.picked().has(this.key(group.whole)) : this.allPicked(group.modules);
  }

  protected groupPartly(group: WebGroup): boolean {
    return !this.groupPicked(group) && group.modules.some((module) => this.picked().has(this.key(module)));
  }

  protected pickGroup(group: WebGroup, on: boolean): void {
    if (!group.whole) {
      this.pickAll(group.modules, on);
      return;
    }
    // Its modules go with it: picked apart too, they would be carried twice.
    for (const module of group.modules) {
      this.pick(module, false);
    }
    this.pick(group.whole, on);
  }

  /** The applications on top, as the box of the whole section picks them. */
  protected readonly webTops = computed(() => this.groups().flatMap((group) => (group.whole ? [group.whole] : group.modules)));

  private readonly matching = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    return (this.targets() ?? []).filter((target) => !wanted || target.Name.toLowerCase().includes(wanted) || target.Paths.some((path) => path.toLowerCase().includes(wanted)));
  });

  /** One section for each kind that has something to show. */
  protected readonly sections = computed(() =>
    TARGET_KINDS.filter((kind) => !this.kind() || this.kind() === kind)
      .map((kind) => ({ kind, items: kind === 'frontend' ? this.webTops() : this.matching().filter((target) => target.Kind === kind) }))
      .filter((section) => section.items.length),
  );

  ngOnInit(): void {
    this.load();
  }

  /** The targets picked to be packed together, by kind and name. */
  protected readonly picked = signal<ReadonlySet<string>>(new Set());

  protected key(target: TransportTarget): string {
    return `${target.Kind}:${target.Name}`;
  }

  protected emptySet(): ReadonlySet<string> {
    return new Set();
  }

  protected pick(target: TransportTarget, on: boolean): void {
    this.picked.update((all) => {
      const next = new Set(all);
      if (on) {
        next.add(this.key(target));
      } else {
        next.delete(this.key(target));
      }
      return next;
    });
  }

  private pickable(items: TransportTarget[]): TransportTarget[] {
    return items.filter((target) => !target.Problem);
  }

  protected allPicked(items: TransportTarget[]): boolean {
    const usable = this.pickable(items);
    return usable.length > 0 && usable.every((target) => this.picked().has(this.key(target)));
  }

  protected somePicked(items: TransportTarget[]): boolean {
    return !this.allPicked(items) && items.some((target) => this.picked().has(this.key(target)));
  }

  protected pickAll(items: TransportTarget[], on: boolean): void {
    for (const target of this.pickable(items)) {
      this.pick(target, on);
    }
  }

  /** The picked ones that have a new version waiting in the inbox. */
  protected readonly pickedIncoming = computed(() => (this.targets() ?? []).filter((target) => target.Incoming && this.picked().has(this.key(target))));

  /**
   * Everything picked goes into the area, one after the other: what is running now, or the new
   * version left in the inbox. What fails is said, and the rest goes on.
   */
  protected async packPicked(fromInbox: boolean): Promise<void> {
    const chosen = fromInbox ? this.pickedIncoming() : (this.targets() ?? []).filter((target) => this.picked().has(this.key(target)));
    const draft = await this.transport.draftFor();
    if (!draft) {
      return;
    }
    this.busy.set(true);
    const failed: string[] = [];
    let done = 0;
    for (const target of chosen) {
      try {
        await firstValueFrom(fromInbox ? this.api.addIncoming(draft.id, target.Kind, target.Name) : this.api.addRunning(draft.id, target.Kind, target.Name));
        done++;
        this.pick(target, false);
      } catch (failure) {
        failed.push(`${target.Name}: ${uploadProblem(failure, 'recusado')}`);
      }
    }
    this.busy.set(false);
    this.transport.refresh();
    this.load();
    this.transport.told(`${done} ${done === 1 ? 'foi' : 'foram'} para ${draft.name} v${draft.version}${failed.length ? `; não ${failed.length === 1 ? 'foi' : 'foram'}: ${failed.join(' · ')}` : ''}`, draft, failed.length > 0);
  }

  /** What the application is running right now goes into the area. */
  protected running(target: TransportTarget): Promise<void> {
    return this.place((draft) => this.api.addRunning(draft, target.Kind, target.Name), `${target.Name}: a versão que está rodando foi`);
  }

  /** The new version that was left for it in the inbox goes into the area, and leaves the inbox. */
  protected incoming(target: TransportTarget): Promise<void> {
    return this.place((draft) => this.api.addIncoming(draft, target.Kind, target.Name), `${target.Name}: a versão nova foi`);
  }

  /** A zip of the published folder, chosen on the machine of whoever is at the panel; for a file to download, the file itself. */
  protected async upload(target: TransportTarget, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }
    if (target.Kind === 'download') {
      const version = await this.versionOf(target.Name, target.Version);
      if (version) {
        await this.uploadAndApply(target.Kind, target.Name, file, version);
      }
      return;
    }
    await this.uploadAndApply(target.Kind, target.Name, file, null);
  }

  /**
   * A version brought by hand, applied here in one go: a package is made with it, closed and
   * approved to apply now. The version that was there is kept to go back to, what happened is
   * recorded, and the same package can go on to the next environment.
   */
  private async uploadAndApply(kind: string, name: string, file: File, version: string | null): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: `Aplicar ${file.name} em ${name} agora?`,
      message: `Um pacote é criado com este arquivo e aplicado neste ambiente: ${kind === 'download' ? 'o arquivo passa a estar na pasta de distribuição' : 'a aplicação para, os arquivos dela são trocados (os de configuração ficam) e ela inicia de novo'}.`,
      warning: 'A versão atual fica guardada para reverter, e o pacote pode depois seguir para outros ambientes.',
      action: 'Aplicar',
    });
    if (!sure) {
      return;
    }
    this.busy.set(true);
    let draft: string | null = null;
    try {
      const stamp = new Date().toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
      draft = (await firstValueFrom(this.api.createDraft({ name: `${name} ${version ?? stamp}`, description: `Upload de ${file.name} aplicado em ${this.transport.summary()?.environment || 'este ambiente'}.` }))).id;
      await firstValueFrom(this.api.addFiles(draft, kind, name, file, version));
    } catch (failure) {
      // Nothing was applied: the package that was being made goes away.
      if (draft) {
        await firstValueFrom(this.api.deleteDraft(draft)).catch(() => undefined);
      }
      this.busy.set(false);
      this.snack.open(uploadProblem(failure, 'O arquivo não foi aceito'), 'Fechar', { duration: 12000 });
      return;
    }
    let made: string;
    try {
      made = (await firstValueFrom(this.api.closePackage({ draft }))).package.id;
    } catch (failure) {
      this.busy.set(false);
      this.snack.open(uploadProblem(failure, 'O pacote não pôde ser fechado'), 'Fechar', { duration: 12000 });
      void this.router.navigate(['/transporte/aberto', draft]);
      return;
    }
    try {
      await firstValueFrom(this.api.approvePackage(made, null));
      this.snack.open(`${name}: sendo aplicado`, undefined, { duration: 4000 });
    } catch (failure) {
      this.snack.open(uploadProblem(failure, 'O pacote foi criado, mas não pôde ser aplicado'), 'Fechar', { duration: 12000 });
    } finally {
      this.busy.set(false);
      this.transport.refresh();
    }
    // The package page follows the application, item by item.
    void this.router.navigate(['/transporte/pacotes', made]);
  }

  /** A file to download that is not in the folder yet: it goes in under the name it has. */
  protected async uploadNew(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }
    const existing = (this.targets() ?? []).filter((target) => target.Kind === 'download');
    // With more than one folder, the name says which: the same as the files already there.
    const folder = existing.find((target) => target.Name.includes('/'))?.Name.split('/')[0];
    const name = folder ? `${folder}/${file.name}` : file.name;
    if (existing.some((target) => target.Name.toLowerCase() === name.toLowerCase())) {
      this.snack.open(`${file.name} já existe: use Upload e aplicar na linha dele`, 'Fechar', { duration: 8000 });
      return;
    }
    const version = await this.versionOf(name, null);
    if (version) {
      await this.uploadAndApply('download', name, file, version);
    }
  }

  private versionOf(name: string, current: string | null): Promise<string | null> {
    return ask(this.dialog, {
      title: `Versão de ${name}`,
      message: current ? `Hoje está na versão ${current}. Qual é a versão deste arquivo?` : 'Qual é a versão deste arquivo? Ela fica no versions.json da pasta, para quem baixa saber.',
      label: 'Versão',
      action: 'Continuar',
    });
  }

  private async place(request: (draft: string) => Observable<AreaItem>, done: string): Promise<void> {
    const draft = await this.transport.draftFor();
    if (!draft) {
      return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(request(draft.id));
      this.transport.told(`${done} para ${draft.name} v${draft.version}`, draft);
      this.transport.refresh();
      this.load();
    } catch (failure) {
      this.snack.open(uploadProblem(failure, 'O serviço não aceitou o pedido'), 'Fechar', { duration: 12000 });
    } finally {
      this.busy.set(false);
    }
  }

  private load(): void {
    this.api.frontends().pipe(catchError(() => of(null))).subscribe((answer) => this.web.set(answer?.Frontends ?? []));
    this.api.transportTargets().subscribe({
      next: (answer) => {
        this.targets.set(answer.Targets);
        this.inboxes.set((answer as { Inboxes?: Record<string, string> }).Inboxes ?? {});
        this.unmatched.set(answer.Unmatched ?? []);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.11).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }
}
