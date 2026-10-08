import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { Live } from '../core/live';
import { NumPipe } from '../core/format';

/** The two screens about queues: the queues themselves, and what died in them. */
@Component({
  selector: 'zap-queue-tabs',
  imports: [RouterLink, RouterLinkActive, MatIconModule, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Filas">
      <a routerLink="/filas" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }"><mat-icon svgIcon="stacks" /> Filas</a>
      <a routerLink="/mortas" routerLinkActive="active">
        <mat-icon svgIcon="skull" /> Mensagens mortas
        @if (dead() > 0) {
          <span class="count">{{ dead() | num: 'compact' }}</span>
        }
      </a>
    </nav>
  `,
  styles: `
    :host { display: block; min-width: 0; max-width: 100%; }
    nav {
      display: inline-flex;
      gap: 4px;
      padding: 4px;
      border-radius: 999px;
      background: var(--mat-sys-surface-container-high);
      max-width: 100%;
      overflow-x: auto;
    }
    a {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 8px 16px;
      border-radius: 999px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-large);
      text-decoration: none !important;
      white-space: nowrap;
    }
    a mat-icon { width: 18px; height: 18px; }
    a:hover { color: var(--mat-sys-on-surface); }
    a.active { background: var(--zap-surface); color: var(--mat-sys-on-surface); box-shadow: var(--zap-shadow); }
    .count {
      min-width: 20px;
      padding: 0 6px;
      border-radius: 999px;
      background: var(--zap-danger);
      color: var(--mat-sys-surface);
      font: var(--mat-sys-label-small);
      line-height: 18px;
      text-align: center;
    }
    @media (max-width: 560px) {
      a { padding: 8px 12px; }
      a mat-icon { display: none; }
    }
  `,
})
export class QueueTabs {
  private readonly live = inject(Live);

  protected readonly dead = computed(() => {
    const letters = this.live.overview()?.deadLetters;
    return letters ? letters.expired + letters.notConsumed + letters.unconfirmed : 0;
  });
}
