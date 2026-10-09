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
import { size } from '../core/database';
import { AgoPipe } from '../core/format';
import { TargetFile, TransportTarget } from '../core/models';
import { targetIcon, targetName } from '../core/transport';

/**
 * One application of this environment: where it is, what serves it, and the files of its
 * configuration, which are edited here and never travel in a package.
 */
@Component({
  selector: 'zap-transport-app',
  imports: [FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatIconModule, MatTooltipModule, AgoPipe],
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

  ngOnInit(): void {
    this.load();
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
    try {
      // Comments are allowed in these files and are not JSON for the browser.
      JSON.parse(text.replace(/^\s*\/\/.*$/gm, ''));
      return '';
    } catch (error) {
      return (error as Error).message;
    }
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
