import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { MessageModel, PublishResult } from '../core/models';

export interface PublishData {
  /** Empty lets the queue be typed. */
  queue: string;
  /** What the editor starts with: the body of a message being reused, for one. */
  body?: unknown;
}

/**
 * A message written by hand: its body edited as JSON, loaded from a file or from a model
 * saved before, and published in a queue. A question (RPC) waits for its answer and shows it.
 */
@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule, MatMenuModule, MatSlideToggleModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Publicar mensagem</h2>
    <mat-dialog-content>
      <mat-form-field class="queue" subscriptSizing="dynamic">
        <mat-label>Fila</mat-label>
        <input matInput [(ngModel)]="queue" (ngModelChange)="queueChanged()" [readonly]="!!data.queue" spellcheck="false" placeholder="nome da fila" />
      </mat-form-field>

      <div class="bar">
        <button mat-button [matMenuTriggerFor]="modelMenu" [disabled]="!queue.trim()"><mat-icon svgIcon="bookmark" /> Modelos{{ models().length ? ' (' + models().length + ')' : '' }}</button>
        <mat-menu #modelMenu="matMenu">
          @for (model of models(); track model.name) {
            <div class="model">
              <button mat-menu-item (click)="use(model)">{{ model.name }}</button>
              <button mat-icon-button (click)="removeModel(model); $event.stopPropagation()" [attr.aria-label]="'Apagar o modelo ' + model.name"><mat-icon svgIcon="delete" /></button>
            </div>
          } @empty {
            <button mat-menu-item disabled>Nenhum modelo desta fila ainda</button>
          }
          <button mat-menu-item (click)="naming.set(true)" [disabled]="!!problem()"><mat-icon svgIcon="save" /> <span>Salvar o que está no editor como modelo…</span></button>
        </mat-menu>
        <button mat-button (click)="file.click()"><mat-icon svgIcon="folder" /> Abrir arquivo</button>
        <input #file type="file" accept=".json,application/json,text/plain" hidden (change)="load($event)" />
        <button mat-button (click)="download()" [disabled]="!!problem()"><mat-icon svgIcon="download" /> Salvar arquivo</button>
        <span class="spacer"></span>
        <button mat-button (click)="format()" [disabled]="!!problem()" matTooltip="Arruma os recuos do JSON"><mat-icon svgIcon="data_object" /> Formatar</button>
      </div>

      @if (naming()) {
        <form class="naming" (submit)="saveModel(); $event.preventDefault()">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Nome do modelo</mat-label>
            <input matInput [(ngModel)]="modelName" name="modelName" placeholder="Pedido de teste" />
          </mat-form-field>
          <button mat-flat-button type="submit" [disabled]="!modelName.trim() || !!problem()">Salvar</button>
          <button mat-button type="button" (click)="naming.set(false)">Cancelar</button>
        </form>
      }

      <textarea class="mono" [class.bad]="!!problem()" [(ngModel)]="text" (ngModelChange)="typed()" spellcheck="false" aria-label="Corpo da mensagem, em JSON" (keydown.tab)="indent($event)"></textarea>
      <p class="status" [class.bad]="!!problem()">{{ problem() || 'JSON válido · ' + size() }}</p>

      <div class="options">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Validade</mat-label>
          <input matInput type="number" min="0" [(ngModel)]="ttlSeconds" />
          <span matTextSuffix>segundos</span>
          <mat-hint>0 = sem validade própria</mat-hint>
        </mat-form-field>
        <mat-slide-toggle [(ngModel)]="rpc" matTooltip="A mensagem espera uma resposta de quem a processar">Esperar resposta (RPC)</mat-slide-toggle>
      </div>

      @if (asking()) {
        <p class="result info">Publicada; esperando a resposta…</p>
      } @else if (result(); as done) {
        <div class="result" [class.warn]="done.rpc && !done.answered">
          <strong>Publicada</strong> <span class="mono">{{ done.id }}</span>
          @if (done.rpc && done.answered) {
            <span>Resposta:</span>
            <pre class="json">{{ response() }}</pre>
          } @else if (done.rpc) {
            <span>Ninguém respondeu no prazo.</span>
          }
        </div>
      } @else if (failure()) {
        <p class="result bad">{{ failure() }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (arming()) {
        <span class="sure">A mensagem será processada de verdade por quem consome <strong>{{ queue }}</strong>.</span>
        <button mat-button (click)="arming.set(false)">Voltar</button>
        <button mat-flat-button class="go" (click)="publish()"><mat-icon svgIcon="send" /> Publicar agora</button>
      } @else {
        <button mat-button mat-dialog-close>Fechar</button>
        <button mat-flat-button (click)="arming.set(true)" [disabled]="!!problem() || !queue.trim() || asking()"><mat-icon svgIcon="send" /> Publicar</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    mat-dialog-content { display: grid; gap: 10px; align-content: start; padding-top: 8px !important; }
    .queue { width: 100%; }
    .bar { display: flex; align-items: center; gap: 2px 4px; flex-wrap: wrap; }
    .bar .spacer { flex: 1; }
    textarea {
      width: 100%;
      min-height: min(42vh, 360px);
      padding: 12px 14px;
      border: 1px solid var(--zap-border);
      border-radius: 12px;
      background: var(--mat-sys-surface-container-lowest);
      color: inherit;
      line-height: 1.5;
      tab-size: 2;
      resize: vertical;
      outline: none;
    }
    textarea:focus { border-color: var(--mat-sys-primary); }
    textarea.bad { border-color: var(--zap-danger); }
    .status { margin: -4px 0 0; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-body-small); }
    .status.bad { color: var(--zap-danger); }
    .options { display: flex; align-items: center; gap: 16px 24px; flex-wrap: wrap; }
    .options mat-form-field { width: 180px; }
    .result { display: grid; gap: 4px; margin: 0; padding: 10px 12px; border-radius: 12px; background: var(--zap-ok-container); color: var(--zap-ok); overflow-wrap: anywhere; }
    .result.warn { background: var(--zap-warn-container); color: var(--zap-warn); }
    .result.bad { background: var(--zap-danger-container); color: var(--zap-danger); }
    .result.info { background: var(--zap-info-container); color: var(--zap-info); }
    .result pre { margin: 4px 0 0; max-height: 220px; overflow: auto; color: var(--mat-sys-on-surface); }
    .sure { flex: 1; color: var(--zap-warn); font: var(--mat-sys-body-small); text-align: left; padding: 0 8px; }
    .go { --mat-button-filled-container-color: var(--zap-warn); --mat-button-filled-label-text-color: var(--mat-sys-surface); }
    .naming { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .naming mat-form-field { flex: 1 1 220px; }
    .model { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; min-width: 240px; }
  `,
})
export class PublishDialog implements OnInit {
  protected readonly data = inject<PublishData>(MAT_DIALOG_DATA);
  private readonly api = inject(Api);

  protected queue = this.data.queue;
  protected text = this.data.body === undefined ? '{\n  \n}' : JSON.stringify(this.data.body, null, 2);
  protected ttlSeconds = 0;
  protected rpc = false;

  protected readonly problem = signal('');
  protected readonly size = signal('');
  protected readonly models = signal<MessageModel[]>([]);
  protected readonly arming = signal(false);
  protected readonly naming = signal(false);
  protected modelName = '';
  protected readonly asking = signal(false);
  protected readonly result = signal<PublishResult | null>(null);
  protected readonly failure = signal('');
  protected readonly response = computed(() => JSON.stringify(this.result()?.response ?? null, null, 2));
  private loaded = '';

  ngOnInit(): void {
    this.typed();
    this.queueChanged();
  }

  protected typed(): void {
    this.arming.set(false);
    try {
      JSON.parse(this.text);
      this.problem.set('');
      const bytes = new Blob([this.text]).size;
      this.size.set(bytes < 1024 ? `${bytes} bytes` : `${(bytes / 1024).toFixed(1).replace('.', ',')} KB`);
    } catch (error) {
      this.problem.set('O texto não é um JSON válido: ' + (error as Error).message);
    }
  }

  /** The models are of the queue: another queue, other models. */
  protected queueChanged(): void {
    const queue = this.queue.trim();
    if (!queue || queue === this.loaded) {
      return;
    }
    this.loaded = queue;
    this.api.messageModels(queue).subscribe({ next: (models) => this.queue.trim() === queue && this.models.set(models), error: () => this.models.set([]) });
  }

  protected use(model: MessageModel): void {
    this.text = JSON.stringify(model.body, null, 2);
    this.ttlSeconds = Math.round(model.ttlMs / 1000);
    this.rpc = model.rpc;
    this.modelName = model.name;
    this.typed();
  }

  protected saveModel(): void {
    const name = this.modelName.trim();
    if (!name) {
      return;
    }
    this.api.saveMessageModel(this.queue.trim(), name, { body: JSON.parse(this.text), ttlMs: Math.max(0, this.ttlSeconds) * 1000, rpc: this.rpc }).subscribe({
      next: (models) => {
        this.models.set(models);
        this.naming.set(false);
        this.modelName = '';
      },
      error: (failure: HttpErrorResponse) => this.failure.set(failure.error?.error ?? 'O modelo não pôde ser salvo.'),
    });
  }

  protected removeModel(model: MessageModel): void {
    this.api.removeMessageModel(this.queue.trim(), model.name).subscribe({ next: (models) => this.models.set(models), error: () => undefined });
  }

  protected format(): void {
    this.text = JSON.stringify(JSON.parse(this.text), null, 2);
    this.typed();
  }

  /** Tab writes two spaces instead of leaving the editor. */
  protected indent(event: Event): void {
    event.preventDefault();
    const area = event.target as HTMLTextAreaElement;
    const { selectionStart, selectionEnd } = area;
    this.text = area.value.slice(0, selectionStart) + '  ' + area.value.slice(selectionEnd);
    queueMicrotask(() => area.setSelectionRange(selectionStart + 2, selectionStart + 2));
    this.typed();
  }

  protected async load(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) {
      this.text = await file.text();
      this.typed();
    }
    input.value = '';
  }

  protected download(): void {
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([this.text + '\n'], { type: 'application/json;charset=utf-8' }));
    link.download = `${(this.queue.trim() || 'mensagem').replace(/[^\w.-]+/g, '_')}.json`;
    link.click();
    URL.revokeObjectURL(link.href);
  }

  protected async publish(): Promise<void> {
    this.arming.set(false);
    this.result.set(null);
    this.failure.set('');
    this.asking.set(this.rpc);
    try {
      this.result.set(await firstValueFrom(this.api.publishMessage(this.queue.trim(), { body: JSON.parse(this.text), ttlMs: Math.max(0, this.ttlSeconds) * 1000, rpc: this.rpc })));
    } catch (failure) {
      this.failure.set((failure as HttpErrorResponse).error?.error ?? 'A mensagem não pôde ser publicada.');
    } finally {
      this.asking.set(false);
    }
  }
}

export function openPublish(dialog: MatDialog, data: PublishData): void {
  dialog.open(PublishDialog, { data, maxWidth: '820px', width: 'calc(100vw - 32px)', autoFocus: 'textarea' });
}
