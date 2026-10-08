import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive } from '@angular/router';

/** The screens about the database of the environment: how it is doing and what is defined in it. */
@Component({
  selector: 'zap-database-tabs',
  imports: [RouterLink, RouterLinkActive, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Banco de dados">
      <a routerLink="/banco" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }"><mat-icon svgIcon="monitor_heart" /> Saúde</a>
      <a routerLink="/banco/objetos" routerLinkActive="active"><mat-icon svgIcon="table" /> Objetos</a>
    </nav>
  `,
  styles: `
    :host { display: block; min-width: 0; max-width: 100%; }
    nav { display: inline-flex; gap: 4px; padding: 4px; border-radius: 999px; background: var(--mat-sys-surface-container-high); max-width: 100%; overflow-x: auto; }
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
  `,
})
export class DatabaseTabs {}
