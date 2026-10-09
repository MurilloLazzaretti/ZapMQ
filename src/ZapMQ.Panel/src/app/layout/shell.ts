import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatTooltipModule } from '@angular/material/tooltip';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { Auth } from '../core/auth';
import { Live } from '../core/live';
import { Theme } from '../core/theme';
import { PasswordDialog } from '../shared/user-dialogs';
import { Transport } from '../core/transport';

interface Destination {
  path: string;
  label: string;
  icon: string;
  exact?: boolean;
  /** Another address that belongs to this destination too. */
  also?: string;
  badge?: () => number;
}

/**
 * The frame around every screen: navigation on the side (a rail on wide screens, a drawer on
 * narrow ones) and a bar on top with what is always worth seeing.
 */
@Component({
  selector: 'zap-shell',
  imports: [RouterOutlet, RouterLink, MatSidenavModule, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, MatBadgeModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly auth = inject(Auth);
  protected readonly live = inject(Live);
  protected readonly theme = inject(Theme);
  private readonly transport = inject(Transport);

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
    { path: '/mapa', label: 'Mapa', icon: 'hub' },
    { path: '/filas', label: 'Mensageria', icon: 'stacks', also: '/mortas', badge: this.dead },
    { path: '/workers', label: 'Processos e serviços', icon: 'precision_manufacturing' },
    { path: '/trafego', label: 'Tráfego HTTP', icon: 'monitoring' },
    { path: '/web', label: 'Aplicação web', icon: 'web' },
    { path: '/banco', label: 'Banco de dados', icon: 'database' },
    { path: '/transporte', label: 'Transporte', icon: 'local_shipping', badge: this.transport.waiting },
  ];

  protected readonly themeIcon = computed(() => (this.theme.choice() === 'auto' ? 'tune' : this.theme.choice() === 'dark' ? 'dark_mode' : 'light_mode'));
  protected readonly themeLabel = computed(() => (this.theme.choice() === 'auto' ? 'Tema: do sistema' : this.theme.choice() === 'dark' ? 'Tema: escuro' : 'Tema: claro'));

  /** Where the panel is now, without what comes after the path. */
  private readonly address = signal(this.router.url.split(/[?#]/)[0]);

  /** Whether a destination of the menu is the one on screen. */
  protected current(destination: Destination): boolean {
    const address = this.address();
    const under = (path: string) => address === path || address.startsWith(path + '/');
    return destination.exact ? address === destination.path : under(destination.path) || (!!destination.also && under(destination.also));
  }

  ngOnInit(): void {
    this.router.events.subscribe((event) => {
      if (event instanceof NavigationEnd) {
        this.address.set(event.urlAfterRedirects.split(/[?#]/)[0]);
      }
    });
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

  protected changePassword(): void {
    this.dialog
      .open(PasswordDialog, { maxWidth: '460px', width: 'calc(100vw - 32px)' })
      .afterClosed()
      .subscribe((changed) => changed && this.snack.open('Senha trocada', undefined, { duration: 2500 }));
  }

  protected navigated(): void {
    this.drawerOpen.set(false);
  }
}
