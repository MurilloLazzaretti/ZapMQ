import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { AgoPipe, WhenPipe } from '../core/format';
import { PanelUser } from '../core/models';
import { confirm } from '../shared/dialogs';
import { UserDialog } from '../shared/user-dialogs';

/** Who may get into the panel. Only the master sees this screen. */
@Component({
  selector: 'zap-users',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Usuários</h1>
          <p class="muted">Quem entra no painel. Quem entra vê e faz tudo, menos gerenciar os usuários.</p>
        </div>
        <span class="spacer"></span>
        <button mat-flat-button (click)="create()"><mat-icon svgIcon="add" /> Novo usuário</button>
      </header>

      @if (problem()) {
        <section class="panel empty"><mat-icon svgIcon="cloud_off" /><strong>Não foi possível ler os usuários</strong><span>{{ problem() }}</span></section>
      } @else if (users(); as list) {
        <section class="panel">
          <div class="table-wrap">
            <table class="data stack">
              <thead>
                <tr><th>Usuário</th><th>Situação</th><th>Último acesso</th><th>Criado</th><th></th></tr>
              </thead>
              <tbody>
                @for (user of list; track user.login) {
                  <tr [class.off]="!user.enabled">
                    <td data-label="Usuário">
                      <strong>{{ user.login }}</strong>
                      @if (user.name && user.name !== user.login) { <span class="muted sub">{{ user.name }}</span> }
                      @if (user.master) { <span class="pill primary">master</span> }
                      @if (user.login === auth.user()) { <span class="pill">você</span> }
                    </td>
                    <td data-label="Situação">
                      @if (!user.enabled) {
                        <span class="pill danger">desativado</span>
                      } @else if (user.initialPassword) {
                        <span class="pill warn" matTooltip="Ainda não escolheu a própria senha">senha inicial</span>
                      } @else {
                        <span class="pill ok">ativo</span>
                      }
                    </td>
                    <td data-label="Último acesso" [matTooltip]="user.lastLoginAt | when">{{ user.lastLoginAt ? (user.lastLoginAt | ago) : 'nunca entrou' }}</td>
                    <td data-label="Criado">{{ user.createdAt | when }}@if (user.createdBy) { <span class="muted sub">por {{ user.createdBy }}</span> }</td>
                    <td class="actions">
                      @if (!user.master) {
                        <button mat-icon-button [matMenuTriggerFor]="menu" [attr.aria-label]="'Ações de ' + user.login"><mat-icon svgIcon="more_vert" /></button>
                        <mat-menu #menu="matMenu" xPosition="before">
                          <button mat-menu-item (click)="reset(user)"><mat-icon svgIcon="key" /><span>Redefinir senha</span></button>
                          <button mat-menu-item (click)="enable(user, !user.enabled)"><mat-icon [svgIcon]="user.enabled ? 'block' : 'check_circle'" /><span>{{ user.enabled ? 'Desativar' : 'Ativar' }}</span></button>
                          <button mat-menu-item (click)="remove(user)"><mat-icon svgIcon="delete" /><span>Excluir</span></button>
                        </mat-menu>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        <p class="muted note">
          A senha do master é trocada no menu com o nome do usuário, no alto da tela. Se ela for perdida, apague o arquivo de usuários do serviço:
          na partida seguinte o master volta a ser o das configurações, e os outros usuários precisam ser criados de novo.
        </p>
      } @else {
        <section class="panel empty"><span>Lendo…</span></section>
      }
    </div>
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px 16px; flex-wrap: wrap; }
    .head h1 { margin: 0; font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; }
    .head p { margin: 2px 0 0; }
    .head .spacer { flex: 1; }
    .sub { margin-left: 6px; }
    td .pill { margin-left: 8px; }
    td[data-label='Situação'] .pill { margin-left: 0; }
    tr.off td:not(.actions) { opacity: 0.6; }
    .actions { width: 48px; text-align: right; }
    .note { margin: 0; max-width: 90ch; font: var(--mat-sys-body-small); }
  `,
})
export class UsersPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly auth = inject(Auth);

  protected readonly users = signal<PanelUser[] | null>(null);
  protected readonly problem = signal('');

  ngOnInit(): void {
    this.load();
  }

  protected async create(): Promise<void> {
    if (await firstValueFrom(this.dialog.open(UserDialog, { data: {}, maxWidth: '460px', width: 'calc(100vw - 32px)' }).afterClosed())) {
      this.snack.open('Usuário criado', undefined, { duration: 2500 });
      this.load();
    }
  }

  protected async reset(user: PanelUser): Promise<void> {
    if (await firstValueFrom(this.dialog.open(UserDialog, { data: { reset: user.login }, maxWidth: '460px', width: 'calc(100vw - 32px)' }).afterClosed())) {
      this.snack.open(`Senha de ${user.login} redefinida`, undefined, { duration: 2500 });
      this.load();
    }
  }

  protected async enable(user: PanelUser, enabled: boolean): Promise<void> {
    if (!enabled && !(await confirm(this.dialog, { title: `Desativar ${user.login}?`, message: 'O usuário deixa de entrar no painel, e quem estiver com ele aberto é desconectado. Pode ser ativado de novo depois.', action: 'Desativar', danger: true }))) {
      return;
    }
    await this.act(this.api.changeUser(user.login, { enabled }), enabled ? `${user.login} ativado` : `${user.login} desativado`);
  }

  protected async remove(user: PanelUser): Promise<void> {
    if (await confirm(this.dialog, { title: `Excluir ${user.login}?`, message: 'O usuário é removido de vez. Para só impedir a entrada, desative.', action: 'Excluir', danger: true })) {
      await this.act(this.api.deleteUser(user.login), `${user.login} excluído`);
    }
  }

  private async act(request: ReturnType<Api['deleteUser']> | ReturnType<Api['changeUser']>, done: string): Promise<void> {
    try {
      await firstValueFrom(request as ReturnType<Api['deleteUser']>);
      this.snack.open(done, undefined, { duration: 2500 });
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 7000 });
    }
    this.load();
  }

  private load(): void {
    this.api.users().subscribe({
      next: (answer) => {
        this.users.set(answer.users);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }
}
