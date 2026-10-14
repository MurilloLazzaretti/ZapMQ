import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive } from '@angular/router';

/** The two sides of the micro services: what is running, and how each group is set up. */
@Component({
  selector: 'zap-group-views',
  imports: [RouterLink, RouterLinkActive, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Micro serviços">
      <a routerLink="/workers" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }"><mat-icon svgIcon="monitor_heart" /> Acompanhar</a>
      <a routerLink="/workers/configuracao" routerLinkActive="on"><mat-icon svgIcon="tune" /> Configurar</a>
    </nav>
  `,
  styles: `
    nav { display: inline-flex; border: 1px solid var(--zap-border); border-radius: 12px; overflow: hidden; }
    a {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 8px 16px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-large);
      text-decoration: none !important;
    }
    a + a { border-left: 1px solid var(--zap-border); }
    a mat-icon { width: 18px; height: 18px; }
    a:hover { color: var(--mat-sys-on-surface); }
    a.on { background: var(--mat-sys-secondary-container); color: var(--mat-sys-on-secondary-container); }
  `,
})
export class GroupViews {}
