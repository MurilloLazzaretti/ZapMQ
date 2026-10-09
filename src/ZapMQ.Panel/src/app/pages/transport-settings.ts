import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { TransportSettings } from '../core/models';
import { TARGET_KINDS, targetIcon, targetName } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/** How the transport is set up on this environment: where new versions are left, by kind. */
@Component({
  selector: 'zap-transport-settings',
  imports: [FormsModule, MatButtonModule, MatIconModule, TransportTabs],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Transporte</h1>
          <p class="muted">Como o transporte funciona neste ambiente.</p>
        </div>
        <span class="spacer"></span>
        <zap-transport-tabs />
      </header>

      @if (problem()) {
        <section class="panel empty"><mat-icon svgIcon="cloud_off" /><strong>Não foi possível ler a configuração</strong><span>{{ problem() }}</span></section>
      } @else if (settings(); as now) {
        <section class="panel">
          <div class="panel-head"><h2>Pastas de entrada</h2><span class="hint">onde uma versão nova é deixada no servidor, por tipo de aplicação</span></div>
          <form (ngSubmit)="save()">
            @for (kind of kinds; track kind) {
              <label>
                <span class="kind"><mat-icon [svgIcon]="targetIcon(kind)" /> {{ targetName(kind, true) }}</span>
                <input type="text" [name]="kind" [attr.data-kind]="kind" [attr.aria-label]="'Pasta de entrada de ' + targetName(kind, true)" [(ngModel)]="paths[kind]" [placeholder]="defaults[kind]" spellcheck="false" autocomplete="off" />
                @if (!now.Inboxes[kind].Exists) { <span class="pill warn">a pasta não existe</span> }
              </label>
            }
            <p class="muted note">
              Dentro da pasta de cada tipo, a versão nova fica em uma pasta (ou um <code>.zip</code>) com o nome da aplicação. Em branco, vale a pasta padrão, ao lado do Worker Control.
              A pasta é criada ao salvar, se ainda não existir.
            </p>
            <div class="buttons"><button mat-flat-button type="submit" [disabled]="busy()"><mat-icon svgIcon="save" /> Salvar</button></div>
          </form>
        </section>

        <section class="panel">
          <div class="panel-head"><h2>O que é do ambiente</h2><span class="hint">nunca entra em um pacote nem é substituído por um</span></div>
          <div class="keep">
            @for (pattern of now.Keep; track pattern) { <span class="pill mono">{{ pattern }}</span> }
          </div>
          <p class="muted note pad">São guardadas as últimas {{ now.KeepVersions }} cópias de cada aplicação substituída. Estes dois ajustes ficam em <code>Transport</code>, no <code>ConfigWorkers.json</code>.</p>
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
    form { display: grid; gap: 10px; padding: 6px 20px 18px; }
    label { display: grid; grid-template-columns: minmax(180px, 240px) minmax(0, 1fr) auto; align-items: center; gap: 12px; }
    .kind { display: flex; align-items: center; gap: 10px; }
    .kind mat-icon { width: 20px; height: 20px; color: var(--mat-sys-primary); }
    input {
      height: 40px; min-width: 0; padding: 0 12px; border: 1px solid var(--zap-border); border-radius: 10px;
      background: var(--zap-surface); color: var(--mat-sys-on-surface); font-family: var(--zap-mono); font-size: 13px;
    }
    input:focus { outline: 2px solid var(--mat-sys-primary); border-color: transparent; }
    .note { margin: 4px 0 0; font: var(--mat-sys-body-small); max-width: 90ch; }
    .note.pad { padding: 0 20px 16px; }
    .buttons { display: flex; justify-content: flex-end; }
    .keep { display: flex; gap: 8px; flex-wrap: wrap; padding: 4px 20px 12px; }
    @media (max-width: 720px) { label { grid-template-columns: minmax(0, 1fr); gap: 4px; } }
  `,
})
export class TransportSettingsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);

  protected readonly kinds = TARGET_KINDS;
  protected readonly targetName = targetName;
  protected readonly targetIcon = targetIcon;
  protected readonly settings = signal<TransportSettings | null>(null);
  protected readonly problem = signal('');
  protected readonly busy = signal(false);
  /** What was typed for each kind; empty goes back to the default folder. */
  protected paths: Record<string, string> = {};
  protected defaults: Record<string, string> = {};

  ngOnInit(): void {
    this.load();
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.setInboxes(Object.fromEntries(this.kinds.map((kind) => [kind, (this.paths[kind] ?? '').trim()]))));
      this.snack.open('Pastas de entrada salvas', undefined, { duration: 2500 });
      // The Worker Control reads its configuration again in a moment.
      setTimeout(() => this.load(), 1500);
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou as pastas', 'Fechar', { duration: 9000 });
    } finally {
      this.busy.set(false);
    }
  }

  private load(): void {
    this.api.transportSettings().subscribe({
      next: (settings) => {
        this.settings.set(settings);
        for (const kind of this.kinds) {
          const inbox = settings.Inboxes[kind];
          this.paths[kind] = inbox.Said ? inbox.Path : '';
          this.defaults[kind] = inbox.Said ? '' : inbox.Path;
        }
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.13).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }
}
