import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { uploadProblem } from '../core/settings';
import { AgoPipe } from '../core/format';
import { AreaItem, TransportTarget } from '../core/models';
import { targetLabel, TARGET_KINDS, Transport, targetIcon, targetName } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/**
 * What runs on the machine of this environment and can be carried in a package: by kind, with
 * the ways a version of each gets into the area.
 */
@Component({
  selector: 'zap-transport-apps',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, TransportTabs, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-apps.html',
  styleUrl: './transport-apps.scss',
})
export class TransportAppsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly transport = inject(Transport);

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

  protected readonly counts = computed(() => Object.fromEntries(TARGET_KINDS.map((kind) => [kind, this.matching().filter((target) => target.Kind === kind).length])));

  private readonly matching = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    return (this.targets() ?? []).filter((target) => !wanted || target.Name.toLowerCase().includes(wanted) || target.Paths.some((path) => path.toLowerCase().includes(wanted)));
  });

  /** One section for each kind that has something to show. */
  protected readonly sections = computed(() =>
    TARGET_KINDS.filter((kind) => !this.kind() || this.kind() === kind)
      .map((kind) => ({ kind, items: this.matching().filter((target) => target.Kind === kind) }))
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
    this.busy.set(true);
    const failed: string[] = [];
    let done = 0;
    for (const target of chosen) {
      try {
        await firstValueFrom(fromInbox ? this.api.addIncoming(target.Kind, target.Name) : this.api.addRunning(target.Kind, target.Name));
        done++;
        this.pick(target, false);
      } catch (failure) {
        failed.push(`${target.Name}: ${uploadProblem(failure, 'recusado')}`);
      }
    }
    this.busy.set(false);
    this.transport.refresh();
    this.load();
    const message = `${done} ${done === 1 ? 'foi' : 'foram'} para a expedição${failed.length ? `; não ${failed.length === 1 ? 'foi' : 'foram'}: ${failed.join(' · ')}` : ''}`;
    this.snack.open(message, done ? 'Ver a expedição' : 'Fechar', { duration: failed.length ? 15000 : 5000 }).onAction().subscribe(() => done && void this.router.navigate(['/transporte']));
  }

  /** What the application is running right now goes into the area. */
  protected running(target: TransportTarget): Promise<void> {
    return this.place(this.api.addRunning(target.Kind, target.Name), `${target.Name}: a versão que está rodando foi para a expedição`);
  }

  /** The new version that was left for it in the inbox goes into the area, and leaves the inbox. */
  protected incoming(target: TransportTarget): Promise<void> {
    return this.place(this.api.addIncoming(target.Kind, target.Name), `${target.Name}: a versão nova foi para a expedição`);
  }

  /** A zip of the published folder, chosen on the machine of whoever is at the panel. */
  protected async upload(target: TransportTarget, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file) {
      await this.place(this.api.addFiles(target.Kind, target.Name, file), `${target.Name}: ${file.name} foi para a expedição`);
    }
  }

  private async place(request: Observable<AreaItem>, done: string): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(request);
      this.snack.open(done, 'Ver a expedição', { duration: 5000 }).onAction().subscribe(() => void this.router.navigate(['/transporte']));
      this.transport.refresh();
      this.load();
    } catch (failure) {
      this.snack.open(uploadProblem(failure, 'O serviço não aceitou o pedido'), 'Fechar', { duration: 12000 });
    } finally {
      this.busy.set(false);
    }
  }

  private load(): void {
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
