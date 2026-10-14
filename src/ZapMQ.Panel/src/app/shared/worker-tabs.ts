import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';

/** The screens of the area for what runs on the machine: micro services, services, applications, the proxy and what happened. */
@Component({
  selector: 'zap-worker-tabs',
  imports: [RouterLink, RouterLinkActive, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Processos e serviços">
      <a routerLink="/workers" [class.active]="groups"><mat-icon svgIcon="memory" /> Micro serviços</a>
      <a routerLink="/workers/servicos" routerLinkActive="active"><mat-icon svgIcon="settings_applications" /> Serviços do Windows</a>
      <a routerLink="/workers/aplicacoes" routerLinkActive="active"><mat-icon svgIcon="deployed_code" /> Aplicações</a>
      <a routerLink="/workers/proxy" routerLinkActive="active"><mat-icon svgIcon="dns" /> Proxy reverso</a>
      <a routerLink="/workers/eventos" routerLinkActive="active"><mat-icon svgIcon="history" /> Histórico</a>
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
    @media (max-width: 1099.98px) {
      a mat-icon { display: none; }
    }
    @media (max-width: 560px) {
      a { padding: 8px 12px; }
      a mat-icon { display: none; }
    }
    a.active { background: var(--zap-surface); color: var(--mat-sys-on-surface); box-shadow: var(--zap-shadow); }
  `,
})
export class WorkerTabs {
  /** The groups are looked after and set up under the same tab. */
  protected readonly groups = /^\/workers(\/configuracao)?(\?|$)/.test(inject(Router).url);
}
