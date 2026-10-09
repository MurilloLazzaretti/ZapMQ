import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { size } from '../core/database';
import { NumPipe } from '../core/format';
import { ApplicationDefaults, CreatedApplication, InspectedFiles } from '../core/models';
import { targetIcon, targetName } from '../core/transport';

type Kind = 'worker' | 'api' | 'service';

const STEPS = ['Tipo', 'Binários', 'Parâmetros', 'Configuração', 'Revisão'];
const NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/**
 * Creating on this machine an application it does not have yet, in steps: what it is, its
 * files, where and how it runs, its configuration, and a last look before it is made.
 */
@Component({
  selector: 'zap-app-new',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app-new.html',
  styleUrl: './app-new.scss',
})
export class AppNewPage implements OnInit {
  private readonly api = inject(Api);

  protected readonly steps = STEPS;
  protected readonly kinds: Kind[] = ['worker', 'api', 'service'];
  protected readonly targetName = targetName;
  protected readonly targetIcon = targetIcon;
  protected readonly size = size;

  protected readonly defaults = signal<ApplicationDefaults | null>(null);
  protected readonly problem = signal('');
  protected readonly step = signal(0);
  protected readonly kind = signal<Kind>('worker');
  protected readonly busy = signal(false);
  protected readonly uploadProblem = signal('');
  protected readonly inspected = signal<InspectedFiles | null>(null);
  protected readonly fileName = signal('');
  protected readonly result = signal<CreatedApplication | null>(null);
  protected readonly failure = signal('');

  protected readonly name = signal('');
  /** Typed by hand, the folder stops following the name. */
  private readonly folderTyped = signal<string | null>(null);
  protected readonly executable = signal('');
  protected readonly instances = signal(2);
  protected readonly port = signal<number | null>(null);
  protected readonly siteName = signal('{name} {n}');
  protected readonly displayName = signal('');
  protected readonly startType = signal('Automatic');
  protected readonly settings = signal<{ path: string; content: string; original: string }[]>([]);
  protected readonly openSetting = signal(0);

  protected readonly folder = computed(() => {
    const typed = this.folderTyped();
    if (typed !== null) {
      return typed;
    }
    const base = this.defaults()?.Folders[this.kind()] ?? '';
    const name = this.name().trim();
    if (!base || !name) {
      return base;
    }
    const slash = base.includes('\\') || !base.includes('/') ? '\\' : '/';
    return base.replace(/[\\/]+$/, '') + slash + name;
  });

  private readonly slash = computed(() => (this.folder().includes('\\') || !this.folder().includes('/') ? '\\' : '/'));

  /** The sites that would be made, each with its port and its folder. */
  protected readonly sites = computed(() => {
    const first = this.port();
    return Array.from({ length: Math.max(0, Math.min(16, this.instances())) }, (_, index) => ({
      name: this.siteName().replace(/\{name\}/gi, this.name().trim()).replace(/\{n\}/gi, String(index + 1)),
      port: first === null ? null : first + index,
      folder: this.folder() + this.slash() + (index + 1),
    }));
  });

  /** What is wrong with what was typed, in the order of the fields; nothing when it can go on. */
  protected readonly wrong = computed(() => {
    const defaults = this.defaults();
    const name = this.name().trim();
    const kind = this.kind();
    if (!name) {
      return 'Dê um nome à aplicação.';
    }
    if (!NAME.test(name)) {
      return 'O nome aceita letras, números, ponto, hífen e sublinhado, e começa com letra ou número.';
    }
    if (!this.folder().trim()) {
      return 'Informe a pasta onde a aplicação fica.';
    }
    if (kind !== 'api' && !this.executable()) {
      return 'Escolha o executável.';
    }
    if (kind === 'worker') {
      if (defaults?.Groups.some((group) => group.Name.toLowerCase() === name.toLowerCase())) {
        return `Já existe um grupo chamado ${name}.`;
      }
      if (this.instances() < 0 || this.instances() > 16) {
        return 'A quantidade de processos vai de 0 a 16.';
      }
    }
    if (kind === 'service' && defaults?.Services.some((service) => service.Name.toLowerCase() === name.toLowerCase())) {
      return `Já existe um serviço chamado ${name}.`;
    }
    if (kind === 'api') {
      if (this.instances() < 1 || this.instances() > 16) {
        return 'A quantidade de instâncias vai de 1 a 16.';
      }
      if (!/\{name\}/i.test(this.siteName()) || (this.instances() > 1 && !/\{n\}/i.test(this.siteName()))) {
        return 'O nome dos sites precisa de {name} e, com mais de uma instância, de {n}.';
      }
      if (this.port() === null || this.port()! < 1 || this.port()! + this.instances() - 1 > 65535) {
        return 'Informe a primeira porta.';
      }
      for (const site of this.sites()) {
        const taken = defaults?.Sites.find((other) => other.Port === site.port);
        if (taken) {
          return `A porta ${site.port} já é do site ${taken.Name}.`;
        }
        if (defaults?.Listening.includes(site.port!)) {
          return `Algo nesta máquina já escuta na porta ${site.port}.`;
        }
        if (defaults?.Sites.some((other) => other.Name.toLowerCase() === site.name.toLowerCase())) {
          return `Já existe um site chamado ${site.name}.`;
        }
      }
    }
    return '';
  });

  /** A setting that is JSON and does not read as one. */
  protected readonly badSetting = computed(() => {
    for (const setting of this.settings()) {
      if (setting.path.toLowerCase().endsWith('.json')) {
        try {
          JSON.parse(setting.content);
        } catch (error) {
          return `${setting.path} não é um JSON válido: ${(error as Error).message}`;
        }
      }
    }
    return '';
  });

  /** What will be made on the machine, in the words of the review. */
  protected readonly plan = computed(() => {
    const name = this.name().trim();
    const files = this.inspected()?.files.length ?? 0;
    const edited = this.settings().filter((setting) => setting.content !== setting.original).length;
    const configured = this.settings().length ? `${this.settings().length} arquivo(s) de configuração${edited ? `, ${edited} alterado(s) aqui` : ', como vieram no zip'}` : 'nenhum arquivo de configuração no zip';
    switch (this.kind()) {
      case 'worker':
        return [
          `Pasta ${this.folder()} com ${files} arquivos (${configured}).`,
          `Grupo "${name}" no Worker Control, ligado, com ${this.instances()} processo(s) de ${this.executable()}.`,
          'Os demais parâmetros do grupo entram com o padrão e são ajustados depois, na tela do grupo.',
        ];
      case 'service':
        return [
          `Pasta ${this.folder()} com ${files} arquivos (${configured}).`,
          `Serviço do Windows "${name}" (${this.displayName().trim() || name}), início ${this.startType() === 'Manual' ? 'manual' : 'automático'}, conta do sistema, rodando ${this.executable()}.`,
          'O serviço entra na lista dos que o Worker Control acompanha e é iniciado.',
        ];
      default:
        return [
          ...this.sites().map((site) => `Pasta ${site.folder} com ${files} arquivos, site "${site.name}" na porta ${site.port} e um application pool com o mesmo nome.`),
          `Configuração: ${configured}, igual em todas as instâncias.`,
          'Os application pools são criados sem código gerenciado e com a identidade padrão (ApplicationPoolIdentity).',
        ];
    }
  });

  /** For an application of the web server: what the reverse proxy needs to send requests to it. */
  protected readonly upstream = computed(() => {
    const name = this.name().trim().toLowerCase();
    return `upstream api_${name} {\n${this.sites().map((site) => `    server 127.0.0.1:${site.port};`).join('\n')}\n}`;
  });

  ngOnInit(): void {
    this.api.applicationDefaults().subscribe({
      next: (defaults) => {
        this.defaults.set(defaults);
        this.siteName.set(defaults.SiteName || '{name} {n}');
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.17).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }

  protected choose(kind: Kind): void {
    this.kind.set(kind);
    this.instances.set(kind === 'service' ? 1 : 2);
  }

  protected typeFolder(text: string): void {
    this.folderTyped.set(text);
  }

  protected async pick(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }
    this.busy.set(true);
    this.uploadProblem.set('');
    try {
      const inspected = await firstValueFrom(this.api.inspectApplication(file));
      this.inspected.set(inspected);
      this.fileName.set(file.name);
      this.executable.set(inspected.executables[0] ?? '');
      this.settings.set(inspected.settings.map((setting) => ({ ...setting, original: setting.content })));
      this.openSetting.set(0);
      if (!this.name()) {
        this.name.set(file.name.replace(/\.zip$/i, '').replace(/[^A-Za-z0-9._-]/g, ''));
      }
    } catch (failure) {
      this.uploadProblem.set((failure as HttpErrorResponse).error?.error ?? 'Não foi possível receber o arquivo.');
    } finally {
      this.busy.set(false);
    }
  }

  protected edit(index: number, content: string): void {
    this.settings.update((all) => all.map((setting, at) => (at === index ? { ...setting, content } : setting)));
  }

  /** Whether the step being shown lets the next one be gone to. */
  protected canGoOn(): boolean {
    switch (this.step()) {
      case 1:
        return !!this.inspected() && (this.kind() === 'api' || this.inspected()!.executables.length > 0);
      case 2:
        return !this.wrong();
      case 3:
        return !this.badSetting();
      default:
        return true;
    }
  }

  protected async create(): Promise<void> {
    const inspected = this.inspected();
    if (!inspected || this.wrong() || this.badSetting()) {
      return;
    }
    this.busy.set(true);
    this.failure.set('');
    try {
      const kind = this.kind();
      const result = await firstValueFrom(
        this.api.createApplication({
          token: inspected.token,
          kind,
          name: this.name().trim(),
          folder: this.folder().trim(),
          executable: kind === 'api' ? null : this.executable(),
          instances: kind === 'service' ? 1 : this.instances(),
          port: kind === 'api' ? (this.port() ?? 0) : 0,
          siteName: kind === 'api' ? this.siteName() : null,
          displayName: kind === 'service' ? this.displayName().trim() || null : null,
          startType: kind === 'service' ? this.startType() : null,
          settings: this.settings().map(({ path, content }) => ({ path, content })),
        }),
      );
      this.result.set(result);
    } catch (failure) {
      this.failure.set((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido.');
    } finally {
      this.busy.set(false);
    }
  }

  /** Back to the parameters, with everything that was typed, to try again. */
  protected again(): void {
    this.result.set(null);
    this.step.set(2);
    this.api.applicationDefaults().subscribe({ next: (defaults) => this.defaults.set(defaults), error: () => undefined });
  }
}
