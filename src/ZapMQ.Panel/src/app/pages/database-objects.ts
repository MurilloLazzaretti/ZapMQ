import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { kindIcon, kindName, size, variety } from '../core/database';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { CatalogObject, CatalogPage } from '../core/models';
import { DatabaseTabs } from '../shared/database-tabs';

const PAGE = 100;

/**
 * What is defined in a database of the environment: tables, views, procedures, functions and
 * types. Read by the Worker Control from the catalog; no row of any table comes here.
 */
@Component({
  selector: 'zap-database-objects',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, DatabaseTabs, NumPipe, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './database-objects.html',
  styleUrl: './database-objects.scss',
})
export class DatabaseObjectsPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private typing: ReturnType<typeof setTimeout> | null = null;
  private asked = 0;

  /** The databases the Worker Control watches. */
  protected readonly databases = signal<string[]>([]);
  protected readonly database = signal('');
  protected readonly kind = signal('');
  protected readonly schema = signal('');
  protected readonly search = signal('');
  protected readonly sort = signal('name');
  protected readonly limit = signal(PAGE);
  protected readonly page = signal<CatalogPage | null>(null);
  protected readonly problem = signal('');
  /** Why there is nothing to list, when the reason is in the configuration. */
  protected readonly unset = signal('');
  protected readonly outdated = signal(false);
  protected readonly loading = signal(true);

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly variety = variety;
  protected readonly size = size;

  ngOnInit(): void {
    // Where the screen was is in the address, so that coming back from an object finds it as it was left.
    const query = this.route.snapshot.queryParamMap;
    this.database.set(query.get('banco') ?? '');
    this.kind.set(query.get('tipo') ?? '');
    this.schema.set(query.get('schema') ?? '');
    this.search.set(query.get('busca') ?? '');
    this.sort.set(query.get('ordem') ?? 'name');
    this.api.database().subscribe({ next: (state) => this.databases.set(state.Databases), error: () => undefined });
    this.refresh();
  }

  ngOnDestroy(): void {
    if (this.typing) {
      clearTimeout(this.typing);
    }
  }

  protected choose(target: { set(value: string): void }, value: string): void {
    target.set(value);
    this.limit.set(PAGE);
    this.refresh();
  }

  protected typed(text: string): void {
    this.search.set(text);
    this.limit.set(PAGE);
    if (this.typing) {
      clearTimeout(this.typing);
    }
    this.typing = setTimeout(() => this.refresh(), 300);
  }

  protected more(): void {
    this.limit.update((limit) => limit + PAGE);
    this.refresh();
  }

  protected link(item: CatalogObject): string[] {
    return ['/banco/objetos', this.page()!.Database, item.Kind, item.Schema, item.Name];
  }

  protected refresh(fresh = false): void {
    const mine = ++this.asked;
    this.loading.set(true);
    this.router.navigate([], {
      queryParams: { banco: this.database() || null, tipo: this.kind() || null, schema: this.schema() || null, busca: this.search() || null, ordem: this.sort() === 'name' ? null : this.sort() },
      replaceUrl: true,
    });
    this.api
      .databaseObjects({ database: this.database(), kind: this.kind(), schema: this.schema(), search: this.search(), sort: this.sort(), limit: this.limit(), fresh })
      .subscribe({
        next: (page) => {
          if (mine !== this.asked) {
            return;
          }
          this.page.set(page);
          this.problem.set('');
          this.unset.set('');
          this.outdated.set(false);
          this.loading.set(false);
        },
        error: (failure: HttpErrorResponse) => {
          if (mine !== this.asked) {
            return;
          }
          this.loading.set(false);
          if (failure.status === 501) {
            this.outdated.set(true);
          } else if (failure.status === 404 || failure.error?.code === 'not-configured') {
            this.page.set(null);
            this.unset.set(failure.error?.code ?? 'not-found');
          } else if (failure.status !== 401) {
            this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
          }
        },
      });
  }
}
