import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Api } from '../core/api';
import { InstalledService, ServiceConfig } from '../core/models';

/** What whoever chose services to watch decided: which ones, and where suggestions come from. */
export interface ServiceChoice {
  names: string[];
  suggestFrom: string[];
}

const under = (path: string | null, folders: string[]) => {
  if (!path) {
    return false;
  }
  const file = path.replace(/\//g, '\\').toLowerCase();
  return folders.some((folder) => file.startsWith(folder.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase() + '\\'));
};

/**
 * The services installed on the machine of the Worker Control, to pick the ones to watch.
 * Nobody has to know or type the name of a service: the ones from the folders of the
 * applications come first.
 */
@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Adicionar serviços</h2>
    <mat-dialog-content>
      <div class="folders">
        <span class="muted">Sugerir os serviços instalados em</span>
        @for (folder of folders(); track folder) {
          <span class="pill"><mat-icon svgIcon="folder" /> <span class="mono">{{ folder }}</span>
            <button type="button" (click)="removeFolder(folder)" [attr.aria-label]="'Deixar de sugerir de ' + folder"><mat-icon svgIcon="close" /></button>
          </span>
        }
        <form class="add" (submit)="addFolder(); $event.preventDefault()">
          <input [(ngModel)]="folder" name="folder" placeholder="D:\\Apps" aria-label="Pasta para sugerir serviços" spellcheck="false" />
          <button mat-button type="submit" [disabled]="!folder.trim()">Adicionar pasta</button>
        </form>
      </div>

      <mat-form-field class="search" subscriptSizing="dynamic">
        <mat-icon matPrefix svgIcon="search" />
        <input matInput placeholder="Buscar por nome ou caminho" [ngModel]="filter()" (ngModelChange)="filter.set($event)" aria-label="Buscar serviço" />
      </mat-form-field>

      @if (problem()) {
        <p class="empty"><mat-icon svgIcon="cloud_off" /> {{ problem() }}</p>
      } @else if (!services()) {
        <p class="muted">Perguntando à máquina quais serviços ela tem…</p>
      } @else {
        @for (section of sections(); track section.title) {
          @if (section.items.length) {
            <h3>{{ section.title }} <span class="muted">{{ section.items.length }}</span></h3>
            <ul>
              @for (service of section.items; track service.Name) {
                <li [class.watched]="service.Watched">
                  <mat-checkbox [checked]="service.Watched || chosen().has(service.Name)" [disabled]="service.Watched" (change)="toggle(service.Name)">
                    <span class="who">
                      <strong>{{ service.DisplayName }}</strong>
                      <span class="muted mono">{{ service.Name }}{{ service.ExecutablePath ? ' · ' + service.ExecutablePath : '' }}</span>
                    </span>
                  </mat-checkbox>
                  <span class="pill" [class.ok]="service.State === 'Running'">{{ service.Watched ? 'já acompanhado' : state(service) }}</span>
                </li>
              }
            </ul>
          }
        }
        @if (hiddenSystem()) {
          <button mat-button (click)="showSystem.set(true)">Mostrar também os {{ hiddenSystem() }} serviços do Windows</button>
        }
        @if (!shown()) {
          <p class="muted">Nenhum serviço com esse texto.</p>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancelar</button>
      <button mat-flat-button (click)="done()" [disabled]="!chosen().size && !foldersChanged()">
        {{ chosen().size ? 'Acompanhar ' + chosen().size + (chosen().size === 1 ? ' serviço' : ' serviços') : 'Gravar as pastas' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    mat-dialog-content { display: grid; gap: 12px; min-height: 320px; align-content: start; }
    .folders { display: flex; align-items: center; gap: 6px 8px; flex-wrap: wrap; font: var(--mat-sys-body-small); }
    .folders .pill button { display: grid; place-items: center; border: none; background: none; color: inherit; cursor: pointer; padding: 0; }
    .folders .pill mat-icon { width: 14px; height: 14px; }
    .add { display: inline-flex; align-items: center; gap: 4px; }
    .add input { width: 160px; padding: 6px 10px; border-radius: 999px; border: 1px solid var(--zap-border); background: transparent; color: inherit; font: inherit; font-family: var(--zap-mono); }
    .search { width: 100%; }
    h3 { margin: 4px 0 -6px; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-label-medium); letter-spacing: 0.04em; text-transform: uppercase; }
    ul { list-style: none; margin: 0; padding: 0; display: grid; }
    li { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 8px; padding: 2px 0; border-bottom: 1px solid var(--zap-border); }
    li.watched { opacity: 0.65; }
    .who { display: grid; min-width: 0; line-height: 1.3; }
    .who .muted { font-size: 11.5px; overflow-wrap: anywhere; }
    .empty { display: flex; gap: 8px; align-items: center; }
  `,
})
export class AddServiceDialog implements OnInit {
  private readonly api = inject(Api);
  private readonly reference = inject<MatDialogRef<AddServiceDialog, ServiceChoice>>(MatDialogRef);

  protected readonly services = signal<InstalledService[] | null>(null);
  protected readonly problem = signal('');
  protected readonly filter = signal('');
  protected readonly chosen = signal<ReadonlySet<string>>(new Set());
  protected readonly folders = signal<string[]>([]);
  protected readonly showSystem = signal(false);
  protected folder = '';
  private original: string[] = [];

  protected readonly foldersChanged = computed(() => this.folders().join('|') !== this.original.join('|'));

  private readonly matching = computed(() => {
    const wanted = this.filter().trim().toLowerCase();
    const folders = this.folders();
    return (this.services() ?? [])
      .map((service) => ({ ...service, Suggested: under(service.ExecutablePath, folders) }))
      .filter((service) => !wanted || `${service.Name} ${service.DisplayName} ${service.ExecutablePath ?? ''}`.toLowerCase().includes(wanted));
  });

  protected readonly sections = computed(() => {
    const all = this.matching();
    // Looking for something by name finds it wherever it is.
    const system = this.showSystem() || !!this.filter().trim();
    return [
      { title: 'Sugeridos', items: all.filter((service) => service.Suggested) },
      { title: 'Outros', items: all.filter((service) => !service.Suggested && !service.System) },
      { title: 'Do Windows', items: system ? all.filter((service) => !service.Suggested && service.System) : [] },
    ];
  });

  protected readonly shown = computed(() => this.sections().reduce((total, section) => total + section.items.length, 0));
  protected readonly hiddenSystem = computed(() =>
    this.showSystem() || this.filter().trim() ? 0 : this.matching().filter((service) => !service.Suggested && service.System).length,
  );

  ngOnInit(): void {
    this.api.installedServices().subscribe({
      next: (answer) => {
        this.original = answer.SuggestFrom;
        this.folders.set(answer.SuggestFrom);
        this.services.set(answer.Services);
      },
      error: (failure: HttpErrorResponse) =>
        this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior ao acompanhamento de serviços.' : (failure.error?.error ?? 'O Worker Control não respondeu.')),
    });
  }

  protected state(service: InstalledService): string {
    return service.State === 'Running' ? 'rodando' : service.State === 'Stopped' ? 'parado' : service.State.toLowerCase();
  }

  protected toggle(name: string): void {
    const chosen = new Set(this.chosen());
    if (!chosen.delete(name)) {
      chosen.add(name);
    }
    this.chosen.set(chosen);
  }

  protected addFolder(): void {
    const folder = this.folder.trim();
    if (folder && !this.folders().some((existing) => existing.toLowerCase() === folder.toLowerCase())) {
      this.folders.update((folders) => [...folders, folder]);
    }
    this.folder = '';
  }

  protected removeFolder(folder: string): void {
    this.folders.update((folders) => folders.filter((existing) => existing !== folder));
  }

  protected done(): void {
    this.reference.close({ names: [...this.chosen()], suggestFrom: this.folders() });
  }
}

/**
 * How one service is watched: whether it is started again when it falls, how long it is
 * waited for when asked to stop, what proves it is well, and where its log is.
 */
@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <mat-slide-toggle [(ngModel)]="autoRestart">Iniciar de novo quando cair</mat-slide-toggle>
      <p class="muted hint">Só quando o serviço para sozinho com erro. Um serviço que alguém parou continua parado.</p>

      <mat-form-field>
        <mat-label>Prazo para parar</mat-label>
        <input matInput type="number" min="1" [(ngModel)]="stopSeconds" />
        <span matTextSuffix>segundos</span>
        <mat-hint>Passado o prazo, o painel avisa. O serviço não é encerrado à força.</mat-hint>
      </mat-form-field>

      <div class="block">
        <span class="label">Verificação</span>
        <mat-button-toggle-group [(ngModel)]="check" hideSingleSelectionIndicator aria-label="Verificação">
          <mat-button-toggle value="none">Nenhuma</mat-button-toggle>
          <mat-button-toggle value="tcp">Porta</mat-button-toggle>
          <mat-button-toggle value="url">Endereço</mat-button-toggle>
        </mat-button-toggle-group>
        <p class="muted hint">Para saber se o serviço, além de rodando, está respondendo. Feita a cada 15 segundos.</p>
        @if (check === 'tcp') {
          <mat-form-field>
            <mat-label>Máquina e porta</mat-label>
            <input matInput [(ngModel)]="target" placeholder="localhost:3000" spellcheck="false" />
            <mat-hint>Precisa aceitar uma conexão.</mat-hint>
          </mat-form-field>
        } @else if (check === 'url') {
          <mat-form-field>
            <mat-label>Endereço</mat-label>
            <input matInput [(ngModel)]="target" placeholder="http://localhost:3000/health" spellcheck="false" />
            <mat-hint>Precisa responder com sucesso (2xx).</mat-hint>
          </mat-form-field>
        }
      </div>

      <mat-form-field>
        <mat-label>Arquivos de log</mat-label>
        <input matInput [(ngModel)]="logFiles" placeholder="D:\\Apps\\servico\\logs\\app-*.log" spellcheck="false" />
        <mat-hint>Pasta e padrão do nome, para acompanhar o log ao vivo.</mat-hint>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancelar</button>
      <button mat-flat-button (click)="save()" [disabled]="check !== 'none' && !target.trim()">Gravar</button>
    </mat-dialog-actions>
  `,
  styles: `
    mat-dialog-content { display: grid; gap: 14px; }
    mat-form-field { width: 100%; }
    .hint { margin: -10px 0 0; font: var(--mat-sys-body-small); }
    .block { display: grid; gap: 12px; justify-items: start; }
    .block mat-form-field { justify-self: stretch; }
    .block .hint { margin: -6px 0 0; }
    .label { font: var(--mat-sys-label-large); }
  `,
})
export class ServiceSettingsDialog {
  protected readonly data = inject<{ title: string; config: ServiceConfig }>(MAT_DIALOG_DATA);
  private readonly reference = inject<MatDialogRef<ServiceSettingsDialog, ServiceConfig>>(MatDialogRef);

  protected autoRestart = this.data.config.AutoRestart ?? false;
  protected stopSeconds = Math.round((this.data.config.StopTimeoutMs ?? 30000) / 1000);
  protected check: 'none' | 'tcp' | 'url' = this.data.config.Check?.Tcp ? 'tcp' : this.data.config.Check?.Url ? 'url' : 'none';
  protected target = this.data.config.Check?.Tcp ?? this.data.config.Check?.Url ?? '';
  protected logFiles = this.data.config.LogFiles ?? '';

  protected save(): void {
    // What is not set leaves the file, instead of going as an empty value.
    const config: ServiceConfig = { ...this.data.config, Name: this.data.config.Name };
    delete config.AutoRestart;
    delete config.StopTimeoutMs;
    delete config.Check;
    delete config.LogFiles;
    if (this.autoRestart) {
      config.AutoRestart = true;
    }
    if (this.stopSeconds && this.stopSeconds !== 30) {
      config.StopTimeoutMs = Math.max(1, this.stopSeconds) * 1000;
    }
    if (this.check === 'tcp') {
      config.Check = { Tcp: this.target.trim() };
    } else if (this.check === 'url') {
      config.Check = { Url: this.target.trim() };
    }
    if (this.logFiles.trim()) {
      config.LogFiles = this.logFiles.trim();
    }
    this.reference.close(config);
  }
}
