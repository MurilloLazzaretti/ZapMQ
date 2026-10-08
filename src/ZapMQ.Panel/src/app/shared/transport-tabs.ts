import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { Transport } from '../core/transport';

/** The screens of the transport: what is waiting to go into a package, and the packages. */
@Component({
  selector: 'zap-transport-tabs',
  imports: [RouterLink, RouterLinkActive, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Transporte">
      <a routerLink="/transporte" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">
        <mat-icon svgIcon="playlist_add" /> Área
        @if (transport.summary()?.area; as count) { <span class="count">{{ count }}</span> }
      </a>
      <a routerLink="/transporte/pacotes" routerLinkActive="active">
        <mat-icon svgIcon="inventory_2" /> Pacotes
        @if (transport.waiting(); as count) { <span class="count hot">{{ count }}</span> }
      </a>
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
    .count { padding: 0 7px; border-radius: 999px; background: var(--mat-sys-surface-container-highest); font: var(--mat-sys-label-small); line-height: 18px; }
    .count.hot { background: var(--zap-warn-container); color: var(--zap-warn); }
  `,
})
export class TransportTabs {
  protected readonly transport = inject(Transport);
}
