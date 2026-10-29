import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { kindIcon, kindName } from '../core/database';
import { AgoPipe, WhenPipe } from '../core/format';
import { ApiDocs } from '../core/models';
import { Endpoint, readEndpoints } from '../core/openapi';
import { openApiProblem } from '../shared/api-endpoints-card';

/**
 * The endpoints of one API, on a screen of their own: the list on one side, and on the other the
 * one chosen, with what it runs in the database and what its OpenAPI document says of it.
 */
@Component({
  selector: 'zap-api-endpoints',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './api-endpoints.html',
  styleUrl: './api-endpoints.scss',
})
export class ApiEndpointsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);

  protected readonly kind = this.route.snapshot.paramMap.get('kind') ?? 'api';
  protected readonly name = this.route.snapshot.paramMap.get('name') ?? '';

  protected readonly docs = signal<ApiDocs | null>(null);
  protected readonly problem = signal('');
  protected readonly loading = signal(false);
  protected readonly search = signal('');
  protected readonly method = signal('');
  protected readonly withSql = signal(false);
  protected readonly chosen = signal<string | null>(null);
  /** What is shown of the chosen one: the database, or what the document says. */
  protected readonly showing = signal<'banco' | 'doc'>('banco');
  protected openApiPath = '';

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly openApiProblem = openApiProblem;

  protected readonly endpoints = computed(() => (this.docs() ? readEndpoints(this.docs()!) : []));
  protected readonly mappedCount = computed(() => this.endpoints().filter((item) => item.found.length || item.commands.length).length);
  protected readonly methods = computed(() => [...new Set(this.endpoints().map((item) => item.method))]);

  protected readonly kept = computed(() => {
    const words = fold(this.search()).split(/\s+/).filter((word) => word.length);
    return this.endpoints().filter((item) => {
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
  });

  /** By group (the tag of the document, or the controller), what the filters keep. */
  protected readonly groups = computed(() => {
    const groups = new Map<string, Endpoint[]>();
    for (const item of this.kept()) {
      groups.set(item.group, [...(groups.get(item.group) ?? []), item]);
    }
    return [...groups.entries()].map(([name, items]) => ({ name, items })).sort((one, other) => one.name.localeCompare(other.name));
  });

  protected readonly selected = computed(() => this.endpoints().find((item) => item.key === this.chosen()) ?? null);

  ngOnInit(): void {
    const query = this.route.snapshot.queryParamMap;
    this.search.set(query.get('busca') ?? '');
    this.chosen.set(query.get('endpoint'));
    this.load(false);
  }

  load(fresh: boolean): void {
    this.loading.set(true);
    this.api.apiDocs(this.kind, this.name, fresh).subscribe({
      next: (docs) => {
        this.docs.set(docs);
        this.openApiPath = docs.OpenApiSaid ?? '';
        this.problem.set('');
        this.loading.set(false);
        // Without one chosen, the first one, so the screen is never half empty.
        if (!this.selected() && this.endpoints().length && window.innerWidth >= 960) {
          this.choose(this.groups()[0]?.items[0] ?? this.endpoints()[0], true);
        }
      },
      error: (failure: HttpErrorResponse) => {
        this.loading.set(false);
        if (failure.status === 404) {
          this.problem.set('Esta aplicação não existe nesta máquina.');
        } else if (failure.status !== 401) {
          this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.31).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.'));
        }
      },
    });
  }

  protected choose(item: Endpoint, quietly = false): void {
    this.chosen.set(item.key);
    // What it runs first, when the map says it; otherwise what the document says.
    this.showing.set(item.found.length || item.commands.length || !item.documented ? 'banco' : 'doc');
    this.remember();
    if (!quietly && window.innerWidth < 960) {
      setTimeout(() => document.querySelector('.detail')?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
    }
  }

  protected typed(text: string): void {
    this.search.set(text);
    this.remember();
  }

  private remember(): void {
    void this.router.navigate([], { queryParams: { busca: this.search().trim() || null, endpoint: this.chosen() }, replaceUrl: true });
  }

  protected async saveOpenApi(): Promise<void> {
    try {
      await firstValueFrom(this.api.setOpenApi(this.kind, this.name, this.openApiPath.trim() || null));
      this.snack.open(this.openApiPath.trim() ? 'Caminho do OpenAPI gravado' : 'O OpenAPI volta a ser procurado nos caminhos de costume', undefined, { duration: 3500 });
      // The Worker Control reads its configuration again first.
      setTimeout(() => this.load(true), 1500);
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O caminho não foi aceito', 'Fechar', { duration: 8000 });
    }
  }

  protected tried(docs: ApiDocs): string {
    return [...new Set((docs.OpenApiTried ?? []).map((item) => new URL(item.Url).pathname))].join(', ');
  }

  protected where(place: string): string {
    return ({ path: 'rota', query: 'query', header: 'cabeçalho', cookie: 'cookie', formData: 'formulário' } as Record<string, string>)[place] ?? place;
  }

  protected schemaOf(name: string): string {
    return name.split('.')[0];
  }

  protected nameOf(name: string): string {
    return name.split('.').slice(1).join('.');
  }

  protected uses(item: Endpoint): boolean {
    return !!(item.found.length || item.commands.length);
  }
}

function fold(text: string): string {
  return text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}
