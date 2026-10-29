import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { jsonProblem } from '../core/settings';
import { size } from '../core/database';
import { AgoPipe, WhenPipe } from '../core/format';
import { CopyChange, KeptCopies, KeptCopy, RemovedApplication, TargetFile, TransportTarget } from '../core/models';
import { Transport, targetIcon, targetName } from '../core/transport';
import { ApiEndpoints } from '../shared/api-endpoints';

/**
 * One application of this environment: where it is, what serves it, and the files of its
 * configuration, which are edited here and never travel in a package.
 */
@Component({
  selector: 'zap-transport-app',
  imports: [FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatIconModule, MatTooltipModule, AgoPipe, WhenPipe, ApiEndpoints],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-app.html',
  styleUrl: './transport-app.scss',
})
export class TransportAppPage implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly snack = inject(MatSnackBar);

  protected readonly kind = this.route.snapshot.paramMap.get('kind') ?? '';
  protected readonly name = this.route.snapshot.paramMap.get('name') ?? '';
  protected readonly target = signal<TransportTarget | null>(null);
  protected readonly files = signal<TargetFile[] | null>(null);
  protected readonly problem = signal('');
  protected readonly missing = signal(false);
  protected readonly busy = signal(false);

  /** The file that is open, the version of it that was read, and what is in the editor. */
  protected readonly open = signal<TargetFile | null>(null);
  protected readonly loaded = signal<{ content: string; sha: string } | null>(null);
  protected text = '';
  protected restart = false;
  protected readonly edited = signal(false);

  protected readonly targetName = targetName;
  protected readonly targetIcon = targetIcon;
  protected readonly size = size;

  /** Why what is in the editor cannot be saved as it is; empty when it can. */
  protected readonly invalid = signal('');

  protected readonly instances = computed(() => this.target()?.Paths.length ?? 0);
  /** A module of the web application has nothing to start again. */
  protected readonly restartable = this.kind !== 'frontend';

  /** A module of the web application is not taken away from here. */
  protected readonly removable = ['worker', 'api', 'service'].includes(this.kind);
  protected readonly removed = signal<RemovedApplication | null>(null);
  protected typedName = '';
  protected forceRemove = false;

  // ── The copies kept of it, going back to one of them, and how many are kept ──
  private readonly transport = inject(Transport);
  protected readonly copies = signal<KeptCopies | null>(null);
  protected readonly copiesProblem = signal('');
  protected readonly choosing = signal<string | null>(null);
  protected readonly restored = signal<{ Applied: boolean; Problem: string | null; Messages: string[] } | null>(null);
  protected reason = '';
  protected forceRestore = false;
  protected keepValue = 3;
  protected readonly keepEdited = signal(false);

  /** Why a copy was made, in words. */
  protected why(copy: KeptCopy): string {
    switch (copy.Why) {
      case 'before-restore':
        return 'antes de voltar para outra cópia';
      case 'before-revert':
        return 'antes de reverter um pacote';
      default:
        return copy.Package ? `antes do pacote ${this.packageName(copy.Package)}` : 'antes de um pacote';
    }
  }

  protected changeText(entry: CopyChange): string {
    switch (entry.What) {
      case 'replaced':
        return `Pacote ${entry.Package ? this.packageName(entry.Package) : ''} aplicado`;
      case 'reverted':
        return `Pacote ${entry.Package ? this.packageName(entry.Package) : ''} revertido`;
      case 'restored':
        return 'Voltou para uma cópia guardada';
      default:
        return entry.What;
    }
  }

  private packageName(id: string): string {
    const names = this.transport.names();
    return names[id] ?? Object.entries(names).find(([known]) => known.startsWith(id))?.[1] ?? id.slice(0, 8);
  }

  protected choose(id: string): void {
    this.choosing.set(this.choosing() === id ? null : id);
    this.reason = '';
    this.forceRestore = false;
  }

  protected async restore(copy: KeptCopy): Promise<void> {
    if (!this.reason.trim()) {
      return;
    }
    this.busy.set(true);
    this.restored.set(null);
    try {
      this.restored.set(await firstValueFrom(this.api.restoreCopy(this.kind, this.name, copy.Id, this.reason.trim(), this.forceRestore)));
      this.choosing.set(null);
      this.reason = '';
      this.load();
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  protected async saveKeep(): Promise<void> {
    await this.keeping(this.keepValue, `${this.name} passa a guardar ${this.keepValue} ${this.keepValue === 1 ? 'cópia' : 'cópias'}`);
  }

  protected async resetKeep(): Promise<void> {
    await this.keeping(null, `${this.name} volta a guardar o número de cópias do ambiente`);
  }

  private async keeping(value: number | null, said: string): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.setKeptCopies(this.kind, this.name, value));
      this.snack.open(said, undefined, { duration: 4000 });
      // The Worker Control reads its configuration again; a moment later it says the new number.
      setTimeout(() => this.loadCopies(), 1500);
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  private loadCopies(): void {
    this.api.keptCopies(this.kind, this.name).subscribe({
      next: (answer) => {
        this.copies.set(answer);
        this.keepValue = answer.KeepVersions;
        this.keepEdited.set(false);
        this.copiesProblem.set('');
      },
      error: (failure: HttpErrorResponse) => {
        if (failure.status !== 401 && failure.status !== 404) {
          this.copiesProblem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.30).' : (failure.error?.error ?? 'Não foi possível ler as cópias guardadas.'));
        }
      },
    });
  }

  protected async remove(): Promise<void> {
    if (this.typedName.trim() !== this.name) {
      return;
    }
    this.busy.set(true);
    try {
      this.removed.set(await firstValueFrom(this.api.removeApplication(this.kind, this.name, this.forceRemove)));
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  ngOnInit(): void {
    this.load();
    this.transport.loadNames();
  }

  protected select(file: TargetFile): void {
    if (this.edited() && !confirm('O que foi alterado neste arquivo e não foi salvo será perdido. Continuar?')) {
      return;
    }
    this.open.set(file);
    this.loaded.set(null);
    this.edited.set(false);
    this.invalid.set('');
    this.api.targetFile(this.kind, this.name, file.Instance, file.Path).subscribe({
      next: (answer) => {
        this.loaded.set({ content: answer.Content, sha: answer.Sha256 });
        this.text = answer.Content;
      },
      error: (failure) => this.say(failure),
    });
  }

  protected typed(text: string): void {
    this.text = text;
    this.edited.set(text !== this.loaded()?.content);
    this.invalid.set(this.check(text));
  }

  /** A JSON file has to be JSON; anything else is written as it is. */
  private check(text: string): string {
    if (!this.open()?.Path.toLowerCase().endsWith('.json')) {
      return '';
    }
    // As the applications read it: comments and a comma left before a closing bracket are fine.
    return jsonProblem(text);
  }

  protected discard(): void {
    this.text = this.loaded()?.content ?? '';
    this.edited.set(false);
    this.invalid.set('');
  }

  protected async save(): Promise<void> {
    const file = this.open();
    const loaded = this.loaded();
    if (!file || !loaded) {
      return;
    }
    this.busy.set(true);
    try {
      const answer = await firstValueFrom(
        this.api.writeTargetFile({ kind: this.kind, name: this.name, instance: file.Instance, path: file.Path, content: this.text, sha256: loaded.sha, restart: this.restart && this.restartable }),
      );
      this.loaded.set({ content: this.text, sha: answer.Sha256 });
      this.edited.set(false);
      this.snack.open(
        answer.RestartProblem ? `Salvo, mas a aplicação não reiniciou: ${answer.RestartProblem}` : answer.Restarted ? 'Salvo, e a aplicação foi reiniciada' : 'Salvo. A versão anterior ficou guardada no servidor.',
        answer.RestartProblem ? 'Fechar' : undefined,
        { duration: answer.RestartProblem ? 12000 : 4000 },
      );
      this.load();
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  private say(failure: unknown): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 9000 });
  }

  private load(): void {
    this.loadCopies();
    this.api.targetFiles(this.kind, this.name).subscribe({
      next: (answer) => {
        this.target.set(answer.Target);
        this.files.set(answer.Files);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => {
        if (failure.status === 404) {
          this.missing.set(true);
        } else if (failure.status !== 401) {
          this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.13).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.'));
        }
      },
    });
  }
}
