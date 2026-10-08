import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { actionName, actionTone, kindIcon, kindName } from '../core/database';
import { diff } from '../core/diff';
import { NumPipe, WhenPipe } from '../core/format';
import { ObjectChange } from '../core/models';
import { DiffView } from '../shared/diff-view';

/** One change to an object of a database: what it was and what it became, line by line. */
@Component({
  selector: 'zap-database-change',
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, DiffView, NumPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './database-change.html',
  styleUrl: './database-change.scss',
})
export class DatabaseChangePage implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);

  protected readonly change = signal<ObjectChange | null>(null);
  protected readonly problem = signal('');
  protected readonly missing = signal(false);
  /** Every line, or only the ones that changed with a few around them. */
  protected readonly whole = signal(false);

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly actionName = actionName;
  protected readonly actionTone = actionTone;

  protected readonly lines = computed(() => {
    const change = this.change();
    return change ? diff(change.OldScript ?? '', change.NewScript ?? '') : [];
  });

  protected readonly counts = computed(() => ({
    added: this.lines().filter((line) => line.kind === '+').length,
    removed: this.lines().filter((line) => line.kind === '-').length,
  }));

  /** Both sides there are to compare; a created or dropped object has one. */
  protected readonly compared = computed(() => !!this.change()?.OldScript && !!this.change()?.NewScript);

  protected readonly who = computed(() => {
    const change = this.change();
    return change ? [change.Login, change.Host, change.Application].filter(Boolean).join(' · ') : '';
  });

  ngOnInit(): void {
    this.api.databaseChange(Number(this.route.snapshot.paramMap.get('id'))).subscribe({
      next: (answer) => this.change.set(answer.Change),
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
