import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { WhenPipe } from '../core/format';
import { ApiDocs } from '../core/models';
import { readEndpoints } from '../core/openapi';

/** Why the OpenAPI document of an API is not there, in words. */
export function openApiProblem(docs: ApiDocs): string {
  const problem = docs.OpenApiProblem ?? '';
  if (problem === 'absent' || !problem) {
    return 'a API não publica um documento OpenAPI nos caminhos procurados';
  }
  if (problem === 'not-an-api') {
    return 'só APIs servidas pelo servidor web são perguntadas';
  }
  return problem;
}

/**
 * On the screen of an API, what it says about its endpoints in a few lines, and the way to the
 * screen of its endpoints.
 */
@Component({
  selector: 'zap-api-endpoints-card',
  imports: [RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <div class="panel-head">
        <h2>Endpoints</h2>
        @if (docs(); as now) {
          @if (now.OpenApi || now.Map) {
            <span class="hint">{{ count() }} endpoints · {{ mapped() }} usam o banco</span>
          }
        }
        <span class="spacer"></span>
        @if (docs()?.OpenApi || docs()?.Map) {
          <a mat-flat-button [routerLink]="['/workers/aplicacoes', kind(), name(), 'endpoints']"><mat-icon svgIcon="open_in_new" /> Abrir endpoints</a>
        }
      </div>
      @if (problem()) {
        <p class="line muted">{{ problem() }}</p>
      } @else if (docs(); as now) {
        @if (!now.OpenApi && !now.Map) {
          <p class="line"><mat-icon svgIcon="info" /> <span><strong>Recurso não disponível para esta API:</strong> <span class="muted">ela não publica OpenAPI e não traz o mapa de endpoints. Para habilitar, veja <span class="mono">docs/ENDPOINTS.md</span> no ZapMQ.</span></span></p>
        } @else {
          <p class="line">
            <span class="source" [class.ok]="now.OpenApi"><mat-icon [svgIcon]="now.OpenApi ? 'check_circle' : 'info'" /> {{ now.OpenApi ? 'OpenAPI' : 'sem OpenAPI' }}</span>
            <span class="source" [class.ok]="now.Map && !now.MapStale" [class.warn]="now.MapStale" [matTooltip]="now.MapStale ? 'Há binários mais novos que o mapa: ele pode não corresponder ao que está instalado' : ''">
              <mat-icon [svgIcon]="now.Map ? (now.MapStale ? 'warning' : 'check_circle') : 'info'" /> {{ now.Map ? 'mapa do banco' + (now.MapBuiltAt ? ' de ' + (now.MapBuiltAt | when) : '') : 'sem mapa do banco' }}
            </span>
          </p>
        }
      } @else {
        <p class="line muted">Lendo…</p>
      }
    </section>
  `,
  styles: `
    .spacer { flex: 1; }
    .line { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 16px; margin: 0; padding: 0 20px 16px; font-size: 13.5px; }
    .line > mat-icon { flex: none; color: var(--mat-sys-primary); }
    .source { display: inline-flex; align-items: center; gap: 4px; color: var(--mat-sys-on-surface-variant); }
    .source mat-icon { width: 16px; height: 16px; }
    .source.ok mat-icon { color: var(--zap-ok); }
    .source.warn, .source.warn mat-icon { color: var(--zap-warn); }
  `,
})
export class ApiEndpointsCard implements OnInit {
  private readonly api = inject(Api);

  readonly kind = input.required<string>();
  readonly name = input.required<string>();

  protected readonly docs = signal<ApiDocs | null>(null);
  protected readonly problem = signal('');
  private readonly endpoints = computed(() => (this.docs() ? readEndpoints(this.docs()!) : []));
  protected readonly count = computed(() => this.endpoints().length);
  protected readonly mapped = computed(() => this.endpoints().filter((item) => item.found.length || item.commands.length).length);

  ngOnInit(): void {
    this.api.apiDocs(this.kind(), this.name()).subscribe({
      next: (docs) => this.docs.set(docs),
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.31).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }
}
