import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import { Api } from './api';
import { Session } from './models';

/**
 * Who is logged in. The session itself is a cookie only the service can read; here is what
 * the screens need to know about it.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private checked = false;

  readonly user = signal<string | null>(null);
  readonly name = signal('');
  readonly master = signal(false);
  readonly defaultPassword = signal(false);
  readonly version = signal('');

  /** Asks the service once whether there is a session; afterwards answers from memory. */
  async ensure(): Promise<boolean> {
    if (!this.checked) {
      try {
        this.accept(await firstValueFrom(this.api.session()));
      } catch (error) {
        this.version.set((error as HttpErrorResponse)?.error?.version ?? '');
        this.user.set(null);
      }
      this.checked = true;
    }
    return this.user() !== null;
  }

  async login(user: string, password: string): Promise<void> {
    this.accept(await firstValueFrom(this.api.login(user, password)));
    this.checked = true;
  }

  /** The user choosing its own password; the session goes on. */
  async changePassword(current: string, password: string): Promise<void> {
    this.accept(await firstValueFrom(this.api.changePassword(current, password)));
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.api.logout());
    } finally {
      this.expire();
    }
  }

  /** The service refused the session: back to the way in, remembering where the user was. */
  expire(): void {
    const wasIn = this.user() !== null;
    this.user.set(null);
    const url = this.router.url;
    if (!url.startsWith('/login')) {
      void this.router.navigate(['/login'], { queryParams: wasIn && url !== '/' ? { voltar: url } : {} });
    }
  }

  private accept(session: Session): void {
    this.user.set(session.user);
    this.name.set(session.name ?? '');
    this.master.set(!!session.master);
    this.defaultPassword.set(!!session.defaultPassword);
    if (session.version) {
      this.version.set(session.version);
    }
  }
}

export const requireSession: CanActivateFn = async (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);
  if (await auth.ensure()) {
    return true;
  }
  return router.createUrlTree(['/login'], { queryParams: state.url !== '/' ? { voltar: state.url } : {} });
};

export const sessionInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(Auth);
  return next(request).pipe(
    catchError((error: HttpErrorResponse) => {
      const ownQuestion = request.url.endsWith('api/session') || request.url.endsWith('api/login');
      if (error.status === 401 && !ownQuestion) {
        auth.expire();
      }
      return throwError(() => error);
    }),
  );
};

/** The users are the master's business alone. */
export const requireMaster: CanActivateFn = async () => {
  const auth = inject(Auth);
  await auth.ensure();
  return auth.master() ? true : inject(Router).createUrlTree(['/']);
};
