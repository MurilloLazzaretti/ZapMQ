import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { Transport } from '../core/transport';
import { WorkerConfig } from '../core/models';

type Kind = 'text' | 'number' | 'bool' | 'list' | 'secret';

interface Field {
  /** Where the value is, from the section: dots go into objects. */
  key: string;
  label: string;
  kind: Kind;
  hint?: string;
  /** What is used while nothing is written. */
  fallback?: string;
  wide?: boolean;
}

interface Section {
  id: string;
  title: string;
  icon: string;
  about: string;
  /** The object of the file the section is about; empty for the root. */
  path: string | null;
  /** A section that may not be there at all: switched off, it is taken out of the file. */
  optional?: string;
  fields: Field[];
}

const SECTIONS: Section[] = [
  {
    id: 'banco', title: 'Banco de dados', icon: 'database', path: 'Database', optional: 'Acompanhar um banco de dados',
    about: 'A instância que o Worker Control acompanha e em que aplica os scripts do transporte. A senha é guardada cifrada para esta máquina.',
    fields: [
      { key: 'Name', label: 'Nome do ambiente no painel', kind: 'text', fallback: 'DEV' },
      { key: 'Server', label: 'Servidor (instância)', kind: 'text', fallback: 'servidor\\instância' },
      { key: 'Databases', label: 'Bancos acompanhados', kind: 'list', hint: 'Um por linha. Só esses aparecem e recebem scripts.' },
      { key: 'User', label: 'Usuário', kind: 'text', hint: 'Vazio usa a conta do serviço (autenticação do Windows).' },
      { key: 'Password', label: 'Senha', kind: 'secret' },
      { key: 'Encrypt', label: 'Conexão cifrada', kind: 'bool' },
      { key: 'TrustServerCertificate', label: 'Confiar no certificado do servidor', kind: 'bool', fallback: 'true' },
      { key: 'SampleSeconds', label: 'Leitura a cada (s)', kind: 'number', fallback: '30' },
      { key: 'RetentionDays', label: 'Histórico de saúde (dias)', kind: 'number', fallback: '7' },
      { key: 'BlockingSeconds', label: 'Alerta de bloqueio após (s)', kind: 'number', fallback: '30' },
      { key: 'BackupHours', label: 'Alerta sem backup após (h)', kind: 'number', hint: '0 não alerta.', fallback: '0' },
      { key: 'DiskFreePercent', label: 'Alerta de disco abaixo de (%)', kind: 'number', fallback: '10' },
      { key: 'ObjectScanMinutes', label: 'Procurar mudanças nos objetos a cada (min)', kind: 'number', fallback: '10' },
      { key: 'ObjectHistoryDays', label: 'Histórico de mudanças nos objetos (dias)', kind: 'number', fallback: '365' },
    ],
  },
  {
    id: 'aplicacoes', title: 'Aplicações', icon: 'deployed_code', path: 'Transport',
    about: 'Onde as aplicações novas são criadas, como os sites são nomeados, e como as trocas de versão se comportam.',
    fields: [
      { key: 'CreateFolders.worker', label: 'Pasta dos micro serviços', kind: 'text', hint: 'Vazio: a pasta em que está a maioria dos que existem.', wide: true },
      { key: 'CreateFolders.api', label: 'Pasta das APIs', kind: 'text', hint: 'Vazio: a pasta em que está a maioria das que existem.', wide: true },
      { key: 'CreateFolders.service', label: 'Pasta dos serviços do Windows', kind: 'text', hint: 'Vazio: a pasta em que está a maioria dos que existem.', wide: true },
      { key: 'SiteName', label: 'Nome dos sites de uma API nova', kind: 'text', hint: '{name} é o nome da API, {n} o número da instância. Vazio: o padrão dos sites que existem.' },
      { key: 'AllowCreate', label: 'Permitir criar e excluir aplicações pelo painel', kind: 'bool', fallback: 'true' },
      { key: 'StopSeconds', label: 'Espera para parar antes de trocar (s)', kind: 'number', fallback: '90' },
      { key: 'KeepVersions', label: 'Cópias guardadas de cada aplicação', kind: 'number', fallback: '3', hint: 'As versões substituídas, para reverter.' },
      { key: 'Keep', label: 'Arquivos do ambiente', kind: 'list', hint: 'Nunca levados num pacote nem substituídos por um. Um por linha; pasta termina com /. Vazio: o padrão.', fallback: 'appsettings*.json\nweb.config\nConfigWorkers.json\n*.ini\nenvironment.js\nenv.json\n*.db\n*.db-wal\n*.db-shm\nlogs/' },
      { key: 'Directory', label: 'Pasta de trabalho do transporte', kind: 'text', fallback: 'transport', hint: 'Cópias guardadas e entradas. Relativa à pasta de dados do Worker Control.' },
    ],
  },
  {
    id: 'trafego', title: 'Tráfego HTTP', icon: 'monitoring', path: 'Traffic', optional: 'Ler o tráfego do proxy reverso',
    about: 'O log de acesso do proxy reverso, lido para medir o tráfego por aplicação, rota e tela.',
    fields: [
      { key: 'AccessLog', label: 'Log de acesso', kind: 'text', wide: true, fallback: 'C:\\nginx\\logs\\access.zapmq.log' },
      { key: 'ReopenCommand', label: 'Comando para o proxy reabrir o log', kind: 'text', wide: true, hint: 'Usado ao girar o arquivo. Vazio: o próprio Worker Control renomeia.' },
      { key: 'RotateAtMb', label: 'Girar o log ao chegar a (MB)', kind: 'number', fallback: '100' },
      { key: 'KeepFiles', label: 'Logs girados guardados', kind: 'number', fallback: '5' },
      { key: 'RetentionDays', label: 'Histórico (dias)', kind: 'number', fallback: '30' },
      { key: 'MaxRoutes', label: 'Rotas acompanhadas, no máximo', kind: 'number', fallback: '2000' },
      { key: 'KeepErrors', label: 'Erros guardados', kind: 'number', fallback: '500' },
      { key: 'GroupBy', label: 'Prefixos que agrupam aplicações', kind: 'list', fallback: 'api\nmfe' },
      { key: 'Routes', label: 'Rotas com parâmetros', kind: 'list', hint: 'Uma por linha, com {nome} no lugar do que varia.' },
      { key: 'Ignore', label: 'Caminhos ignorados', kind: 'list' },
    ],
  },
  {
    id: 'proxy', title: 'Proxy reverso', icon: 'dns', path: 'Proxy',
    about: 'Onde está o NGINX. Vazio: ele é procurado ao lado do log de acesso.',
    fields: [
      { key: 'Config', label: 'Arquivo de configuração', kind: 'text', wide: true, fallback: 'C:\\nginx\\conf\\nginx.conf' },
      { key: 'Executable', label: 'Executável', kind: 'text', wide: true, fallback: 'C:\\nginx\\nginx.exe' },
      { key: 'Service', label: 'Serviço do Windows', kind: 'text', fallback: 'nginx' },
    ],
  },
  {
    id: 'worker-control', title: 'Worker Control', icon: 'precision_manufacturing', path: null,
    about: 'Como o Worker Control chega ao ZapMQ e por quanto tempo guarda o que aconteceu. Os padrões dos grupos ficam em Micro serviços → Configurar.',
    fields: [
      { key: 'ZapMQHost', label: 'Máquina do ZapMQ', kind: 'text', fallback: 'localhost' },
      { key: 'ZapMQPort', label: 'Porta do ZapMQ', kind: 'number', fallback: '5679' },
      { key: 'EventRetentionDays', label: 'Histórico de eventos (dias)', kind: 'number', fallback: '30' },
      { key: 'HealthRetentionHours', label: 'Medições de saúde (horas)', kind: 'number', fallback: '24' },
      { key: 'Services.SuggestFrom', label: 'Pastas onde procurar serviços para acompanhar', kind: 'list' },
    ],
  },
];

interface Frontend {
  Name: string;
  Root: string;
  Mode?: string;
  BaseUrl?: string;
  Host?: string;
  Manifest?: string;
  VersionFile?: string;
}

/**
 * The settings of the environment in one place, by subject: the database, where applications
 * are created, the web applications, the traffic and the proxy. They are kept in the
 * configuration of the Worker Control, which checks them before taking them.
 */
@Component({
  selector: 'zap-settings',
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class SettingsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);
  protected readonly auth = inject(Auth);
  protected readonly transport = inject(Transport);

  protected readonly sections = SECTIONS;
  protected readonly config = signal<WorkerConfig | null>(null);
  protected readonly current = signal('ambiente');
  protected readonly dirty = signal(false);
  protected readonly saving = signal(false);
  protected readonly problem = signal('');
  protected readonly refusal = signal('');
  /** Passwords typed now, which the file only gets when saved. */
  private readonly typed = new Map<string, string>();

  protected readonly section = computed(() => SECTIONS.find((section) => section.id === this.current()) ?? null);

  protected readonly frontends = computed(() => (this.config()?.['Frontends'] as Frontend[] | undefined) ?? []);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api.workerConfig().subscribe({
      next: (answer) => {
        this.config.set(structuredClone(answer.Config));
        this.typed.clear();
        this.dirty.set(false);
        this.problem.set('');
        this.refusal.set('');
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível ler a configuração do Worker Control.'),
    });
  }

  private holder(section: Section, create: boolean): Record<string, unknown> | null {
    const config = this.config() as Record<string, unknown> | null;
    if (!config) {
      return null;
    }
    if (!section.path) {
      return config;
    }
    if (!config[section.path] && create) {
      config[section.path] = {};
    }
    return (config[section.path] as Record<string, unknown> | undefined) ?? null;
  }

  protected present(section: Section): boolean {
    return !section.optional || !!this.holder(section, false);
  }

  protected toggle(section: Section, on: boolean): void {
    const config = this.config() as Record<string, unknown>;
    if (on) {
      config[section.path!] = {};
    } else {
      delete config[section.path!];
    }
    this.touch();
  }

  protected value(section: Section, field: Field): string | number | boolean | null {
    let at: unknown = this.holder(section, false);
    for (const part of field.key.split('.')) {
      at = at && typeof at === 'object' ? (at as Record<string, unknown>)[part] : undefined;
    }
    if (field.kind === 'secret') {
      return this.typed.get(section.id + field.key) ?? '';
    }
    if (field.kind === 'list') {
      return Array.isArray(at) ? at.join('\n') : '';
    }
    if (field.kind === 'bool') {
      return typeof at === 'boolean' ? at : field.fallback === 'true';
    }
    return at === undefined || at === null ? '' : (at as string | number);
  }

  /** Something kept in the field already, which is not shown: a password. */
  protected kept(section: Section, field: Field): boolean {
    const holder = this.holder(section, false);
    return !!holder && typeof holder[field.key] === 'string' && (holder[field.key] as string).length > 0;
  }

  protected set(section: Section, field: Field, raw: unknown): void {
    const parts = field.key.split('.');
    let at = this.holder(section, true)!;
    for (const part of parts.slice(0, -1)) {
      if (!at[part] || typeof at[part] !== 'object') {
        at[part] = {};
      }
      at = at[part] as Record<string, unknown>;
    }
    const last = parts[parts.length - 1];
    if (field.kind === 'secret') {
      this.typed.set(section.id + field.key, String(raw ?? ''));
      if (raw) {
        at[last] = String(raw);
      }
    } else if (field.kind === 'bool') {
      at[last] = !!raw;
    } else if (field.kind === 'list') {
      const items = String(raw ?? '').split('\n').map((line) => line.trim()).filter(Boolean);
      if (items.length) {
        at[last] = items;
      } else {
        delete at[last];
      }
    } else if (raw === '' || raw === null || raw === undefined) {
      // Nothing written: the default.
      delete at[last];
    } else {
      at[last] = field.kind === 'number' ? Number(raw) : String(raw);
    }
    // What is left empty does not stay in the file as an empty object.
    if (parts.length > 1 && at !== this.holder(section, false) && !Object.keys(at).length) {
      delete this.holder(section, false)![parts[0]];
    }
    this.touch();
  }

  protected addFrontend(): void {
    const config = this.config() as Record<string, unknown>;
    config['Frontends'] = [...this.frontends(), { Name: '', Root: '', Mode: 'mfe' }];
    this.touch();
  }

  protected removeFrontend(index: number): void {
    const config = this.config() as Record<string, unknown>;
    config['Frontends'] = this.frontends().filter((_, at) => at !== index);
    this.touch();
  }

  protected setFrontend(index: number, key: keyof Frontend, raw: string): void {
    const config = this.config() as Record<string, unknown>;
    // A new list of new items, so that whatever shows them sees the change.
    config['Frontends'] = this.frontends().map((app, at) => {
      if (at !== index) {
        return app;
      }
      const changed = { ...app } as Record<string, unknown>;
      if (raw.trim()) {
        changed[key] = raw.trim();
      } else if (key !== 'Name' && key !== 'Root') {
        delete changed[key];
      } else {
        changed[key] = '';
      }
      return changed;
    });
    this.touch();
  }

  private touch(): void {
    this.dirty.set(true);
    this.config.update((config) => (config ? { ...config } : config));
  }

  protected discard(): void {
    this.load();
  }

  protected async save(): Promise<void> {
    const config = this.config();
    if (!config) {
      return;
    }
    this.saving.set(true);
    this.refusal.set('');
    try {
      await firstValueFrom(this.api.saveWorkerConfig(config));
      this.snack.open('Configuração gravada. O Worker Control já está usando.', undefined, { duration: 3500 });
      this.load();
    } catch (failure) {
      this.refusal.set((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou a configuração.');
    } finally {
      this.saving.set(false);
    }
  }
}
