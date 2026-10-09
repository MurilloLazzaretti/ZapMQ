import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { TransportTarget } from '../core/models';
import { TARGET_KINDS, targetIcon, targetName } from '../core/transport';
import { WorkerTabs } from '../shared/worker-tabs';

/**
 * The applications of this machine, by kind: where each one is and, on its own screen, the
 * files of its configuration.
 */
@Component({
  selector: 'zap-apps',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, WorkerTabs],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="head">
        <div>
          <h1>Aplicações</h1>
          <p class="muted">O que roda nesta máquina, com as pastas e os arquivos de configuração de cada uma.</p>
        </div>
        <span class="spacer"></span>
        <a mat-flat-button routerLink="/workers/aplicacoes/nova"><mat-icon svgIcon="add" /> Nova aplicação</a>
        <zap-worker-tabs />
      </header>

      @if (problem()) {
        <section class="panel empty"><mat-icon svgIcon="cloud_off" /><strong>Não foi possível listar as aplicações</strong><span>{{ problem() }}</span></section>
      } @else if (targets(); as all) {
        <section class="panel">
          <div class="tools">
            <label class="search">
              <mat-icon svgIcon="search" />
              <input type="search" placeholder="Buscar por nome ou pasta" [ngModel]="search()" (ngModelChange)="search.set($event)" aria-label="Buscar aplicação" />
            </label>
            <div class="kinds" role="tablist" aria-label="Tipo de aplicação">
              <button type="button" [class.on]="!kind()" (click)="kind.set('')">Todas</button>
              @for (item of kinds; track item) {
                <button type="button" [class.on]="kind() === item" [disabled]="!counts()[item] && kind() !== item" (click)="kind.set(item)">
                  <mat-icon [svgIcon]="targetIcon(item)" /> {{ targetName(item, true) }} <span class="count">{{ counts()[item] }}</span>
                </button>
              }
            </div>
          </div>

          @for (section of sections(); track section.kind) {
            <div class="section">
              <h2><mat-icon [svgIcon]="targetIcon(section.kind)" /> {{ targetName(section.kind, true) }} <span class="muted">{{ section.items.length }}</span></h2>
              <ul>
                @for (target of section.items; track target.Name) {
                  <li>
                    <a [routerLink]="['/workers/aplicacoes', target.Kind, target.Name]">
                      <span class="who">
                        <strong>{{ target.Name }}</strong>
                        @if (target.Version) { <span class="pill">{{ target.Version }}</span> }
                        @if (target.Problem) { <span class="pill danger" [matTooltip]="target.Problem">sem pasta conhecida</span> }
                      </span>
                      <span class="muted mono path">{{ target.Paths[0] }}@if (target.Paths.length > 1) { <span> (+{{ target.Paths.length - 1 }})</span> }</span>
                      <mat-icon class="go" svgIcon="chevron_right" />
                    </a>
                  </li>
                }
              </ul>
            </div>
          } @empty {
            <div class="empty"><mat-icon svgIcon="search" /><strong>{{ all.length ? 'Nenhuma aplicação com esse filtro' : 'O Worker Control não conhece nenhuma aplicação nesta máquina' }}</strong></div>
          }
        </section>
      } @else {
        <section class="panel empty"><span>Perguntando ao Worker Control o que há nesta máquina…</span></section>
      }
    </div>
  `,
  styleUrl: './transport-apps.scss',
  styles: `
    .section li { display: block; padding: 0; }
    .section li a {
      display: grid; grid-template-columns: minmax(160px, 1fr) minmax(0, 1.6fr) auto; align-items: center; gap: 4px 14px;
      padding: 10px 12px 10px 20px; color: inherit; text-decoration: none;
    }
    .section li a:hover strong { text-decoration: underline; }
    .go { color: var(--mat-sys-on-surface-variant); }
    @media (max-width: 900px) { .section li a { grid-template-columns: minmax(0, 1fr) auto; } }
  `,
})
export class AppsPage implements OnInit {
  private readonly api = inject(Api);

  protected readonly targets = signal<TransportTarget[] | null>(null);
  protected readonly problem = signal('');
  protected readonly search = signal('');
  /** The kind being looked at; empty for all of them. */
  protected readonly kind = signal('');

  protected readonly kinds = TARGET_KINDS;
  protected readonly targetName = targetName;
  protected readonly targetIcon = targetIcon;

  private readonly matching = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    return (this.targets() ?? []).filter((target) => !wanted || target.Name.toLowerCase().includes(wanted) || target.Paths.some((path) => path.toLowerCase().includes(wanted)));
  });

  protected readonly counts = computed(() => Object.fromEntries(TARGET_KINDS.map((kind) => [kind, this.matching().filter((target) => target.Kind === kind).length])));

  protected readonly sections = computed(() =>
    TARGET_KINDS.filter((kind) => !this.kind() || this.kind() === kind)
      .map((kind) => ({ kind, items: this.matching().filter((target) => target.Kind === kind) }))
      .filter((section) => section.items.length),
  );

  ngOnInit(): void {
    this.api.transportTargets().subscribe({
      next: (answer) => {
        this.targets.set(answer.Targets);
        this.problem.set('');
      },
      error: (failure: HttpErrorResponse) =>
        failure.status !== 401 && this.problem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.11).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }
}
