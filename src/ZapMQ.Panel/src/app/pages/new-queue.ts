import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

/** Asks for the name of a queue to define; the definition itself is edited on the queue's screen. */
@Component({
  imports: [FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Definir fila</h2>
    <mat-dialog-content>
      <p class="muted">
        Uma fila não precisa ser criada: ela passa a existir no primeiro envio. Defina uma fila quando quiser que ela se comporte de forma
        diferente do padrão.
      </p>
      <mat-form-field>
        <mat-label>Nome da fila</mat-label>
        <input matInput [(ngModel)]="name" cdkFocusInitial spellcheck="false" autocapitalize="none" (keyup.enter)="submit()" />
        <mat-hint>Diferencia maiúsculas de minúsculas.</mat-hint>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancelar</button>
      <button mat-flat-button (click)="submit()" [disabled]="!name.trim()">Continuar</button>
    </mat-dialog-actions>
  `,
  styles: `mat-form-field { width: 100%; } p { margin: 0 0 16px; }`,
})
export class NewQueueDialog {
  private readonly reference = inject(MatDialogRef<NewQueueDialog, string>);
  protected name = '';

  protected submit(): void {
    if (this.name.trim()) {
      this.reference.close(this.name.trim());
    }
  }
}
