import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { actionName, actionTone, kindIcon, kindName } from '../core/database';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { ObjectChange, ObjectTracking } from '../core/models';
import { Transport } from '../core/transport';
import { DatabaseTabs } from '../shared/database-tabs';

const PERIODS = [
  { days: 7, label: '7 dias' },
  { days: 30, label: '30 dias' },
  { days: 365, label: '1 ano' },
];

const KINDS = ['Table', 'View', 'Procedure', 'Function', 'Type'];

/**
 * What changed in the objects of the databases: created, altered, renamed and dropped, as the
 * Worker Control noticed between one look and the next.
 */
@Component({
  selector: 'zap-database-changes',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, DatabaseTabs, NumPipe, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './database-changes.html',
  styleUrl: './database-changes.scss',
})
export class DatabaseChangesPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly transport = inject(Transport);
  /** When each object last went into a package made here. */
  private readonly packaged = signal<Record<string, string>>({});
  private timer: ReturnType<typeof setInterval> | null = null;
  private typing: ReturnType<typeof setTimeout> | null = null;
  private asked = 0;

  protected readonly periods = PERIODS;
  protected readonly kinds = KINDS;
  protected readonly days = signal(30);
  protected readonly kind = signal('');
  protected readonly search = signal('');
  protected readonly changes = signal<ObjectChange[] | null>(null);
  protected readonly total = signal(0);
  protected readonly tracking = signal<ObjectTracking[]>([]);
  protected readonly problem = signal('');
  protected readonly outdated = signal(false);

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly actionName = actionName;
  protected readonly actionTone = actionTone;

  ngOnInit(): void {
    const query = this.route.snapshot.queryParamMap;
    this.days.set(Number(query.get('dias')) || 30);
    this.kind.set(query.get('tipo') ?? '');
    this.search.set(query.get('busca') ?? '');
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 30000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
    if (this.typing) {
      clearTimeout(this.typing);
    }
  }

  protected choose<T>(target: { set(value: T): void }, value: T): void {
    target.set(value);
    this.refresh();
  }

  protected typed(text: string): void {
    this.search.set(text);
    if (this.typing) {
      clearTimeout(this.typing);
    }
    this.typing = setTimeout(() => this.refresh(), 300);
  }

  /** The change came after the last package that carried the object, or no package ever did. */
  protected pending(change: ObjectChange): boolean {
    const last = this.packaged()[`${change.Kind}|${change.Schema}|${change.Name}`.toLowerCase()];
    return !last || Date.parse(last) < Date.parse(change.At);
  }

  protected async carry(change: ObjectChange, event: Event): Promise<void> {
    event.stopPropagation();
    await this.transport.add({ database: change.Database, kind: change.Kind, schema: change.Schema, name: change.Name, drop: change.Action === 'Dropped' });
  }

  protected who(change: ObjectChange): string {
    return [change.Login, change.Host, change.Application].filter(Boolean).join(' · ');
  }

  private refresh(): void {
    const mine = ++this.asked;
    this.router.navigate([], { queryParams: { dias: this.days() === 30 ? null : this.days(), tipo: this.kind() || null, busca: this.search() || null }, replaceUrl: true });
    this.api.packaged().subscribe({ next: (packaged) => this.packaged.set(packaged), error: () => undefined });
    this.api.databaseChanges({ days: this.days(), kind: this.kind(), search: this.search(), limit: 300 }).subscribe({
      next: (answer) => {
        if (mine !== this.asked) {
          return;
        }
        this.changes.set(answer.Changes);
        this.total.set(answer.Total);
        this.tracking.set(answer.Tracking);
        this.problem.set('');
        this.outdated.set(false);
      },
      error: (failure: HttpErrorResponse) => {
        if (mine !== this.asked) {
          return;
        }
        if (failure.status === 501) {
          this.outdated.set(true);
        } else if (failure.status !== 401) {
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }
}
