import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { Auth } from '../core/auth';

@Component({
  selector: 'zap-login',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="backdrop" aria-hidden="true"></div>
    <form class="card" (ngSubmit)="submit()" autocomplete="on">
      @if (busy()) {
        <mat-progress-bar mode="indeterminate" />
      }
      <div class="brand">
        <span class="mark"><svg viewBox="0 0 64 64"><path d="M36 10 18 36h13l-3 18 18-26H33z" /></svg></span>
        <div>
          <h1>ZapMQ</h1>
          <p>Painel de administração</p>
        </div>
      </div>

      <mat-form-field>
        <mat-label>Usuário</mat-label>
        <input matInput name="user" [(ngModel)]="user" autocomplete="username" autocapitalize="none" spellcheck="false" required autofocus />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Senha</mat-label>
        <input matInput name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required />
      </mat-form-field>

      @if (error()) {
        <p class="error" role="alert"><mat-icon svgIcon="error" /> {{ error() }}</p>
      }

      <button mat-flat-button type="submit" [disabled]="busy() || !user || !password">Entrar</button>

      @if (auth.version()) {
        <span class="version">versão {{ auth.version() }}</span>
      }
    </form>
  `,
  styles: `
    :host {
      min-height: 100dvh;
      display: grid;
      place-items: center;
      padding: 24px 16px;
      position: relative;
      overflow: hidden;
    }
    .backdrop {
      position: absolute;
      inset: -20%;
      background:
        radial-gradient(40% 40% at 25% 25%, color-mix(in srgb, #6d5efc 32%, transparent), transparent 70%),
        radial-gradient(35% 35% at 80% 70%, color-mix(in srgb, #22c1dc 28%, transparent), transparent 70%);
      filter: blur(40px);
      z-index: 0;
    }
    .card {
      position: relative;
      z-index: 1;
      width: min(100%, 400px);
      display: grid;
      gap: 16px;
      padding: clamp(24px, 6vw, 40px);
      border-radius: 28px;
      background: color-mix(in srgb, var(--mat-sys-surface-container-lowest) 88%, transparent);
      backdrop-filter: blur(20px);
      border: 1px solid var(--zap-border);
      box-shadow: 0 24px 60px -24px rgb(0 0 0 / 0.35);
      overflow: hidden;
    }
    mat-progress-bar { position: absolute; top: 0; left: 0; right: 0; }
    .brand { display: flex; align-items: center; gap: 16px; margin-bottom: 8px; }
    .mark {
      width: 56px;
      height: 56px;
      border-radius: 16px;
      display: grid;
      place-items: center;
      flex: none;
      background: linear-gradient(135deg, #6d5efc, #22c1dc);
      box-shadow: 0 10px 24px -8px #6d5efc;
    }
    .mark svg { width: 34px; height: 34px; fill: #fff; }
    h1 { margin: 0; font: var(--mat-sys-headline-medium); font-weight: 700; letter-spacing: -0.03em; }
    p { margin: 0; color: var(--mat-sys-on-surface-variant); }
    .error {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 14px;
      border-radius: 12px;
      background: var(--zap-danger-container);
      color: var(--zap-danger);
    }
    button[type='submit'] { height: 48px; font-size: 15px; }
    .version { text-align: center; font: var(--mat-sys-label-small); color: var(--mat-sys-on-surface-variant); }
  `,
})
export class LoginPage {
  protected readonly auth = inject(Auth);
  private readonly router = inject(Router);

  /** Where the user was going when the panel asked for the login. */
  readonly voltar = input<string>();

  protected user = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  constructor() {
    // Shows the version and skips the form for whoever is already in.
    void this.auth.ensure().then((inside) => inside && this.enter());
  }

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.user.trim(), this.password);
      this.enter();
    } catch (failure) {
      const response = failure as HttpErrorResponse;
      this.error.set(response.status === 401 ? 'Usuário ou senha inválidos.' : 'Não foi possível falar com o serviço.');
      this.password = '';
    } finally {
      this.busy.set(false);
    }
  }

  private enter(): void {
    const target = this.voltar();
    void this.router.navigateByUrl(target && target.startsWith('/') && !target.startsWith('//') ? target : '/');
  }
}
