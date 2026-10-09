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
import { AgoPipe } from '../core/format';
import { AreaItem, TransportTarget } from '../core/models';
import { TARGET_KINDS, Transport, targetIcon, targetName } from '../core/transport';
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

  /** What the application is running right now goes into the area. */
  protected running(target: TransportTarget): Promise<void> {
    return this.place(this.api.addRunning(target.Kind, target.Name), `${target.Name}: o que está rodando foi para a área`);
  }

  /** The new version that was left for it in the inbox goes into the area, and leaves the inbox. */
  protected incoming(target: TransportTarget): Promise<void> {
    return this.place(this.api.addIncoming(target.Kind, target.Name), `${target.Name}: a versão nova foi para a área`);
  }

  /** A zip of the published folder, chosen on the machine of whoever is at the panel. */
  protected async upload(target: TransportTarget, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file) {
      await this.place(this.api.addFiles(target.Kind, target.Name, file), `${target.Name}: ${file.name} foi para a área`);
    }
  }

  private async place(request: Observable<AreaItem>, done: string): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(request);
      this.snack.open(done, 'Ver a área', { duration: 5000 }).onAction().subscribe(() => void this.router.navigate(['/transporte']));
      this.transport.refresh();
      this.load();
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 8000 });
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
