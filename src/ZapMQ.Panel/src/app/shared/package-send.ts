import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { TransportRoute, TransportSending } from '../core/models';

/**
 * Sends a package to another environment along one of the routes, and follows it until the other
 * side says it has the whole of it.
 */
@Component({
  selector: 'zap-package-send',
  imports: [FormsModule, MatButtonModule, MatIconModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel send">
      <mat-icon svgIcon="send" />
      @if (routes().length) {
        <div class="what">
          <strong>Enviar para outro ambiente</strong>
          <span class="muted">Lá ele chega como pendente e só é aplicado com aprovação de quem é de lá.</span>
        </div>
        <span class="spacer"></span>
        <select [(ngModel)]="route" aria-label="Rota">
          @for (item of routes(); track item.id) { <option [value]="item.id">{{ item.name }}</option> }
        </select>
        <button mat-flat-button (click)="send()" [disabled]="sending() || !route"><mat-icon svgIcon="send" /> Enviar</button>
      } @else {
        <div class="what">
          <strong>Enviar para outro ambiente</strong>
          <span class="muted">Nenhuma rota configurada. Cadastre uma em <a routerLink="/transporte/configuracao">Transporte → Configuração</a>, ou baixe o arquivo e importe-o lá.</span>
        </div>
      }
      @if (last(); as now) {
        <div class="progress" [class.failed]="now.status === 'failed'" [class.done]="now.status === 'delivered'">
          @switch (now.status) {
            @case ('sending') {
              <span>Enviando para <strong>{{ now.route }}</strong>: {{ percent(now) }}%</span>
              <span class="bar"><span [style.width.%]="percent(now)"></span></span>
            }
            @case ('delivered') {
              @if (now.note) { <span>Enviado a <strong>{{ now.route }}</strong>, mas {{ now.note }}.</span> }
              @else { <span>Entregue a <strong>{{ now.route }}</strong>, conferido pelo SHA-256. Lá ele aguarda aprovação.</span> }
            }
            @case ('failed') { <span>O envio para <strong>{{ now.route }}</strong> não foi concluído: {{ now.problem }}. Enviar de novo continua de onde parou.</span> }
          }
        </div>
      }
    </section>
  `,
  styles: `
    .send { display: flex; align-items: center; gap: 10px 14px; flex-wrap: wrap; padding: 14px 20px; }
    .send > mat-icon { color: var(--mat-sys-primary); flex: none; }
    .what { display: grid; gap: 2px; flex: 1 1 260px; min-width: 0; }
    .spacer { flex: 1; }
    select { height: 40px; padding: 0 12px; border: 1px solid var(--zap-border); border-radius: 999px; background: var(--zap-surface); color: var(--mat-sys-on-surface); font: var(--mat-sys-body-medium); }
    .progress { flex: 1 1 100%; display: grid; gap: 6px; font: var(--mat-sys-body-medium); }
    .progress.failed { color: var(--zap-danger); }
    .progress.done { color: var(--zap-ok); }
    .bar { height: 6px; border-radius: 3px; background: var(--mat-sys-surface-container-high); overflow: hidden; }
    .bar span { display: block; height: 100%; background: var(--mat-sys-primary); transition: width 300ms; }
  `,
})
export class PackageSend implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);

  readonly packageId = input.required<string>();
  /** The other side has it: the page reads the package again, to show the delivery. */
  readonly delivered = output<void>();

  protected readonly routes = signal<TransportRoute[]>([]);
  protected readonly last = signal<TransportSending | null>(null);
  protected readonly sending = signal(false);
  protected route = '';
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.api.transportRoutes().subscribe({
      next: (data) => {
        this.routes.set(data.routes);
        this.route = data.routes[0]?.id ?? '';
      },
    });
    this.follow();
  }

  ngOnDestroy(): void {
    this.stop();
  }

  protected percent(now: TransportSending): number {
    return now.total ? Math.floor((now.sent / now.total) * 100) : 0;
  }

  protected async send(): Promise<void> {
    this.sending.set(true);
    try {
      this.last.set(await firstValueFrom(this.api.sendPackage(this.packageId(), this.route)));
      this.stop();
      this.timer = setInterval(() => this.follow(), 1000);
    } catch (failure) {
      this.sending.set(false);
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O envio não começou', 'Fechar', { duration: 9000 });
    }
  }

  private follow(): void {
    this.api.packageSendings(this.packageId()).subscribe({
      next: (sendings) => {
        const now = sendings[0] ?? null;
        const was = this.last()?.status;
        this.last.set(now);
        const running = now?.status === 'sending';
        this.sending.set(running);
        if (!running) {
          this.stop();
          if (was === 'sending' && now?.status === 'delivered') {
            this.delivered.emit();
          }
        }
      },
    });
  }

  private stop(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
