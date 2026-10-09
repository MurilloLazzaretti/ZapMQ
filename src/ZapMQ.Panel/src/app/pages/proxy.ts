import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { size } from '../core/database';
import { AgoPipe } from '../core/format';
import { ProxyState } from '../core/models';
import { confirm } from '../shared/dialogs';
import { WorkerTabs } from '../shared/worker-tabs';

/**
 * The reverse proxy in front of the applications: the files of its configuration, edited
 * here, and the asking of it to read them again. A file is only left changed when the proxy
 * itself says the whole configuration is still good.
 */
@Component({
  selector: 'zap-proxy',
  imports: [FormsModule, MatButtonModule, MatIconModule, MatTooltipModule, WorkerTabs, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './proxy.html',
  styleUrl: './transport-app.scss',
  styles: `
    .head { align-items: center; flex-wrap: wrap; gap: 12px 16px; }
    .head h1 { margin: 0; }
    .state { display: flex; align-items: center; gap: 8px 12px; flex-wrap: wrap; padding: 14px 20px; }
    .state .where { display: grid; gap: 2px; min-width: min(100%, 300px); flex: 1 1 300px; overflow-wrap: anywhere; }
    .said { margin: 0; padding: 10px 16px; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12.5px; }
    .said.ok { background: var(--zap-ok-container); color: var(--zap-ok); }
    .said.bad { background: var(--zap-danger-container); color: var(--zap-danger); }
    .pending { color: var(--zap-warn); font-weight: 600; }
  `,
})
export class ProxyPage implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);

  protected readonly state = signal<ProxyState | null>(null);
  protected readonly problem = signal('');
  protected readonly busy = signal(false);
  protected readonly open = signal<string | null>(null);
  protected readonly loaded = signal<{ content: string; sha: string } | null>(null);
  protected readonly edited = signal(false);
  /** What the proxy said the last time it was asked something, and whether it was good news. */
  protected readonly said = signal<{ ok: boolean; text: string } | null>(null);
  /** Something was saved and the proxy has not been asked to read it yet. */
  protected readonly unread = signal(false);
  protected text = '';

  protected readonly size = size;

  ngOnInit(): void {
    this.load();
  }

  protected select(path: string): void {
    if (this.edited() && !window.confirm('O que foi alterado neste arquivo e não foi salvo será perdido. Continuar?')) {
      return;
    }
    this.open.set(path);
    this.loaded.set(null);
    this.edited.set(false);
    this.api.proxyFile(path).subscribe({
      next: (answer) => {
        this.loaded.set({ content: answer.Content, sha: answer.Sha256 });
        this.text = answer.Content;
      },
      error: (failure) => this.fail(failure),
    });
  }

  protected typed(text: string): void {
    this.text = text;
    this.edited.set(text !== this.loaded()?.content);
  }

  protected discard(): void {
    this.text = this.loaded()?.content ?? '';
    this.edited.set(false);
  }

  protected async save(): Promise<void> {
    const path = this.open();
    const loaded = this.loaded();
    if (!path || !loaded) {
      return;
    }
    await this.run(this.api.writeProxyFile(path, this.text, loaded.sha), (answer) => {
      this.said.set({ ok: answer.Saved, text: answer.Saved ? 'Salvo. O proxy aceita a configuração: ' + answer.Output : 'Não foi salvo: o proxy recusou a configuração, e o arquivo continua como estava.\n' + answer.Output });
      if (answer.Saved) {
        this.loaded.set({ content: this.text, sha: answer.Sha256 ?? loaded.sha });
        this.edited.set(false);
        this.unread.set(true);
        this.load();
      }
    });
  }

  protected test(): Promise<void> {
    return this.run(this.api.proxyTest(), (answer) => this.said.set({ ok: answer.Valid, text: answer.Output }));
  }

  /** The proxy reads its configuration again without dropping who is connected. */
  protected reload(): Promise<void> {
    return this.run(this.api.proxyReload(), (answer) => {
      this.said.set({ ok: answer.Reloaded, text: answer.Reloaded ? 'O proxy leu a configuração de novo. ' + answer.Output : 'O proxy não foi recarregado: a configuração não está boa.\n' + answer.Output });
      this.unread.set(!answer.Reloaded && this.unread());
    });
  }

  protected async restart(): Promise<void> {
    const sure = await confirm(this.dialog, {
      title: 'Reiniciar o serviço do proxy?',
      message: 'Tudo o que passa pelo proxy fica fora do ar pelos segundos que o serviço leva para parar e iniciar. Para só aplicar uma mudança de configuração, recarregar basta e não derruba ninguém.',
      action: 'Reiniciar',
      danger: true,
    });
    if (sure) {
      await this.run(this.api.proxyRestart(), (answer) => {
        this.said.set({ ok: answer.Restarted, text: answer.Restarted ? 'O serviço foi reiniciado.' : 'O serviço não foi reiniciado.\n' + answer.Output });
        this.unread.set(!answer.Restarted && this.unread());
        this.load();
      });
    }
  }

  private async run<T>(request: Observable<T>, then: (answer: T) => void): Promise<void> {
    this.busy.set(true);
    try {
      then(await firstValueFrom(request));
    } catch (failure) {
      this.fail(failure);
    } finally {
      this.busy.set(false);
    }
  }

  private fail(failure: unknown): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou o pedido', 'Fechar', { duration: 9000 });
  }

  private load(): void {
    this.api.proxy().subscribe({
      next: (state) => {
        this.state.set(state);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.14).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }
}
