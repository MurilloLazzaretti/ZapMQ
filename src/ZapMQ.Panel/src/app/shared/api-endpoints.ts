import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { kindIcon, kindName } from '../core/database';
import { AgoPipe, WhenPipe } from '../core/format';
import { ApiDocs } from '../core/models';
import { Endpoint, readEndpoints } from '../core/openapi';

/**
 * The endpoints of an API, as it says them: the OpenAPI document it serves and the map of what
 * each one runs in the database. Either may be missing; without both, it says the feature is not
 * there for this API and how to have it.
 */
@Component({
  selector: 'zap-api-endpoints',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <div class="panel-head">
        <h2>Endpoints</h2>
        @if (docs(); as now) {
          <span class="hint">{{ endpoints().length }} {{ endpoints().length === 1 ? 'endpoint' : 'endpoints' }}@if (mappedCount()) { · {{ mappedCount() }} com o que executam no banco } · lido {{ now.At | ago }}</span>
        }
        <span class="spacer"></span>
        <button mat-button (click)="load(true)" [disabled]="loading()"><mat-icon svgIcon="refresh" /> Atualizar</button>
      </div>

      @if (problem()) {
        <div class="empty"><mat-icon svgIcon="cloud_off" /><span>{{ problem() }}</span></div>
      } @else if (docs(); as now) {
        @if (!now.OpenApi && !now.Map) {
          <div class="unavailable">
            <mat-icon svgIcon="info" />
            <div>
              <strong>Recurso não disponível para esta API</strong>
              <span>Ela não publica um documento OpenAPI@if (now.OpenApiProblem && now.OpenApiProblem !== 'absent') { ({{ openApiProblem(now) }}) } e não traz o mapa de endpoints (<span class="mono">zapmq.endpoints.json</span>) na pasta.</span>
              <span class="muted">Para habilitar: a API publica o OpenAPI (em .NET, com Swashbuckle ou <span class="mono">MapOpenApi</span>), e o build dela gera o mapa. Se o OpenAPI estiver em outro caminho, informe abaixo. O formato está em <span class="mono">docs/ENDPOINTS.md</span>, no ZapMQ.</span>
            </div>
          </div>
        } @else {
          <ul class="sources">
            <li>
              <mat-icon [svgIcon]="now.OpenApi ? 'check_circle' : 'info'" [class.ok]="now.OpenApi" />
              @if (now.OpenApi) {
                <span><strong>OpenAPI</strong> {{ now.OpenApi['info']?.title || '' }} {{ now.OpenApi['info']?.version ? 'v' + now.OpenApi['info']?.version : '' }} <span class="muted">· {{ now.OpenApiUrl }}</span></span>
              } @else {
                <span><strong>Sem OpenAPI:</strong> <span class="muted">{{ openApiProblem(now) }}. Os endpoints vêm só do mapa, sem parâmetros nem respostas.</span></span>
              }
            </li>
            <li>
              <mat-icon [svgIcon]="now.Map ? (now.MapStale ? 'warning' : 'check_circle') : 'info'" [class.ok]="now.Map && !now.MapStale" [class.warn]="now.MapStale" />
              @if (now.Map) {
                <span><strong>Mapa de endpoints</strong> <span class="muted">· gerado {{ now.MapBuiltAt ? (now.MapBuiltAt | when) : '' }}@if (now.Map.generator) { por {{ now.Map.generator }} }</span>
                  @if (now.MapStale) { <span class="warn-text">· há binários na pasta mais novos que o mapa ({{ now.NewestBinaryAt | when }}): ele pode não corresponder ao que está instalado.</span> }
                </span>
              } @else {
                <span><strong>Sem mapa de endpoints:</strong> <span class="muted">{{ now.MapProblem === 'absent' || !now.MapProblem ? 'a pasta não tem o zapmq.endpoints.json, então não se sabe o que cada endpoint executa no banco' : now.MapProblem }}.</span></span>
              }
            </li>
          </ul>

          <div class="tools">
            <label class="search">
              <mat-icon svgIcon="search" />
              <input type="search" placeholder="Rota, resumo, procedure, tabela, trecho do SQL…" [ngModel]="search()" (ngModelChange)="search.set($event)" aria-label="Buscar endpoint" />
            </label>
            <select class="pick" [ngModel]="method()" (ngModelChange)="method.set($event)" aria-label="Verbo">
              <option value="">Todos os verbos</option>
              @for (item of methods(); track item) { <option [value]="item">{{ item }}</option> }
            </select>
            @if (now.Map) {
              <label class="only"><input type="checkbox" [ngModel]="withSql()" (ngModelChange)="withSql.set($event)" /> Só os que usam o banco</label>
            }
          </div>

          @for (group of groups(); track group.name) {
            <div class="group">
              <div class="group-head"><strong>{{ group.name }}</strong> <span class="muted">{{ group.items.length }}</span></div>
              @for (item of group.items; track item.key) {
                <div class="endpoint" [class.open]="open() === item.key">
                  <button type="button" class="row" (click)="open.set(open() === item.key ? null : item.key)" [attr.aria-expanded]="open() === item.key">
                    <span class="verb" [class]="'verb ' + item.method.toLowerCase()">{{ item.method }}</span>
                    <span class="route mono" [class.deprecated]="item.deprecated">{{ item.route }}</span>
                    <span class="summary muted">{{ item.summary }}</span>
                    <span class="marks">
                      @for (found of item.found.slice(0, 2); track found.name) { <span class="pill info">{{ short(found.name) }}</span> }
                      @if (item.found.length > 2) { <span class="pill">+{{ item.found.length - 2 }}</span> }
                      @if (!item.documented) { <span class="pill" matTooltip="Está no mapa e não no OpenAPI">sem documentação</span> }
                      @if (!item.mapped && now.Map) { <span class="pill" matTooltip="Está no OpenAPI e não no mapa: não se sabe o que executa">sem mapa</span> }
                    </span>
                    <mat-icon class="chevron" svgIcon="keyboard_arrow_down" />
                  </button>
                  @if (open() === item.key) {
                    <div class="detail">
                      @if (item.description) { <p class="description">{{ item.description }}</p> }
                      @if (item.handler) { <p class="muted handler">Atendido por <span class="mono">{{ item.handler }}</span></p> }

                      @if (item.found.length || item.commands.length) {
                        <h4>No banco de dados</h4>
                        @if (item.found.length) {
                          <div class="objects">
                            @for (found of item.found; track found.name) {
                              @if (found.kind && now.Database) {
                                <a class="object" [routerLink]="['/banco/objetos', now.Database, found.kind, schemaOf(found.name), nameOf(found.name)]" [matTooltip]="kindName($any(found.kind))"><mat-icon [svgIcon]="kindIcon($any(found.kind))" /> {{ found.name }}</a>
                              } @else {
                                <span class="object unknown" matTooltip="Não encontrado no banco acompanhado por este ambiente"><mat-icon svgIcon="info" /> {{ found.name }}</span>
                              }
                            }
                          </div>
                        }
                        @for (command of item.commands; track $index) {
                          <div class="command">
                            <div class="command-head">
                              @if (command.source) { <span class="mono muted">{{ command.source }}</span> }
                              @if (command.dynamic) { <span class="pill warn" matTooltip="O texto é montado em execução: o que varia está marcado com /*…*/">montado em execução</span> }
                            </div>
                            <pre class="sql mono">{{ command.sql }}</pre>
                            @for (note of command.notes ?? []; track $index) { <p class="note"><mat-icon svgIcon="warning" /> {{ note }}</p> }
                          </div>
                        }
                      }

                      @if (item.parameters.length) {
                        <h4>Parâmetros</h4>
                        <table class="data params">
                          <thead><tr><th>Nome</th><th>Onde</th><th>Tipo</th><th>Descrição</th></tr></thead>
                          <tbody>
                            @for (parameter of item.parameters; track parameter.in + parameter.name) {
                              <tr><td class="mono">{{ parameter.name }}@if (parameter.required) { <span class="required" matTooltip="obrigatório">*</span> }</td><td>{{ where(parameter.in) }}</td><td class="mono">{{ parameter.type }}</td><td>{{ parameter.description }}</td></tr>
                            }
                          </tbody>
                        </table>
                      }
                      @if (item.body) {
                        <h4>Corpo</h4>
                        <pre class="schema mono">{{ item.body }}</pre>
                      }
                      @if (item.responses.length) {
                        <h4>Respostas</h4>
                        @for (response of item.responses; track response.status) {
                          <div class="response"><span class="pill" [class.ok]="response.status.startsWith('2')" [class.danger]="response.status.startsWith('4') || response.status.startsWith('5')">{{ response.status }}</span> <span>{{ response.description }}</span></div>
                          @if (response.schema) { <pre class="schema mono">{{ response.schema }}</pre> }
                        }
                      }
                    </div>
                  }
                </div>
              }
            </div>
          } @empty {
            <div class="empty"><span>Nenhum endpoint com esses filtros.</span></div>
          }
        }

        <details class="where-openapi" [open]="!now.OpenApi">
          <summary>Onde está o OpenAPI</summary>
          <p class="muted">
            @if (now.OpenApiTried?.length) { Procurado em: <span class="mono">{{ tried(now) }}</span>. }
            Informe um caminho (<span class="mono">/docs/openapi.json</span>), pedido a cada site da API, ou um endereço completo. Vazio volta a procurar nos caminhos de costume.
          </p>
          <form (ngSubmit)="saveOpenApi()">
            <input type="text" name="openapi" [(ngModel)]="openApiPath" placeholder="/swagger/v1/swagger.json" spellcheck="false" aria-label="Caminho do OpenAPI" />
            <button mat-button type="submit" [disabled]="loading()">Gravar</button>
          </form>
        </details>
      } @else {
        <div class="empty"><span>Lendo o que a API diz de si…</span></div>
      }
    </section>
  `,
  styles: `
    .spacer { flex: 1; }
    .unavailable { display: flex; gap: 12px; margin: 0 20px 16px; padding: 14px 16px; border-radius: 14px; background: var(--mat-sys-surface-container-high); }
    .unavailable mat-icon { flex: none; color: var(--mat-sys-primary); }
    .unavailable div { display: grid; gap: 6px; }
    .sources { list-style: none; margin: 0; padding: 0 20px 10px; display: grid; gap: 6px; font-size: 13.5px; }
    .sources li { display: flex; align-items: flex-start; gap: 8px; }
    .sources mat-icon { flex: none; width: 18px; height: 18px; color: var(--mat-sys-on-surface-variant); }
    .sources mat-icon.ok { color: var(--zap-ok); }
    .sources mat-icon.warn, .warn-text { color: var(--zap-warn); }
    .tools { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; padding: 4px 20px 12px; }
    .search { display: flex; align-items: center; gap: 8px; flex: 1 1 260px; max-width: 480px; padding: 0 14px; height: 40px; border-radius: 999px; background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface-variant); }
    .search mat-icon { width: 20px; height: 20px; flex: none; }
    .search input { flex: 1; min-width: 0; border: none; outline: none; background: transparent; color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium); }
    .search:focus-within { outline: 2px solid var(--mat-sys-primary); }
    .pick { height: 40px; padding: 0 12px; border: 1px solid var(--zap-border); border-radius: 999px; background: var(--zap-surface); color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium); }
    .only { display: inline-flex; align-items: center; gap: 6px; font-size: 13.5px; }
    .group { padding: 0 20px 8px; }
    .group-head { padding: 10px 0 6px; font: var(--mat-sys-title-small); }
    .endpoint { border: 1px solid var(--zap-border); border-radius: 12px; margin-bottom: 6px; }
    .endpoint.open { border-color: var(--mat-sys-primary); }
    .row { display: flex; align-items: center; gap: 10px; width: 100%; padding: 8px 12px; border: none; background: none; color: inherit; font: inherit; text-align: left; cursor: pointer; }
    .row .route { overflow-wrap: anywhere; }
    .row .route.deprecated { text-decoration: line-through; }
    .row .summary { flex: 1; min-width: 0; font-size: 12.5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row .marks { display: flex; flex-wrap: wrap; gap: 4px; justify-content: flex-end; }
    .row .chevron { flex: none; transition: transform 0.15s; }
    .open .row .chevron { transform: rotate(180deg); }
    .verb { flex: none; min-width: 58px; padding: 2px 8px; border-radius: 8px; font: 600 12px/1.6 var(--zap-mono, monospace); text-align: center; background: var(--mat-sys-surface-container-highest); }
    .verb.get { background: var(--zap-info-container); color: var(--zap-info); }
    .verb.post { background: var(--zap-ok-container); color: var(--zap-ok); }
    .verb.put, .verb.patch { background: var(--zap-warn-container); color: var(--zap-warn); }
    .verb.delete { background: var(--zap-danger-container); color: var(--zap-danger); }
    .detail { padding: 0 14px 14px; }
    .detail h4 { margin: 14px 0 6px; font: var(--mat-sys-label-large); color: var(--mat-sys-on-surface-variant); }
    .description { margin: 4px 0; }
    .handler { margin: 4px 0; font-size: 12.5px; }
    .objects { display: flex; flex-wrap: wrap; gap: 6px; }
    .object { display: inline-flex; align-items: center; gap: 4px; padding: 2px 10px; border: 1px solid var(--zap-border); border-radius: 999px; color: inherit; text-decoration: none; font-size: 13px; }
    .object:hover { background: var(--mat-sys-surface-container-low); }
    .object mat-icon { width: 16px; height: 16px; }
    .object.unknown { color: var(--mat-sys-on-surface-variant); border-style: dashed; }
    .command { margin-top: 8px; }
    .command-head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; font-size: 12px; margin-bottom: 4px; }
    pre { margin: 0; padding: 10px 12px; border-radius: 10px; background: var(--mat-sys-surface-container-low); font-size: 12.5px; white-space: pre-wrap; overflow-wrap: anywhere; }
    .note { display: flex; align-items: center; gap: 6px; margin: 4px 0 0; font-size: 12.5px; color: var(--zap-warn); }
    .note mat-icon { width: 16px; height: 16px; }
    .params { width: 100%; }
    .required { color: var(--zap-danger); margin-left: 2px; }
    .response { display: flex; align-items: center; gap: 8px; margin: 6px 0 4px; }
    .where-openapi { margin: 8px 20px 16px; font-size: 13.5px; }
    .where-openapi summary { cursor: pointer; color: var(--mat-sys-primary); }
    .where-openapi form { display: flex; gap: 8px; flex-wrap: wrap; }
    .where-openapi input { flex: 1 1 260px; height: 36px; padding: 0 10px; border: 1px solid var(--zap-border); border-radius: 10px; background: var(--zap-surface); color: var(--mat-sys-on-surface); font: 13px var(--zap-mono, monospace); }
  `,
})
export class ApiEndpoints implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);

  readonly kind = input.required<string>();
  readonly name = input.required<string>();

  protected readonly docs = signal<ApiDocs | null>(null);
  protected readonly problem = signal('');
  protected readonly loading = signal(false);
  protected readonly open = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly method = signal('');
  protected readonly withSql = signal(false);
  protected openApiPath = '';

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;

  protected readonly endpoints = computed(() => (this.docs() ? readEndpoints(this.docs()!) : []));
  protected readonly mappedCount = computed(() => this.endpoints().filter((item) => item.found.length || item.commands.length).length);
  protected readonly methods = computed(() => [...new Set(this.endpoints().map((item) => item.method))]);

  /** By group (the tag of the document, or the controller), what the filters keep. */
  protected readonly groups = computed(() => {
    const words = fold(this.search()).split(/\s+/).filter((word) => word.length);
    const kept = this.endpoints().filter((item) => {
      if (this.method() && item.method !== this.method()) {
        return false;
      }
      if (this.withSql() && !item.found.length && !item.commands.length) {
        return false;
      }
      if (!words.length) {
        return true;
      }
      const text = fold([item.route, item.summary, item.description, item.handler, item.group, ...item.found.map((found) => found.name), ...item.commands.map((command) => command.sql)].join(' \n '));
      return words.every((word) => text.includes(word));
    });
    const groups = new Map<string, Endpoint[]>();
    for (const item of kept) {
      groups.set(item.group, [...(groups.get(item.group) ?? []), item]);
    }
    return [...groups.entries()].map(([name, items]) => ({ name, items })).sort((one, other) => one.name.localeCompare(other.name));
  });

  ngOnInit(): void {
    this.load(false);
  }

  load(fresh: boolean): void {
    this.loading.set(true);
    this.api.apiDocs(this.kind(), this.name(), fresh).subscribe({
      next: (docs) => {
        this.docs.set(docs);
        this.openApiPath = docs.OpenApiSaid ?? '';
        this.problem.set('');
        this.loading.set(false);
      },
      error: (failure: HttpErrorResponse) => {
        this.loading.set(false);
        if (failure.status !== 401) {
          this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.31).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.'));
        }
      },
    });
  }

  protected async saveOpenApi(): Promise<void> {
    try {
      await firstValueFrom(this.api.setOpenApi(this.kind(), this.name(), this.openApiPath.trim() || null));
      this.snack.open(this.openApiPath.trim() ? 'Caminho do OpenAPI gravado' : 'O OpenAPI volta a ser procurado nos caminhos de costume', undefined, { duration: 3500 });
      // The Worker Control reads its configuration again first.
      setTimeout(() => this.load(true), 1500);
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O caminho não foi aceito', 'Fechar', { duration: 8000 });
    }
  }

  protected openApiProblem(docs: ApiDocs): string {
    const problem = docs.OpenApiProblem ?? '';
    if (problem === 'absent' || !problem) {
      return 'a API não publica um documento OpenAPI nos caminhos procurados';
    }
    if (problem === 'not-an-api') {
      return 'só APIs servidas pelo servidor web são perguntadas';
    }
    return problem;
  }

  protected tried(docs: ApiDocs): string {
    return [...new Set((docs.OpenApiTried ?? []).map((item) => new URL(item.Url).pathname))].join(', ');
  }

  protected where(place: string): string {
    return ({ path: 'rota', query: 'query', header: 'cabeçalho', cookie: 'cookie', formData: 'formulário' } as Record<string, string>)[place] ?? place;
  }

  protected short(name: string): string {
    return name.startsWith('dbo.') ? name.slice(4) : name;
  }

  protected schemaOf(name: string): string {
    return name.split('.')[0];
  }

  protected nameOf(name: string): string {
    return name.split('.').slice(1).join('.');
  }
}

function fold(text: string): string {
  return text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}
