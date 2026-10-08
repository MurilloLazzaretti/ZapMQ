import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';

export interface Confirmation {
  title: string;
  message: string;
  /** Something the user should think about before going on. */
  warning?: string;
  action: string;
  danger?: boolean;
}

@Component({
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p>{{ data.message }}</p>
      @if (data.warning) {
        <p class="warning"><mat-icon svgIcon="warning" /> <span>{{ data.warning }}</span></p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancelar</button>
      <button mat-flat-button [mat-dialog-close]="true" [class.danger]="data.danger">{{ data.action }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    p { margin: 0 0 12px; }
    .warning {
      display: flex;
      gap: 10px;
      padding: 12px 14px;
      border-radius: 12px;
      background: var(--zap-warn-container);
      color: var(--zap-warn);
    }
    .warning mat-icon { flex: none; }
    .danger { --mat-button-filled-container-color: var(--zap-danger); --mat-button-filled-label-text-color: var(--mat-sys-surface); }
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<Confirmation>(MAT_DIALOG_DATA);
}

export interface Shown {
  title: string;
  subtitle?: string;
  json: unknown;
}

@Component({
  imports: [MatDialogModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      @if (data.subtitle) {
        <p class="muted mono">{{ data.subtitle }}</p>
      }
      <pre class="json">{{ text }}</pre>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="copy()">{{ copied ? 'Copiado' : 'Copiar' }}</button>
      <button mat-flat-button mat-dialog-close>Fechar</button>
    </mat-dialog-actions>
  `,
  styles: `p { margin: 0 0 12px; word-break: break-all; }`,
})
export class JsonDialog {
  protected readonly data = inject<Shown>(MAT_DIALOG_DATA);
  protected readonly text = JSON.stringify(this.data.json, null, 2);
  protected copied = false;

  protected copy(): void {
    void navigator.clipboard?.writeText(this.text).then(() => (this.copied = true));
  }
}

export function confirm(dialog: MatDialog, data: Confirmation): Promise<boolean> {
  return firstValueFrom(dialog.open(ConfirmDialog, { data, maxWidth: '480px', width: 'calc(100vw - 32px)' }).afterClosed()).then(Boolean);
}

export function showJson(dialog: MatDialog, data: Shown): void {
  dialog.open(JsonDialog, { data, maxWidth: '760px', width: 'calc(100vw - 32px)' });
}
