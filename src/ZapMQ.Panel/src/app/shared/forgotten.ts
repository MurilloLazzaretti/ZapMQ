import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { ForgottenItem } from '../core/models';
import { AgoPipe } from '../core/format';

interface ForgottenData {
  items: ForgottenItem[];
  /** The package is still open: what was left out can be put in it from here. */
  canInclude: boolean;
  /** What the person is about to do: close, send or download. */
  action: string;
}

/**
 * Tells what changed here, of the same kind as what is about to go, and is not in the package.
 * Answers: include (open packages only), go on anyway, or cancel.
 */
@Component({
  imports: [MatDialogModule, MatButtonModule, MatIconModule, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Faltou alguma coisa neste pacote?</h2>
    <mat-dialog-content>
      <p>Mudou neste ambiente, num pacote já aplicado, e ainda não foi enviado para lugar nenhum:</p>
      <ul>
        @for (item of data.items; track item.kind + item.name) {
          <li><strong>{{ item.name }}</strong> <span class="muted">· {{ item.package }}, {{ item.changedAt | ago }}</span></li>
        }
      </ul>
      <p class="warning"><mat-icon svgIcon="warning" /> <span>{{ data.canInclude ? 'Se eles dependem uns dos outros, o ambiente de destino pode ficar com uma parte nova e outra antiga.' : 'Este pacote já está fechado e não pode receber itens. Para levar também esses, crie uma versão nova dele.' }}</span></p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancelar</button>
      <button mat-button mat-dialog-close="go">{{ data.action }} assim mesmo</button>
      @if (data.canInclude) {
        <button mat-flat-button mat-dialog-close="include">Incluir esses {{ data.items.length }}</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    p { margin: 0 0 12px; }
    ul { margin: 0 0 12px; padding-left: 20px; }
    .muted { color: var(--mat-sys-on-surface-variant); }
    .warning { display: flex; gap: 10px; padding: 12px 14px; border-radius: 12px; background: var(--zap-warn-container); color: var(--zap-warn); }
    .warning mat-icon { flex: none; }
  `,
})
export class ForgottenDialog {
  protected readonly data = inject<ForgottenData>(MAT_DIALOG_DATA);
}

/**
 * Looks for what was left out and asks. Returns 'go' when nothing was left out or the person
 * says to go on, 'included' when what was left out was put in the open package (which the
 * person should look at before closing), and null to stop.
 */
export async function checkForgotten(dialog: MatDialog, api: Api, where: { draft: string } | { package: string; route?: string }, action: string): Promise<'go' | 'included' | null> {
  let items: ForgottenItem[];
  try {
    items = (await firstValueFrom('draft' in where ? api.draftForgotten(where.draft) : api.packageForgotten(where.package, where.route))).items;
  } catch {
    // The check is a help: when it cannot be made, it does not stand in the way.
    return 'go';
  }
  if (!items.length) {
    return 'go';
  }
  const answer = await firstValueFrom(dialog.open(ForgottenDialog, { data: { items, canInclude: 'draft' in where, action } satisfies ForgottenData, maxWidth: '520px', width: 'calc(100vw - 32px)' }).afterClosed());
  if (answer === 'go') {
    return 'go';
  }
  if (answer === 'include' && 'draft' in where) {
    for (const item of items) {
      await firstValueFrom(api.addRunning(where.draft, item.kind, item.name));
    }
    return 'included';
  }
  return null;
}
