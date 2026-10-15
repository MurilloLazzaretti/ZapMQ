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
import { PackageDraft } from '../core/models';

/**
 * Which open package something goes in: one of those open here, or a new one. Gives the
 * package chosen, or nothing when the question was left without an answer.
 */
@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Em qual pacote?</h2>
    <mat-dialog-content>
      @if (data.drafts.length) {
        <p class="muted">Os pacotes abertos neste ambiente:</p>
        <div class="list" role="radiogroup" aria-label="Pacote aberto">
          @for (draft of data.drafts; track draft.id) {
            <label class="option" [class.on]="chosen() === draft.id">
              <input type="radio" name="draft" [value]="draft.id" [checked]="chosen() === draft.id" (change)="chosen.set(draft.id)" />
              <span><strong>{{ draft.name }}</strong> <span class="pill">v{{ draft.version }}</span>
                <span class="muted">{{ draft.items ?? 0 }} {{ draft.items === 1 ? 'item' : 'itens' }}</span></span>
            </label>
          }
          <label class="option" [class.on]="chosen() === ''">
            <input type="radio" name="draft" value="" [checked]="chosen() === ''" (change)="chosen.set('')" />
            <span><mat-icon svgIcon="add" /> <strong>Um pacote novo</strong></span>
          </label>
        </div>
      } @else {
        <p class="muted">Não há pacote aberto. Crie um: o que for incluído vai nele, e ele é fechado quando estiver pronto.</p>
      }
      @if (chosen() === '') {
        <div class="new">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Nome do pacote</mat-label>
            <input matInput [(ngModel)]="name" placeholder="Fechamento de faturamento por canal" (keydown.enter)="go()" />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>O que muda</mat-label>
            <textarea matInput [(ngModel)]="description" rows="2"></textarea>
            <mat-hint>É o que quem aprova no destino vai ler. Dá para mudar até fechar.</mat-hint>
          </mat-form-field>
        </div>
      }
      @if (problem()) {
        <p class="problem">{{ problem() }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancelar</button>
      <button mat-flat-button (click)="go()" [disabled]="busy() || (chosen() === '' && !name.trim())">{{ chosen() === '' ? 'Criar e incluir' : 'Incluir' }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    p { margin: 0 0 10px; }
    .list { display: grid; gap: 6px; margin-bottom: 12px; }
    .option { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border: 1px solid var(--zap-border); border-radius: 12px; cursor: pointer; }
    .option.on { border-color: var(--mat-sys-primary); background: color-mix(in srgb, var(--mat-sys-primary-container) 40%, transparent); }
    .option mat-icon { width: 18px; height: 18px; vertical-align: middle; }
    .option .muted { margin-left: 6px; font-size: 12.5px; }
    .new { display: grid; gap: 12px; }
    .problem { color: var(--zap-danger); }
  `,
})
export class DraftPicker {
  protected readonly data = inject<{ drafts: PackageDraft[]; current: string | null }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<DraftPicker, PackageDraft>);
  private readonly api = inject(Api);

  protected readonly chosen = signal(this.data.drafts.some((draft) => draft.id === this.data.current) ? this.data.current! : (this.data.drafts[0]?.id ?? ''));
  protected readonly busy = signal(false);
  protected readonly problem = signal('');
  protected name = '';
  protected description = '';

  protected async go(): Promise<void> {
    if (this.chosen()) {
      this.ref.close(this.data.drafts.find((draft) => draft.id === this.chosen()));
      return;
    }
    if (!this.name.trim()) {
      return;
    }
    this.busy.set(true);
    try {
      this.ref.close(await firstValueFrom(this.api.createDraft({ name: this.name.trim(), description: this.description.trim() })));
    } catch (failure) {
      this.problem.set((failure as HttpErrorResponse).error?.error ?? 'O pacote não pôde ser criado');
    } finally {
      this.busy.set(false);
    }
  }
}
