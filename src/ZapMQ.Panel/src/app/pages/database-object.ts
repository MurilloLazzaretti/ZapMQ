import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { Api } from '../core/api';
import { actionName, actionTone, kindIcon, kindName, size, variety } from '../core/database';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { CatalogColumn, CatalogDetail, CatalogReference, ObjectChange } from '../core/models';
import { colour } from '../core/sql';
import { Transport } from '../core/transport';

/** The parts of an object, as the screen calls them. */
const PARTS: Record<string, string> = {
  Columns: 'as colunas',
  Parameters: 'os parâmetros',
  Definition: 'o script',
  Indexes: 'os índices',
  ForeignKeys: 'as chaves estrangeiras',
  Checks: 'as restrições',
  Triggers: 'os triggers',
  Uses: 'o que ele usa',
  UsedBy: 'quem o usa',
  Type: 'a definição do tipo',
  Collation: 'a ordenação do banco',
};

/**
 * One object of a database: what it is made of, what it has to do with the others and what
 * creates it.
 */
@Component({
  selector: 'zap-database-object',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, NumPipe, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './database-object.html',
  styleUrl: './database-object.scss',
})
export class DatabaseObjectPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  protected readonly transport = inject(Transport);
  private watching: Subscription | null = null;
  private copiedTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly detail = signal<CatalogDetail | null>(null);
  protected readonly wanted = signal({ database: '', kind: '', schema: '', name: '' });
  protected readonly problem = signal('');
  protected readonly missing = signal(false);
  protected readonly copied = signal(false);
  /** What happened to this object since it is being watched. */
  protected readonly changes = signal<ObjectChange[]>([]);
  protected readonly actionName = actionName;
  protected readonly actionTone = actionTone;

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly variety = variety;
  protected readonly size = size;

  protected readonly pieces = computed(() => colour(this.detail()?.Script ?? ''));

  /** What is being looked for in the script, and which of the places it was found in is the one being shown. */
  protected readonly find = signal('');
  protected readonly at = signal(0);
  private readonly found = viewChild<ElementRef<HTMLElement>>('found');

  /** Where the text is in the script, whatever the case of the letters. */
  protected readonly hits = computed(() => {
    const script = (this.detail()?.Script ?? '').toLowerCase();
    const wanted = this.find().trim().toLowerCase();
    const places: number[] = [];
    if (wanted.length > 1) {
      for (let index = script.indexOf(wanted); index >= 0; index = script.indexOf(wanted, index + wanted.length)) {
        places.push(index);
      }
    }
    return places;
  });

  /** The script cut where the text was found, each of those pieces with its number. */
  protected readonly marked = computed(() => {
    const script = this.detail()?.Script ?? '';
    const length = this.find().trim().length;
    const parts: { text: string; hit: number | null }[] = [];
    let last = 0;
    this.hits().forEach((place, number) => {
      parts.push({ text: script.slice(last, place), hit: null }, { text: script.slice(place, place + length), hit: number });
      last = place + length;
    });
    parts.push({ text: script.slice(last), hit: null });
    return parts;
  });
  protected readonly lines = computed(() => (this.detail()?.Script ?? '').replace(/\n$/, '').split('\n').length);
  protected readonly unread = computed(() => Object.entries(this.detail()?.Problems ?? {}).map(([part, message]) => ({ part: PARTS[part] ?? part, message })));

  /** The columns that are part of the primary key. */
  protected readonly keys = computed(() => new Set((this.detail()?.Indexes ?? []).filter((index) => index.PrimaryKey).flatMap((index) => index.Columns.map((column) => column.Name))));

  /** Goes to the next place the text was found in, or back to the one before, round and round. */
  protected next(step: number): void {
    const total = this.hits().length;
    if (!total) {
      return;
    }
    this.at.set((this.at() + step + total) % total);
    setTimeout(() => this.found()?.nativeElement.querySelector(`mark[data-hit="${this.at()}"]`)?.scrollIntoView({ block: 'center' }));
  }

  ngOnInit(): void {
    this.transport.loadNames();
    // Coming from a search in the scripts, what was searched for is looked for here too.
    this.find.set(this.route.snapshot.queryParamMap.get('texto') ?? '');
    this.watching = this.route.paramMap.subscribe((parameters) => {
      this.wanted.set({ database: parameters.get('database') ?? '', kind: parameters.get('kind') ?? '', schema: parameters.get('schema') ?? '', name: parameters.get('name') ?? '' });
      this.load();
    });
  }

  ngOnDestroy(): void {
    this.watching?.unsubscribe();
    if (this.copiedTimer) {
      clearTimeout(this.copiedTimer);
    }
  }

  protected extra(column: CatalogColumn): string {
    const notes: string[] = [];
    if (column.Identity) {
      notes.push(`identity(${column.Seed ?? 1}, ${column.Increment ?? 1})`);
    }
    if (column.Computed) {
      notes.push(`calculada: ${column.Computed}${column.Persisted ? ', gravada' : ''}`);
    }
    if (column.Default) {
      notes.push(`default ${column.Default}`);
    }
    return notes.join(' · ');
  }

  protected link(reference: CatalogReference): string[] | null {
    return reference.Kind && reference.Schema && !reference.Database ? ['/banco/objetos', this.detail()!.Database, reference.Kind, reference.Schema, reference.Name] : null;
  }

  protected who(change: ObjectChange): string {
    return [change.Login, change.Host].filter(Boolean).join(' · ');
  }

  protected action(value: string): string {
    return value === 'NO_ACTION' ? '' : value.replace('_', ' ').toLowerCase();
  }

  protected copy(): void {
    const script = this.detail()?.Script;
    if (!script) {
      return;
    }
    navigator.clipboard?.writeText(script).then(() => {
      this.copied.set(true);
      this.copiedTimer = setTimeout(() => this.copied.set(false), 2000);
    });
  }

  protected download(): void {
    const detail = this.detail();
    if (!detail?.Script) {
      return;
    }
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([detail.Script], { type: 'application/sql;charset=utf-8' }));
    link.download = `${detail.Object.Schema}.${detail.Object.Name}.sql`;
    link.click();
    URL.revokeObjectURL(link.href);
  }

  private load(): void {
    const { database, kind, schema, name } = this.wanted();
    this.detail.set(null);
    this.problem.set('');
    this.missing.set(false);
    this.changes.set([]);
    // The page stands without its history.
    this.api.databaseChanges({ database, kind, schema, name, limit: 20 }).subscribe({ next: (answer) => this.changes.set(answer.Changes), error: () => undefined });
    this.api.databaseObject(database, kind, schema, name).subscribe({
      next: (detail) => this.detail.set(detail),
      error: (failure: HttpErrorResponse) => {
        if (failure.status === 404) {
          this.missing.set(true);
        } else if (failure.status !== 401) {
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }
}
