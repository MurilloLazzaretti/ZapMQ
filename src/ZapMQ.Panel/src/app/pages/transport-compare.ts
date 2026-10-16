import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { kindIcon, kindName, size } from '../core/database';
import { DiffLine, diff } from '../core/diff';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { ComparedFile, ComparedItem, Comparison, TransportRoute } from '../core/models';
import { Transport, targetIcon, targetName } from '../core/transport';
import { CompareMemory } from '../core/compare-memory';
import { DiffView } from '../shared/diff-view';
import { Stat } from '../shared/stat';
import { TransportTabs } from '../shared/transport-tabs';

type Filter = 'differ' | 'newerHere' | 'newerThere' | 'onlyHere' | 'onlyThere' | 'same' | 'all';

const FILTERS: { id: Filter; text: string }[] = [
  { id: 'differ', text: 'Tudo o que difere' },
  { id: 'newerHere', text: 'Mais novo aqui' },
  { id: 'newerThere', text: 'Mais novo lá' },
  { id: 'onlyHere', text: 'Só aqui' },
  { id: 'onlyThere', text: 'Só lá' },
  { id: 'same', text: 'Iguais' },
  { id: 'all', text: 'Tudo' },
];

const KINDS = ['database', 'worker', 'service', 'api', 'frontend', 'download'];

/** What is open under a row: the two scripts, or the files of the two sides. */
interface Opened {
  loading: boolean;
  problem?: string;
  lines?: DiffLine[];
  files?: ComparedFile[];
}

/**
 * This environment beside another one reached by a route: what is the same, what differs, what
 * only one of them has, and which side has the newer one. What is newer here can go, in one
 * package, to the other side.
 */
@Component({
  selector: 'zap-transport-compare',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, TransportTabs, Stat, DiffView, AgoPipe, NumPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-compare.html',
  styleUrl: './transport-compare.scss',
})
export class TransportComparePage implements OnInit {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  private readonly transport = inject(Transport);

  protected readonly routes = signal<TransportRoute[] | null>(null);
  protected readonly environment = signal('');
  protected route = '';
  protected readonly result = signal<Comparison | null>(null);
  /** The route the result is of. */
  private compared = '';
  protected readonly busy = signal(false);
  protected readonly started = signal<number | null>(null);
  protected readonly problem = signal('');
  /** What was being looked at is kept while the panel is open. */
  private readonly memory = inject(CompareMemory);
  protected readonly filter = this.memory.filter as ReturnType<typeof signal<Filter>>;
  protected readonly kind = this.memory.kind;
  protected readonly search = this.memory.search;
  /** Loading the comparison kept from before. */
  protected readonly loadingLast = signal(false);
  /** A comparison more than an hour old: the environments may have changed since. */
  protected readonly stale = computed(() => {
    const at = this.result()?.comparedAt;
    return !!at && Date.now() - Date.parse(at) > 3600000;
  });
  protected readonly picked = signal<ReadonlySet<string>>(new Set());
  protected readonly opened = signal<ReadonlyMap<string, Opened>>(new Map());
  protected readonly creating = signal(false);

  protected readonly filters = FILTERS;
  protected readonly kindIcon = kindIcon;
  protected readonly kindName = kindName;
  protected readonly targetIcon = targetIcon;
  protected readonly size = size;

  protected readonly here = computed(() => this.result()?.here.environment ?? this.environment());
  protected readonly there = computed(() => this.result()?.there.environment ?? '');

  protected readonly totals = computed(() => {
    const items = this.result()?.items ?? [];
    return {
      same: items.filter((item) => item.state === 'same').length,
      newerHere: items.filter((item) => this.newerHere(item)).length,
      newerThere: items.filter((item) => this.newerThere(item)).length,
      undecided: items.filter((item) => (item.state === 'different' || item.state === 'unknown') && !item.newer).length,
      onlyHere: items.filter((item) => item.state === 'here').length,
      onlyThere: items.filter((item) => item.state === 'there').length,
    };
  });

  private readonly matching = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    return (this.result()?.items ?? []).filter((item) => {
      const passes: Record<Filter, boolean> = {
        differ: item.state !== 'same',
        newerHere: this.newerHere(item),
        newerThere: this.newerThere(item),
        onlyHere: item.state === 'here',
        onlyThere: item.state === 'there',
        same: item.state === 'same',
        all: true,
      };
      return passes[this.filter()] && (!wanted || this.label(item).toLowerCase().includes(wanted));
    });
  });

  protected readonly counts = computed(() => Object.fromEntries(KINDS.map((kind) => [kind, this.matching().filter((item) => item.kind === kind).length])));

  protected readonly sections = computed(() =>
    KINDS.filter((kind) => !this.kind() || this.kind() === kind)
      .map((kind) => ({ kind, items: this.matching().filter((item) => item.kind === kind) }))
      .filter((section) => section.items.length),
  );

  protected readonly pickedItems = computed(() => (this.result()?.items ?? []).filter((item) => this.picked().has(this.key(item))));

  ngOnInit(): void {
    this.api.transportRoutes().subscribe({
      next: (answer) => {
        this.routes.set(answer.routes);
        this.environment.set(answer.environment);
        this.route = answer.routes.some((item) => item.id === this.memory.route()) ? this.memory.route() : (answer.routes[0]?.id ?? '');
        this.showLast();
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível ler as rotas de transporte.'),
    });
  }

  protected kindLabel(kind: string): string {
    return kind === 'database' ? 'Banco de dados' : targetName(kind, true);
  }

  protected icon(item: ComparedItem): string {
    return item.kind === 'database' ? kindIcon(item.objectKind) : targetIcon(item.kind);
  }

  protected key(item: ComparedItem): string {
    return `${item.kind}|${item.objectKind ?? ''}|${item.schema ?? ''}|${item.name}`;
  }

  protected label(item: ComparedItem): string {
    return item.schema ? `${item.schema}.${item.name}` : item.name;
  }

  protected newerHere(item: ComparedItem): boolean {
    return (item.state === 'different' || item.state === 'unknown') && item.newer === 'here';
  }

  protected newerThere(item: ComparedItem): boolean {
    return (item.state === 'different' || item.state === 'unknown') && item.newer === 'there';
  }

  /** What can go from here to the other side: what only this side has, and what is newer here. */
  protected carriable(item: ComparedItem): boolean {
    return item.state === 'here' || this.newerHere(item) || ((item.state === 'different' || item.state === 'unknown') && !item.newer);
  }

  protected state(item: ComparedItem): { text: string; tone: string; hint: string } {
    const there = this.there();
    switch (item.state) {
      case 'same':
        return { text: 'igual', tone: 'ok', hint: 'O mesmo nos dois ambientes.' };
      case 'here':
        return { text: `só em ${this.here()}`, tone: 'info', hint: `${there} não tem.` };
      case 'there':
        return { text: `só em ${there}`, tone: 'warn', hint: `${this.here()} não tem. Para levar daqui, só como exclusão; para trazer, compare a partir de ${there}.` };
      case 'unknown':
        return { text: 'não deu para comparar', tone: '', hint: 'O conteúdo de um dos lados não pôde ser lido. As datas ainda dizem qual mudou depois.' };
      default:
        return { text: 'diferente', tone: 'danger', hint: 'Os dois têm, e não são iguais.' };
    }
  }

  /** The comparison with the chosen environment kept by the service, as it was found. */
  protected showLast(): void {
    this.memory.route.set(this.route);
    this.result.set(null);
    this.picked.set(new Set());
    this.opened.set(new Map());
    if (!this.route) {
      return;
    }
    this.loadingLast.set(true);
    const route = this.route;
    this.api.lastComparison(route).subscribe({
      next: (last) => {
        if (route === this.route) {
          this.result.set(last);
          this.compared = route;
        }
        this.loadingLast.set(false);
      },
      error: () => this.loadingLast.set(false),
    });
  }

  protected async run(): Promise<void> {
    if (!this.route) {
      return;
    }
    this.busy.set(true);
    this.started.set(Date.now());
    this.problem.set('');
    this.picked.set(new Set());
    this.opened.set(new Map());
    try {
      this.result.set(await firstValueFrom(this.api.compare(this.route)));
      this.compared = this.route;
      this.memory.route.set(this.route);
    } catch (failure) {
      this.problem.set((failure as HttpErrorResponse).error?.error ?? 'A comparação não pôde ser feita.');
    } finally {
      this.busy.set(false);
      this.started.set(null);
    }
  }

  protected pick(item: ComparedItem, on: boolean): void {
    this.picked.update((all) => {
      const next = new Set(all);
      if (on) {
        next.add(this.key(item));
      } else {
        next.delete(this.key(item));
      }
      return next;
    });
  }

  protected clearPicked(): void {
    this.picked.set(new Set());
  }

  protected allPicked(items: ComparedItem[]): boolean {
    const usable = items.filter((item) => this.carriable(item));
    return usable.length > 0 && usable.every((item) => this.picked().has(this.key(item)));
  }

  protected pickAll(items: ComparedItem[], on: boolean): void {
    for (const item of items.filter((candidate) => this.carriable(candidate))) {
      this.pick(item, on);
    }
  }

  /** Marks what is behind on the other side: what is newer here, and what only this side has. */
  protected suggest(): void {
    this.picked.set(new Set((this.result()?.items ?? []).filter((item) => item.state === 'here' || this.newerHere(item)).map((item) => this.key(item))));
    this.filter.set('differ');
    this.snack.open(`${this.picked().size} ${this.picked().size === 1 ? 'item marcado' : 'itens marcados'}: o que está mais novo ou só existe em ${this.here()}`, undefined, { duration: 4000 });
  }

  protected async toggle(item: ComparedItem): Promise<void> {
    const key = this.key(item);
    if (this.opened().has(key)) {
      this.opened.update((all) => {
        const next = new Map(all);
        next.delete(key);
        return next;
      });
      return;
    }
    const set = (value: Opened) => this.opened.update((all) => new Map(all).set(key, value));
    set({ loading: true });
    try {
      if (item.kind === 'database') {
        const scripts = await firstValueFrom(this.api.compareObject(this.compared, item.objectKind ?? '', item.schema ?? '', item.name));
        // What the other side has, and what this one would bring.
        set({ loading: false, lines: diff(scripts.there ?? '', scripts.here ?? '') });
      } else {
        set({ loading: false, files: (await firstValueFrom(this.api.compareTarget(this.compared, item.kind, item.name))).files });
      }
    } catch (failure) {
      set({ loading: false, problem: (failure as HttpErrorResponse).error?.error ?? 'Não foi possível ler os dois lados.' });
    }
  }

  protected changedFiles(files: ComparedFile[]): ComparedFile[] {
    return files.filter((file) => file.state !== 'same');
  }

  protected sameFiles(files: ComparedFile[]): number {
    return files.filter((file) => file.state === 'same').length;
  }

  /**
   * One package with what was marked, made here: the objects as they are now, the applications
   * as they are running. It opens to be looked at, closed and sent.
   */
  protected async equalize(): Promise<void> {
    const items = this.pickedItems();
    if (!items.length) {
      return;
    }
    const day = new Date().toLocaleDateString('pt-BR');
    // An open package, or a new one with the name of the equalization.
    const draft = await this.transport.draftFor(false, {
      name: `Equalização ${this.here()} → ${this.there()} ${day}`,
      description: `O que estava mais novo em ${this.here()} do que em ${this.there()}, ou só existia em ${this.here()}, na comparação de ${day}.`,
    });
    if (!draft) {
      return;
    }
    this.creating.set(true);
    const failed: string[] = [];
    try {
      for (const item of items) {
        try {
          if (item.kind === 'database') {
            await firstValueFrom(this.api.addToArea(draft.id, { kind: item.objectKind ?? '', schema: item.schema ?? '', name: item.name }));
          } else {
            await firstValueFrom(this.api.addRunning(draft.id, item.kind, item.name));
          }
        } catch (failure) {
          failed.push(`${this.label(item)}: ${(failure as HttpErrorResponse).error?.error ?? 'recusado'}`);
        }
      }
      this.transport.choose(draft);
      this.snack.open(
        failed.length
          ? `${items.length - failed.length} em ${draft.name} v${draft.version}; não ${failed.length === 1 ? 'entrou' : 'entraram'}: ${failed.join(' · ')}`
          : `${items.length} ${items.length === 1 ? 'item foi' : 'itens foram'} para ${draft.name} v${draft.version}`,
        'Fechar', { duration: failed.length ? 15000 : 4000 });
      void this.router.navigate(['/transporte/aberto', draft.id]);
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'Não foi possível incluir no pacote', 'Fechar', { duration: 8000 });
    } finally {
      this.creating.set(false);
    }
  }
}
