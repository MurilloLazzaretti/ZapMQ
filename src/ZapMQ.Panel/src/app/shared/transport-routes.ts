import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { TransportRoute, TransportRoutes } from '../core/models';
import { WhenPipe } from '../core/format';
import { ask, confirm } from './dialogs';

interface Draft {
  id: string | null;
  name: string;
  url: string;
  key: string;
  allowHttp: boolean;
}

/**
 * The ways between this environment and the others: the routes a package can be sent along, and
 * the environments allowed to send here. Only the master changes them.
 */
@Component({
  selector: 'zap-transport-routes',
  imports: [FormsModule, MatButtonModule, MatIconModule, MatTooltipModule, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <div class="panel-head">
        <h2>Rotas de transporte</h2>
        <span class="hint">para onde um pacote pode ser enviado daqui</span>
        <span class="spacer"></span>
        @if (auth.master() && !draft()) {
          <button mat-button (click)="edit(null)"><mat-icon svgIcon="add" /> Nova rota</button>
        }
      </div>
      @if (draft(); as now) {
        <form class="draft" (ngSubmit)="saveRoute()">
          <label>Nome<input name="name" [(ngModel)]="now.name" placeholder="QAS" autocomplete="off" /></label>
          <label class="wide">Endereço do painel de destino<input name="url" class="mono" [(ngModel)]="now.url" placeholder="https://qas.servidor/zapmq" spellcheck="false" autocomplete="off" /></label>
          <label class="wide">Chave da rota
            <input name="key" class="mono" type="password" [(ngModel)]="now.key" [placeholder]="now.id ? 'em branco: continua a mesma' : 'zt1.…, dada pelo destino em Origens autorizadas'" spellcheck="false" autocomplete="off" />
          </label>
          <label class="check"><input type="checkbox" name="http" [(ngModel)]="now.allowHttp" /> Aceitar http, sem certificado (só em rede interna)</label>
          <div class="buttons">
            <button mat-button type="button" (click)="draft.set(null)">Cancelar</button>
            <button mat-flat-button type="submit" [disabled]="busy()"><mat-icon svgIcon="save" /> Salvar</button>
          </div>
        </form>
      }
      @if (data(); as now) {
        @if (now.routes.length) {
          <ul class="rows">
            @for (route of now.routes; track route.id) {
              <li>
                <div class="who">
                  <strong>{{ route.name }}</strong>
                  <span class="mono muted url">{{ route.url }}</span>
                  <span class="tags">
                    @if (route.allowHttp) { <span class="pill warn" matTooltip="Sem TLS: o conteúdo viaja aberto na rede. A assinatura impede alterá-lo, não lê-lo">http</span> }
                    @if (!route.keyReadable) { <span class="pill danger">a chave precisa ser informada de novo</span> }
                    @if (route.lastProblem) { <span class="pill danger" [matTooltip]="route.lastProblem">falhou {{ route.lastUsedAt | when }}</span> }
                    @else if (route.lastUsedAt) { <span class="pill ok">usada {{ route.lastUsedAt | when }}</span> }
                  </span>
                </div>
                <span class="spacer"></span>
                <button mat-button (click)="test(route)" [disabled]="busy()" matTooltip="Pergunta ao destino quem ele é, com a assinatura desta rota"><mat-icon svgIcon="sync" /> Testar</button>
                @if (auth.master()) {
                  <button mat-icon-button (click)="edit(route)" matTooltip="Alterar" aria-label="Alterar"><mat-icon svgIcon="edit" /></button>
                  <button mat-icon-button (click)="removeRoute(route)" matTooltip="Excluir" aria-label="Excluir"><mat-icon svgIcon="delete" /></button>
                }
              </li>
            }
          </ul>
        } @else if (!draft()) {
          <p class="muted pad">Nenhuma rota. Sem ela, um pacote vai a outro ambiente baixando o arquivo e importando-o lá.</p>
        }
      }
    </section>

    <section class="panel">
      <div class="panel-head">
        <h2>Origens autorizadas</h2>
        <span class="hint">os ambientes que podem enviar pacotes para {{ data()?.environment }}</span>
        <span class="spacer"></span>
        @if (auth.master()) {
          <button mat-button (click)="addOrigin()" [disabled]="busy()"><mat-icon svgIcon="add" /> Autorizar origem</button>
        }
      </div>
      @if (newKey(); as given) {
        <div class="key">
          <mat-icon svgIcon="key" />
          <div>
            <strong>Chave de {{ given.name }}: copie agora, ela não é mostrada de novo.</strong>
            <span class="muted">Em {{ given.name }}, cadastre uma rota para este ambiente com esta chave. Quem tiver a chave pode enviar pacotes para cá (que continuam precisando de aprovação aqui).</span>
            <code>{{ given.key }}</code>
          </div>
          <button mat-flat-button (click)="copy(given.key)"><mat-icon svgIcon="content_copy" /> Copiar</button>
          <button mat-icon-button (click)="newKey.set(null)" matTooltip="Fechar" aria-label="Fechar"><mat-icon svgIcon="close" /></button>
        </div>
      }
      @if (data(); as now) {
        @if (now.origins.length) {
          <ul class="rows">
            @for (origin of now.origins; track origin.name) {
              <li>
                <div class="who">
                  <strong>{{ origin.name }}</strong>
                  <span class="muted">autorizada por {{ origin.createdBy }} {{ origin.createdAt | when }}{{ origin.lastSeenAt ? ' · último contato ' : '' }}{{ origin.lastSeenAt ? (origin.lastSeenAt | when) : '' }}</span>
                </div>
                <span class="spacer"></span>
                @if (auth.master()) {
                  <button mat-icon-button (click)="removeOrigin(origin.name)" matTooltip="Deixar de aceitar pacotes desta origem" aria-label="Excluir"><mat-icon svgIcon="delete" /></button>
                }
              </li>
            }
          </ul>
        } @else {
          <p class="muted pad">Nenhuma. Pacotes de outros ambientes só chegam importando o arquivo.</p>
        }
      }
      <p class="muted note">
        Cada pedido entre os ambientes é assinado com a chave da rota (HMAC-SHA256) e recusado se a assinatura não confere, se veio de origem não autorizada,
        se passou de 5 minutos ou se já foi recebido. O pacote chega inteiro, conferido pelo SHA-256, e fica pendente até ser aprovado aqui. As chaves ficam protegidas pela máquina.
      </p>
    </section>
  `,
  styles: `
    .spacer { flex: 1; }
    .pad { padding: 4px 20px 16px; margin: 0; }
    .note { padding: 0 20px 16px; margin: 0; font: var(--mat-sys-body-small); max-width: 100ch; }
    .rows { list-style: none; margin: 0; padding: 4px 0 8px; }
    .rows li { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 10px 20px; border-top: 1px solid var(--zap-border); }
    .who { display: grid; gap: 2px; min-width: 0; }
    .url { overflow-wrap: anywhere; }
    .tags { display: flex; gap: 6px; flex-wrap: wrap; }
    .draft { display: grid; grid-template-columns: minmax(120px, 200px) minmax(0, 1fr); gap: 10px 14px; padding: 8px 20px 16px; }
    .draft label { display: grid; gap: 4px; font: var(--mat-sys-label-medium); color: var(--mat-sys-on-surface-variant); }
    .draft .wide { grid-column: span 2; }
    .draft .check { grid-column: span 2; display: flex; align-items: center; gap: 8px; }
    .draft input:not([type=checkbox]) {
      height: 40px; min-width: 0; padding: 0 12px; border: 1px solid var(--zap-border); border-radius: 10px;
      background: var(--zap-surface); color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium);
    }
    .draft .buttons { grid-column: span 2; display: flex; justify-content: flex-end; gap: 8px; }
    .key { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 4px 20px 12px; padding: 12px 16px; border-radius: 14px; background: var(--zap-warn-container); color: var(--zap-warn); }
    .key > div { display: grid; gap: 4px; flex: 1 1 260px; min-width: 0; }
    .key code { padding: 6px 8px; border-radius: 8px; background: var(--zap-surface); color: var(--mat-sys-on-surface); }
    @media (max-width: 600px) { .draft { grid-template-columns: minmax(0, 1fr); } .draft .wide, .draft .check, .draft .buttons { grid-column: auto; } }
  `,
})
export class TransportRoutesPanel implements OnInit {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  protected readonly auth = inject(Auth);

  protected readonly data = signal<TransportRoutes | null>(null);
  protected readonly draft = signal<Draft | null>(null);
  protected readonly newKey = signal<{ name: string; key: string } | null>(null);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.api.transportRoutes().subscribe({ next: (data) => this.data.set(data), error: () => this.data.set(null) });
  }

  private problem(failure: unknown, fallback: string): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? fallback, 'Fechar', { duration: 9000 });
  }

  protected edit(route: TransportRoute | null): void {
    this.draft.set(route ? { id: route.id, name: route.name, url: route.url, key: '', allowHttp: route.allowHttp } : { id: null, name: '', url: '', key: '', allowHttp: false });
  }

  protected async saveRoute(): Promise<void> {
    const draft = this.draft();
    if (!draft) {
      return;
    }
    this.busy.set(true);
    try {
      const body = { name: draft.name.trim(), url: draft.url.trim(), key: draft.key.trim(), allowHttp: draft.allowHttp };
      await firstValueFrom(draft.id ? this.api.changeTransportRoute(draft.id, body) : this.api.addTransportRoute(body));
      this.draft.set(null);
      this.snack.open('Rota salva. Use Testar para conferir o endereço e a chave.', undefined, { duration: 4000 });
      this.load();
    } catch (failure) {
      this.problem(failure, 'A rota não foi salva');
    } finally {
      this.busy.set(false);
    }
  }

  protected async test(route: TransportRoute): Promise<void> {
    this.busy.set(true);
    try {
      const answer = await firstValueFrom(this.api.testTransportRoute(route.id));
      this.snack.open(`${route.name} respondeu: ambiente ${answer.environment}, com a assinatura aceita.`, undefined, { duration: 5000 });
    } catch (failure) {
      this.problem(failure, `${route.name} não respondeu`);
    } finally {
      this.busy.set(false);
      this.load();
    }
  }

  protected async removeRoute(route: TransportRoute): Promise<void> {
    if (!(await confirm(this.dialog, { title: `Excluir a rota ${route.name}?`, message: 'Os pacotes já entregues continuam como estão. Para voltar a enviar, será preciso uma chave nova do destino.', action: 'Excluir', danger: true }))) {
      return;
    }
    try {
      await firstValueFrom(this.api.removeTransportRoute(route.id));
      this.load();
    } catch (failure) {
      this.problem(failure, 'A rota não foi excluída');
    }
  }

  protected async addOrigin(): Promise<void> {
    const name = (await ask(this.dialog, {
      title: 'Autorizar uma origem',
      message: 'O ambiente que poderá enviar pacotes para cá. Uma chave nova é gerada e mostrada uma vez, para ser cadastrada na rota de lá.',
      label: 'Nome do ambiente (por exemplo DEV)',
      action: 'Autorizar',
    }))?.trim();
    if (!name) {
      return;
    }
    this.busy.set(true);
    try {
      const answer = await firstValueFrom(this.api.addTransportOrigin(name));
      this.newKey.set({ name: answer.origin.name, key: answer.key });
      this.load();
    } catch (failure) {
      this.problem(failure, 'A origem não foi autorizada');
    } finally {
      this.busy.set(false);
    }
  }

  protected async removeOrigin(name: string): Promise<void> {
    if (!(await confirm(this.dialog, { title: `Deixar de aceitar pacotes de ${name}?`, message: 'A chave dada a ela deixa de valer na hora. Os pacotes que já chegaram ficam aqui.', action: 'Excluir', danger: true }))) {
      return;
    }
    try {
      await firstValueFrom(this.api.removeTransportOrigin(name));
      this.load();
    } catch (failure) {
      this.problem(failure, 'A origem não foi excluída');
    }
  }

  protected async copy(key: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(key);
      this.snack.open('Chave copiada', undefined, { duration: 2000 });
    } catch {
      this.snack.open('Selecione a chave e copie com Ctrl+C', undefined, { duration: 4000 });
    }
  }
}
