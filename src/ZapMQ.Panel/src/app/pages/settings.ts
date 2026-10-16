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
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, firstValueFrom, forkJoin, of } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { Transport } from '../core/transport';
import { ApplicationDefaults, ProxyState, WorkerConfig, ZapMQSettings } from '../core/models';
import { copyText } from '../core/clipboard';
import { WhenPipe } from '../core/format';

type Kind = 'text' | 'number' | 'bool' | 'list' | 'secret';

interface Field {
  /** Where the value is, from the section: dots go into objects. */
  key: string;
  label: string;
  kind: Kind;
  hint?: string;
  /** What is used while nothing is written. */
  fallback?: string;
  /** Only an example of what goes there: nothing is used in its place. */
  example?: string;
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
      { key: 'Name', label: 'Nome do ambiente no painel', kind: 'text', example: 'DEV' },
      { key: 'Server', label: 'Servidor (instância)', kind: 'text', example: 'servidor\\instância' },
      { key: 'Databases', label: 'Bancos acompanhados', kind: 'list', hint: 'Um por linha. Só esses aparecem e recebem scripts.' },
      { key: 'User', label: 'Usuário', kind: 'text', hint: 'Vazio usa a conta do serviço (autenticação do Windows).' },
      { key: 'Password', label: 'Senha', kind: 'secret' },
      { key: 'Encrypt', label: 'Conexão cifrada', kind: 'bool', fallback: 'false' },
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
      { key: 'AccessLog', label: 'Log de acesso', kind: 'text', wide: true, example: 'C:\\nginx\\logs\\access.zapmq.log' },
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
      { key: 'Config', label: 'Arquivo de configuração', kind: 'text', wide: true, example: 'C:\\nginx\\conf\\nginx.conf' },
      { key: 'Executable', label: 'Executável', kind: 'text', wide: true, example: 'C:\\nginx\\nginx.exe' },
      { key: 'Service', label: 'Serviço do Windows', kind: 'text', example: 'nginx' },
    ],
  },
];

const OWN_LABELS: Record<string, string> = {
  'Transport.Environment': 'Nome do ambiente',
  'Transport.Directory': 'Pasta do transporte',
  Port: 'Porta da mensageria',
  RetentionSeconds: 'Mensagens não entregues vivem (s)',
  EmptyQueueLifetimeSeconds: 'Fila vazia sem ninguém some após (s)',
  MaxRequestLineBytes: 'Maior mensagem do protocolo 1.x (bytes)',
  'V2.Enabled': 'Protocolo 2.x ligado',
  'V2.MaxFrameBytes': 'Maior mensagem do protocolo 2.x (bytes)',
  'V2.PingSeconds': 'Sinal de vida a cada (s)',
  'V2.PingTimeoutSeconds': 'Conexão sem sinal cai após (s)',
  'DeadLetters.MaxMessagesPerQueue': 'Mensagens mortas guardadas por fila',
  'DeadLetters.MaxAgeHours': 'Mensagens mortas guardadas por (horas)',
  'Panel.Port': 'Porta do painel',
  'Panel.BasePath': 'Caminho do painel no proxy',
  'Panel.SessionHours': 'Sessão do painel dura (horas)',
  LogDirectory: 'Pasta do log',
  LogRetentionDays: 'Log guardado por (dias)',
  LogLevel: 'Nível do log',
};

interface Download {
  Name: string;
  Root: string;
  Files?: string;
  VersionsFile?: string;
}

interface Frontend {
  Name: string;
  Root: string;
  Mode?: string;
  Address?: string;
  Active?: boolean;
  Serve?: string;
  ProxyTo?: string;
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
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatTooltipModule, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class SettingsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);
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

  /** Where the file of the environment is, on the machine of the Worker Control. */
  protected readonly location = signal('');
  /** How many parameters were in their default and were filled with it, to be written. */
  protected readonly filled = signal(0);

  /** The settings of the ZapMQ itself, and the values being edited, by key. */
  protected readonly own = signal<ZapMQSettings | null>(null);
  protected ownValues: Record<string, unknown> = {};
  protected readonly ownDirty = signal(false);
  protected readonly ownSaving = signal(false);
  protected readonly ownProblem = signal('');

  /** The rules of the environment being edited: they hold as soon as they are saved. */
  protected rules = { manualVersions: false, separateApprover: false, windowStart: '', windowEnd: '' };
  protected readonly rulesDirty = signal(false);
  protected readonly rulesSaving = signal(false);
  protected readonly rulesProblem = signal('');

  private loadRules(): void {
    this.api.policy().subscribe({
      next: (policy) => {
        this.transport.policy.set(policy);
        this.rules = { manualVersions: policy.manualVersions, separateApprover: policy.separateApprover, windowStart: policy.windowStart ?? '', windowEnd: policy.windowEnd ?? '' };
        this.rulesDirty.set(false);
      },
      error: () => undefined,
    });
  }

  protected rulesChanged(): void {
    this.rulesDirty.set(true);
  }

  protected async saveRules(): Promise<void> {
    this.rulesSaving.set(true);
    this.rulesProblem.set('');
    try {
      const saved = await firstValueFrom(this.api.setPolicy({ ...this.rules, windowStart: this.rules.windowStart || null, windowEnd: this.rules.windowEnd || null }));
      this.transport.policy.set(saved);
      this.rulesDirty.set(false);
      this.snack.open('Regras gravadas: já valem.', undefined, { duration: 4000 });
    } catch (failure) {
      this.rulesProblem.set((failure as HttpErrorResponse).error?.error ?? 'As regras não foram gravadas.');
    } finally {
      this.rulesSaving.set(false);
    }
  }

  protected readonly ownGroups = computed(() => {
    const all = this.own()?.settings ?? [];
    const group = (prefix: string[]) => all.filter((setting) => prefix.some((start) => setting.key === start || setting.key.startsWith(start + '.')));
    return [
      { title: 'Ambiente e transporte', items: group(['Transport']) },
      { title: 'Mensageria', items: group(['Port', 'RetentionSeconds', 'EmptyQueueLifetimeSeconds', 'MaxRequestLineBytes', 'V2', 'DeadLetters']) },
      { title: 'Painel', items: group(['Panel']) },
      { title: 'Log', items: group(['LogDirectory', 'LogRetentionDays', 'LogLevel']) },
    ];
  });

  protected ownLabel(key: string): string {
    return OWN_LABELS[key] ?? key;
  }

  /** Something written for the next start that is not what the service runs with now. */
  protected pending(setting: ZapMQSettings['settings'][number]): boolean {
    return setting.saved !== null && setting.saved !== setting.current;
  }

  private loadOwn(): void {
    this.api.zapmqSettings().subscribe({
      next: (settings) => {
        this.own.set(settings);
        this.ownValues = Object.fromEntries(settings.settings.map((setting) => [setting.key, setting.saved ?? setting.current]));
        this.ownDirty.set(false);
        this.ownProblem.set('');
      },
      error: () => undefined,
    });
  }

  protected ownChanged(key: string, value: unknown): void {
    this.ownValues[key] = value;
    this.ownDirty.set(true);
  }

  protected async saveOwn(): Promise<void> {
    const settings = this.own();
    if (!settings) {
      return;
    }
    this.ownSaving.set(true);
    this.ownProblem.set('');
    // Only what differs from what the service runs with goes to the file: the rest stays with the file of the program.
    const values = Object.fromEntries(
      settings.settings
        .map((setting) => [setting.key, setting.kind === 'number' ? Number(this.ownValues[setting.key]) : this.ownValues[setting.key]] as const)
        .filter(([key, value]) => value !== settings.settings.find((setting) => setting.key === key)!.current || settings.settings.find((setting) => setting.key === key)!.saved !== null),
    );
    try {
      this.own.set(await firstValueFrom(this.api.saveZapmqSettings(values)));
      this.ownDirty.set(false);
      this.snack.open('Gravado. Passa a valer quando o serviço do ZapMQ for reiniciado.', undefined, { duration: 5000 });
    } catch (failure) {
      this.ownProblem.set((failure as HttpErrorResponse).error?.error ?? 'O ZapMQ não aceitou as configurações.');
    } finally {
      this.ownSaving.set(false);
    }
  }

  protected readonly section = computed(() => SECTIONS.find((section) => section.id === this.current()) ?? null);

  protected readonly frontends = computed(() => (this.config()?.['Frontends'] as Frontend[] | undefined) ?? []);

  /** The kind of each web application already saved, by name: it never changes. */
  protected readonly savedKinds = signal(new Map<string, string>());

  protected kindLocked(app: Frontend): boolean {
    return this.savedKinds().has(app.Name.trim().toLowerCase());
  }

  ngOnInit(): void {
    // ?assunto= opens one subject, as the pages that depend on it link here.
    const asked = this.route.snapshot.queryParamMap.get('assunto');
    if (asked && ['ambiente', 'regras', 'web', 'downloads', ...SECTIONS.map((section) => section.id)].includes(asked))
      this.current.set(asked);
    this.load();
    this.loadOwn();
    this.loadRules();
  }

  protected load(): void {
    forkJoin({
      environment: this.api.environment(),
      // What is worked out from the machine stands for what is not written: the folders, the names of the sites, the proxy.
      defaults: this.api.applicationDefaults().pipe(catchError(() => of(null))),
      proxy: this.api.proxy().pipe(catchError(() => of(null))),
    }).subscribe({
      next: ({ environment, defaults, proxy }) => {
        const config = structuredClone(environment.Config) as WorkerConfig;
        this.config.set(config);
        this.savedKinds.set(new Map(((config['Frontends'] as Frontend[] | undefined) ?? []).map((app) => [app.Name.trim().toLowerCase(), app.Mode || 'mfe'])));
        this.location.set(environment.File);
        this.typed.clear();
        this.problem.set('');
        this.refusal.set('');
        const filled = this.fill(defaults, proxy);
        this.filled.set(filled);
        this.dirty.set(filled > 0);
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.21).' : (failure.error?.error ?? 'Não foi possível ler a configuração do Worker Control.')),
    });
  }

  /**
   * Writes in the copy being edited what is in use without being written: the defaults, and
   * what was worked out from the machine. Saved, they stay in the file as they are now.
   */
  private fill(defaults: ApplicationDefaults | null, proxy: ProxyState | null): number {
    let count = 0;
    const put = (section: Section, key: string, value: unknown) => {
      if (value === null || value === undefined || value === '') {
        return;
      }
      const field = section.fields.find((candidate) => candidate.key === key)!;
      const current = this.read(section, key);
      if (current === undefined || current === null || current === '') {
        this.set(section, field, field.kind === 'list' ? String(value) : value, false);
        count++;
      }
    };
    for (const section of SECTIONS) {
      if (!this.present(section)) {
        continue;
      }
      for (const field of section.fields) {
        if (field.fallback !== undefined) {
          put(section, field.key, field.kind === 'bool' ? field.fallback === 'true' : field.kind === 'number' ? Number(field.fallback) : field.fallback);
        }
      }
      if (section.id === 'aplicacoes' && defaults) {
        put(section, 'CreateFolders.worker', defaults.Folders['worker']);
        put(section, 'CreateFolders.api', defaults.Folders['api']);
        put(section, 'CreateFolders.service', defaults.Folders['service']);
        put(section, 'SiteName', defaults.SiteName);
      }
      if (section.id === 'proxy' && proxy?.Configured) {
        put(section, 'Config', proxy.Config);
        put(section, 'Executable', proxy.Executable);
        put(section, 'Service', proxy.Service?.Name);
      }
    }
    return count;
  }

  /** What is written for a field, as it is in the file; undefined when nothing is. */
  private read(section: Section, key: string): unknown {
    let at: unknown = this.holder(section, false);
    for (const part of key.split('.')) {
      at = at && typeof at === 'object' ? (at as Record<string, unknown>)[part] : undefined;
    }
    return at;
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

  protected set(section: Section, field: Field, raw: unknown, touched = true): void {
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
    if (touched) {
      this.touch();
    }
  }

  protected readonly downloads = computed(() => (this.config()?.['Downloads'] as Download[] | undefined) ?? []);

  /** What the reverse proxy needs to serve each folder, once it was made ready. */
  protected readonly prepared = signal<Record<number, { created: boolean; snippet: string }>>({});

  protected async prepare(index: number): Promise<void> {
    const area = this.downloads()[index];
    try {
      const answer = await firstValueFrom(this.api.prepareDownloads({ name: area.Name, root: area.Root, files: area.Files, versionsFile: area.VersionsFile }));
      this.prepared.update((all) => ({ ...all, [index]: { created: answer.Created, snippet: answer.Snippet } }));
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não preparou a pasta.', 'Fechar', { duration: 9000 });
    }
  }

  protected copy(text: string): void {
    void copyText(text).then((done) => this.snack.open(done ? 'Trecho copiado' : 'Não foi possível copiar: selecione o trecho e use Ctrl+C', undefined, { duration: done ? 2000 : 5000 }));
  }

  protected addDownload(): void {
    (this.config() as Record<string, unknown>)['Downloads'] = [...this.downloads(), { Name: '', Root: '' }];
    this.touch();
  }

  protected removeDownload(index: number): void {
    const left = this.downloads().filter((_, at) => at !== index);
    const config = this.config() as Record<string, unknown>;
    if (left.length) {
      config['Downloads'] = left;
    } else {
      delete config['Downloads'];
    }
    this.touch();
  }

  protected setDownload(index: number, key: keyof Download, raw: string): void {
    (this.config() as Record<string, unknown>)['Downloads'] = this.downloads().map((item, at) => {
      if (at !== index) {
        return item;
      }
      const changed = { ...item } as Record<string, unknown>;
      if (raw.trim() || key === 'Name' || key === 'Root') {
        changed[key] = raw.trim();
      } else {
        delete changed[key];
      }
      return changed;
    });
    this.touch();
  }

  protected addFrontend(): void {
    const config = this.config() as Record<string, unknown>;
    // A new application does not answer by its name until it is activated.
    config['Frontends'] = [...this.frontends(), { Name: '', Root: '', Mode: 'mfe', Active: false }];
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
      await firstValueFrom(this.api.saveEnvironment(config as Record<string, unknown>));
      this.snack.open('Configurações gravadas. O Worker Control já está usando.', undefined, { duration: 3500 });
      this.load();
    } catch (failure) {
      this.refusal.set((failure as HttpErrorResponse).error?.error ?? 'O Worker Control não aceitou a configuração.');
    } finally {
      this.saving.set(false);
    }
  }
}
