import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { Auth } from '../core/auth';
import { Live } from '../core/live';
import { Theme } from '../core/theme';

interface Destination {
  path: string;
  label: string;
  icon: string;
  exact?: boolean;
  badge?: () => number;
}

/**
 * The frame around every screen: navigation on the side (a rail on wide screens, a drawer on
 * narrow ones) and a bar on top with what is always worth seeing.
 */
@Component({
  selector: 'zap-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, MatBadgeModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit, OnDestroy {
  protected readonly auth = inject(Auth);
  protected readonly live = inject(Live);
  protected readonly theme = inject(Theme);

  private readonly breakpoints = inject(BreakpointObserver);
  /** Below this the navigation becomes a drawer that opens over the content. */
  protected readonly narrow = toSignal(this.breakpoints.observe('(max-width: 959.98px)').pipe(map((state) => state.matches)), { initialValue: false });
  /** Between this and the next the navigation is a rail of icons. */
  protected readonly medium = toSignal(this.breakpoints.observe('(max-width: 1279.98px)').pipe(map((state) => state.matches)), { initialValue: false });
  protected readonly collapsed = signal(false);
  protected readonly rail = computed(() => !this.narrow() && (this.medium() || this.collapsed()));
  protected readonly drawerOpen = signal(false);

  private readonly dead = computed(() => {
    const letters = this.live.overview()?.deadLetters;
    return letters ? letters.expired + letters.notConsumed + letters.unconfirmed : 0;
  });

  protected readonly destinations: Destination[] = [
    { path: '/', label: 'Visão geral', icon: 'dashboard', exact: true },
    { path: '/filas', label: 'Filas', icon: 'stacks' },
    { path: '/mortas', label: 'Mensagens mortas', icon: 'skull', badge: this.dead },
    { path: '/aplicacoes', label: 'Aplicações', icon: 'lan' },
  ];

  protected readonly themeIcon = computed(() => (this.theme.choice() === 'auto' ? 'tune' : this.theme.choice() === 'dark' ? 'dark_mode' : 'light_mode'));
  protected readonly themeLabel = computed(() => (this.theme.choice() === 'auto' ? 'Tema: do sistema' : this.theme.choice() === 'dark' ? 'Tema: escuro' : 'Tema: claro'));

  ngOnInit(): void {
    this.live.start();
  }

  ngOnDestroy(): void {
    this.live.stop();
  }

  protected toggleNavigation(): void {
    if (this.narrow()) {
      this.drawerOpen.update((open) => !open);
    } else {
      this.collapsed.update((collapsed) => !collapsed);
    }
  }

  protected navigated(): void {
    this.drawerOpen.set(false);
  }
}
