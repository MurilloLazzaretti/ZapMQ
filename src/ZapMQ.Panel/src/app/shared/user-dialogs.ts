import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';

const MINIMUM = 8;

const STYLES = `
  .fields { display: grid; gap: 14px; padding-top: 6px; }
  mat-form-field { width: 100%; }
  .lead { margin: 0; color: var(--mat-sys-on-surface-variant); }
  .error { display: flex; gap: 8px; align-items: center; margin: 0; color: var(--zap-danger); }
  .error mat-icon { flex: none; width: 18px; height: 18px; }
`;

const said = (failure: unknown) => (failure as HttpErrorResponse).error?.error ?? 'Não foi possível falar com o serviço.';

/** Whoever is logged in choosing its own password. */
@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Trocar senha</h2>
    <form (ngSubmit)="save()">
      <mat-dialog-content>
        <div class="fields">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Senha atual</mat-label>
            <input matInput type="password" name="current" [(ngModel)]="current" autocomplete="current-password" required />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Senha nova</mat-label>
            <input matInput type="password" name="password" [(ngModel)]="password" autocomplete="new-password" required />
            <mat-hint>Pelo menos {{ minimum }} caracteres</mat-hint>
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Senha nova, de novo</mat-label>
            <input matInput type="password" name="again" [(ngModel)]="again" autocomplete="new-password" required />
            @if (again && password !== again) {
              <mat-hint>As duas não são iguais</mat-hint>
            }
          </mat-form-field>
          @if (error()) {
            <p class="error" role="alert"><mat-icon svgIcon="error" /> {{ error() }}</p>
          }
        </div>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancelar</button>
        <button mat-flat-button type="submit" [disabled]="busy() || !current || password.length < minimum || password !== again">Trocar</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: STYLES,
})
export class PasswordDialog {
  private readonly auth = inject(Auth);
  private readonly reference = inject(MatDialogRef<PasswordDialog, boolean>);

  protected readonly minimum = MINIMUM;
  protected current = '';
  protected password = '';
  protected again = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.changePassword(this.current, this.password);
      this.reference.close(true);
    } catch (failure) {
      this.error.set(said(failure));
    } finally {
      this.busy.set(false);
    }
  }
}

/** The master making a new user, or giving an existing one another password. */
@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.reset ? 'Redefinir senha' : 'Novo usuário' }}</h2>
    <form (ngSubmit)="save()">
      <mat-dialog-content>
        <div class="fields">
          @if (data.reset) {
            <p class="lead">Uma senha nova para <strong>{{ data.reset }}</strong>. Quem estiver com esse usuário aberto precisa entrar de novo.</p>
          } @else {
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Login</mat-label>
              <input matInput name="login" [(ngModel)]="login" autocomplete="off" spellcheck="false" required />
              <mat-hint>Letras, números, ponto, traço, sublinhado ou arroba</mat-hint>
            </mat-form-field>
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Nome</mat-label>
              <input matInput name="name" [(ngModel)]="name" autocomplete="off" />
            </mat-form-field>
          }
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ data.reset ? 'Senha nova' : 'Senha inicial' }}</mat-label>
            <input matInput type="text" name="password" [(ngModel)]="password" autocomplete="off" spellcheck="false" required />
            <mat-hint>Pelo menos {{ minimum }} caracteres. A pessoa troca depois, no menu do usuário.</mat-hint>
          </mat-form-field>
          @if (error()) {
            <p class="error" role="alert"><mat-icon svgIcon="error" /> {{ error() }}</p>
          }
        </div>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancelar</button>
        <button mat-flat-button type="submit" [disabled]="busy() || password.length < minimum || (!data.reset && login.trim().length < 3)">{{ data.reset ? 'Redefinir' : 'Criar' }}</button>
      </mat-dialog-actions>
    </form>
  `,
  styles: STYLES,
})
export class UserDialog {
  private readonly api = inject(Api);
  private readonly reference = inject(MatDialogRef<UserDialog, boolean>);
  protected readonly data = inject<{ reset?: string }>(MAT_DIALOG_DATA);

  protected readonly minimum = MINIMUM;
  protected login = '';
  protected name = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      if (this.data.reset) {
        await firstValueFrom(this.api.resetPassword(this.data.reset, this.password));
      } else {
        await firstValueFrom(this.api.createUser({ login: this.login.trim(), name: this.name.trim(), password: this.password }));
      }
      this.reference.close(true);
    } catch (failure) {
      this.error.set(said(failure));
    } finally {
      this.busy.set(false);
    }
  }
}
