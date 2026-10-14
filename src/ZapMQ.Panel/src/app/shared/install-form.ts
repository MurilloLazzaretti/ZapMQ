import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApplicationDefaults, InstallChoice, PackageItem } from '../core/models';
import { targetIcon, targetName } from '../core/transport';
import { copyText } from '../core/clipboard';

/** The lines of the proxy of the other environment, with its ports replaced one for one by the ones chosen here. */
export function adaptProxy(proxy: string, from: number[], to: number[]): string {
  let text = proxy;
  from.forEach((port, index) => {
    if (index < to.length) {
      text = text.replace(new RegExp(`(?<=[:\\s])${port}\\b`, 'g'), `\u0001${index}\u0001`);
    }
  });
  to.forEach((port, index) => (text = text.split(`\u0001${index}\u0001`).join(String(port))));
  return text;
}

/**
 * How an application this environment does not have is to be installed from a package: what is of
 * this environment (the folder, the instances and ports, the service) and the configuration, which
 * came without the secrets of the other one.
 */
@Component({
  selector: 'zap-install-form',
  imports: [FormsModule, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="install">
      <div class="title"><mat-icon [svgIcon]="targetIcon(item().kind)" /><strong>{{ item().name }}</strong><span class="muted">· {{ targetName(item().kind) }} que {{ environment() }} ainda não tem</span></div>
      <div class="fields">
        <label class="wide">Pasta<input class="mono" [ngModel]="choice().folder" (ngModelChange)="set('folder', $event)" spellcheck="false" /></label>
        @if (item().kind !== 'api') {
          <label class="wide">Executável, dentro da pasta<input class="mono" [ngModel]="choice().executable" (ngModelChange)="set('executable', $event)" spellcheck="false" /></label>
        }
        @switch (item().kind) {
          @case ('worker') {
            <label>Processos<input type="number" min="0" max="16" [ngModel]="choice().instances" (ngModelChange)="set('instances', +$event)" /></label>
          }
          @case ('api') {
            <label>Instâncias<input type="number" min="1" max="16" [ngModel]="choice().instances" (ngModelChange)="set('instances', +$event)" /></label>
            <label>Primeira porta<input type="number" min="1" max="65535" [ngModel]="choice().port || null" (ngModelChange)="set('port', +$event)" /></label>
            <label class="wide">Nome dos sites<input [ngModel]="choice().siteName" (ngModelChange)="set('siteName', $event)" spellcheck="false" /></label>
            <label class="check wide"><input type="checkbox" [ngModel]="choice().sharedFolder" (ngModelChange)="set('sharedFolder', $event)" /> As instâncias servem a mesma pasta</label>
            @if (busyPorts().length) { <p class="muted hint wide">Portas que estão em uso nesta máquina: {{ busyPorts().join(', ') }}.</p> }
          }
          @case ('service') {
            <label class="wide">Nome de exibição<input [ngModel]="choice().displayName" (ngModelChange)="set('displayName', $event)" /></label>
            <label>Início
              <select [ngModel]="choice().startType" (ngModelChange)="set('startType', $event)">
                <option value="Automatic">Automático</option>
                <option value="Manual">Manual</option>
              </select>
            </label>
          }
        }
      </div>

      @for (setting of choice().settings; track setting.path; let index = $index) {
        <label class="setting">
          <span><span class="mono">{{ setting.path }}</span>
            @if (blanked(setting.path).length) { <span class="warn"> · em branco, para preencher: {{ blanked(setting.path).join(', ') }}</span> }
          </span>
          <textarea class="mono" spellcheck="false" [ngModel]="setting.content" (ngModelChange)="setSetting(index, $event)"></textarea>
        </label>
      }

      @if (proxy(); as lines) {
        <div class="proxy">
          <span><strong>Proxy reverso</strong> <span class="muted">· como está em {{ origin() }}, com as portas escolhidas aqui. Inclua pela aba Proxy reverso, que testa antes de recarregar.</span></span>
          <pre class="mono">{{ lines }}</pre>
          <button mat-button (click)="copy(lines)"><mat-icon svgIcon="content_copy" /> Copiar</button>
        </div>
      }
    </div>
  `,
  styles: `
    .install { display: grid; gap: 12px; padding: 14px 20px; border-top: 1px solid var(--zap-border); }
    .title { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .title mat-icon { color: var(--mat-sys-primary); }
    .fields { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 10px 14px; }
    .fields .wide { grid-column: span 2; }
    label { display: grid; gap: 4px; font: var(--mat-sys-label-medium); color: var(--mat-sys-on-surface-variant); }
    label.check { display: flex; align-items: center; gap: 8px; }
    input:not([type=checkbox]), select, textarea {
      min-width: 0; padding: 8px 12px; border: 1px solid var(--zap-border); border-radius: 10px;
      background: var(--zap-surface); color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium);
    }
    textarea { min-height: 140px; font-family: var(--zap-mono); font-size: 12.5px; resize: vertical; }
    .warn { color: var(--zap-warn); }
    .hint { margin: 0; font: var(--mat-sys-body-small); }
    .proxy { display: grid; gap: 6px; }
    pre { margin: 0; padding: 10px 12px; border-radius: 10px; background: var(--mat-sys-surface-container); overflow-x: auto; font-size: 12.5px; }
    .proxy button { justify-self: start; }
    @media (max-width: 600px) { .fields .wide { grid-column: auto; } }
  `,
})
export class InstallForm implements OnInit {
  private readonly snack = inject(MatSnackBar);

  readonly item = input.required<PackageItem>();
  readonly defaults = input<ApplicationDefaults | null>(null);
  readonly environment = input('');
  readonly origin = input('');
  readonly changed = output<InstallChoice>();

  protected readonly targetIcon = targetIcon;
  protected readonly targetName = targetName;
  protected readonly choice = signal<InstallChoice>({ folder: '', executable: null, instances: 1, port: 0, siteName: null, sharedFolder: false, displayName: null, startType: 'Automatic', settings: [] });

  protected readonly busyPorts = computed(() => this.defaults()?.Listening ?? []);

  protected readonly proxy = computed(() => {
    const install = this.item().install;
    const choice = this.choice();
    if (!install?.proxy) {
      return null;
    }
    const ports = choice.port > 0 ? Array.from({ length: choice.instances }, (_, index) => choice.port + index) : [];
    return ports.length ? adaptProxy(install.proxy, install.ports, ports) : install.proxy;
  });

  ngOnInit(): void {
    const item = this.item();
    const install = item.install!;
    const defaults = this.defaults();
    const base = defaults?.Folders?.[item.kind];
    const separator = base?.includes('/') && !base.includes('\\') ? '/' : '\\';
    // What was said before, when the approval is being looked at again; otherwise what is usual here.
    this.choice.set(item.chosen ?? {
      folder: base ? `${base.replace(/[\\/]+$/, '')}${separator}${item.name}` : '',
      executable: install.executable,
      instances: install.instances || 1,
      port: 0,
      siteName: defaults?.SiteName ?? '{name} {n}',
      sharedFolder: !!defaults?.SharedFolder,
      displayName: install.displayName ?? item.name,
      startType: install.startType === 'Manual' ? 'Manual' : 'Automatic',
      settings: install.settings.map((setting) => ({ path: setting.path, content: setting.content })),
    });
    this.changed.emit(this.choice());
  }

  protected blanked(path: string): string[] {
    return this.item().install?.settings.find((setting) => setting.path === path)?.blanked ?? [];
  }

  protected set<K extends keyof InstallChoice>(key: K, value: InstallChoice[K]): void {
    this.choice.update((now) => ({ ...now, [key]: value }));
    this.changed.emit(this.choice());
  }

  protected setSetting(index: number, content: string): void {
    this.choice.update((now) => ({ ...now, settings: now.settings.map((setting, at) => (at === index ? { ...setting, content } : setting)) }));
    this.changed.emit(this.choice());
  }

  protected async copy(text: string): Promise<void> {
    const done = await copyText(text);
    this.snack.open(done ? 'Copiado' : 'Não foi possível copiar: selecione o texto e use Ctrl+C', undefined, { duration: done ? 2000 : 5000 });
  }
}
