import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive } from '@angular/router';

/** The three screens of the Worker Control section. */
@Component({
  selector: 'zap-worker-tabs',
  imports: [RouterLink, RouterLinkActive, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Worker Control">
      <a routerLink="/workers" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }"><mat-icon svgIcon="precision_manufacturing" /> Grupos</a>
      <a routerLink="/workers/eventos" routerLinkActive="active"><mat-icon svgIcon="history" /> Histórico</a>
      <a routerLink="/workers/configuracao" routerLinkActive="active"><mat-icon svgIcon="tune" /> Configuração</a>
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
    @media (max-width: 560px) {
      a { padding: 8px 12px; }
      a mat-icon { display: none; }
    }
    a.active { background: var(--zap-surface); color: var(--mat-sys-on-surface); box-shadow: var(--zap-shadow); }
  `,
})
export class WorkerTabs {}
