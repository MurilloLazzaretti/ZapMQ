import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Api } from '../core/api';
import { AgoPipe, NumPipe, WhenPipe } from '../core/format';
import { TrafficScreen, WebApplication, WebModule, WebPublication } from '../core/models';
import { Stat } from '../shared/stat';

const STATES: Record<WebModule['State'], { label: string; tone: string }> = {
  Up: { label: 'no ar', tone: 'ok' },
  Down: { label: 'fora do ar', tone: 'danger' },
  Incomplete: { label: 'incompleto', tone: 'danger' },
};

const megabytes = (bytes: number) => Math.round((bytes / 1048576) * 10) / 10;

/** Everything one module says about itself, and what was seen of it. */
@Component({
  imports: [MatDialogModule, MatButtonModule, MatIconModule, WhenPipe, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.module.Title || data.module.Name }}</h2>
    <mat-dialog-content>
      <dl>
        <div><dt>Módulo</dt><dd class="mono">{{ data.module.Name }}</dd></div>
        <div><dt>Build</dt><dd>{{ data.module.Build ?? '—' }}</dd></div>
        <div><dt>Publicado em</dt><dd>{{ data.module.PublishedAt | when }}</dd></div>
        <div><dt>Arquivos</dt><dd>{{ data.module.Files | num }} · {{ size }} MB</dd></div>
      </dl>
      <p class="muted mono entry">{{ data.module.Entry }}</p>
      @if (data.module.Problem) {
        <p class="problem"><mat-icon svgIcon="error" /> {{ data.module.Problem }}</p>
      }

      <h3>Versões</h3>
      @for (version of data.module.Versions; track $index) {
        <div class="version">
          <strong>{{ version.Version || 'sem número' }}</strong>
          <span class="muted">{{ version.Date }}</span>
          <ul>
            @for (description of version.Descriptions; track $index) {
              <li>{{ description }}</li>
            }
          </ul>
        </div>
      } @empty {
        <p class="muted">O módulo não informa versões.</p>
      }

      @if (data.pages.length) {
        <h3>Telas usadas nas últimas 24 horas</h3>
        <ul class="seen">
          @for (page of data.pages; track page.Page) {
            <li><span class="mono">{{ page.Page }}</span> <span class="muted">{{ page.Users | num }} {{ page.Users === 1 ? 'pessoa' : 'pessoas' }}</span></li>
          }
        </ul>
      }

      @if (data.publications.length) {
        <h3>Publicações vistas</h3>
        <ul class="seen">
          @for (publication of data.publications; track publication.At) {
            <li><span class="num">{{ publication.At | when }}</span> <span class="muted">{{ data.describe(publication) }}</span></li>
          }
        </ul>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Fechar</button>
    </mat-dialog-actions>
  `,
  styles: `
    dl { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin: 0; }
    dl div { padding: 10px 12px; border-radius: 12px; background: var(--mat-sys-surface-container-low); min-width: 0; }
    dt { color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-label-small); }
    dd { margin: 2px 0 0; font: var(--mat-sys-title-small); overflow-wrap: anywhere; }
    .entry { margin: 10px 0 0; overflow-wrap: anywhere; }
    .problem { display: flex; gap: 8px; align-items: center; margin: 12px 0 0; padding: 10px 12px; border-radius: 12px; background: var(--zap-danger-container); color: var(--zap-danger); }
    h3 { margin: 18px 0 8px; color: var(--mat-sys-on-surface-variant); font: var(--mat-sys-label-medium); letter-spacing: 0.04em; text-transform: uppercase; }
    .version { display: grid; grid-template-columns: auto 1fr; gap: 2px 10px; align-items: baseline; padding: 8px 0; border-top: 1px solid var(--zap-border); }
    .version ul { grid-column: 1 / -1; margin: 2px 0 0; padding-left: 18px; }
    .seen { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
    .seen li { display: flex; gap: 10px; flex-wrap: wrap; }
  `,
})
export class WebModuleDialog {
  protected readonly data = inject<{ module: WebModule; pages: TrafficScreen[]; publications: WebPublication[]; describe: (publication: WebPublication) => string }>(MAT_DIALOG_DATA);
  protected readonly size = megabytes(this.data.module.Bytes);
}

/**
 * The web application served from the machine of the Worker Control, module by module:
 * which version each one is in, whether it answers, and when it was published.
 */
@Component({
  selector: 'zap-web',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatIconModule, MatTooltipModule, Stat, NumPipe, AgoPipe, WhenPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './web.html',
  styleUrl: './web.scss',
})
export class WebPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly applications = signal<WebApplication[] | null>(null);
  protected readonly publications = signal<WebPublication[]>([]);
  protected readonly problem = signal('');
  protected readonly outdated = signal(false);
  protected readonly chosen = signal('');
  protected readonly filter = signal('');
  protected readonly order = signal<'name' | 'published' | 'use'>('name');
  /** How much the screens of each module were used in the last day, by module. Empty while it is not known. */
  protected readonly usage = signal<ReadonlyMap<string, { Count: number; Users: number }>>(new Map());
  private readonly pages = signal<TrafficScreen[]>([]);

  protected readonly application = computed(() => {
    const all = this.applications() ?? [];
    return all.find((application) => application.Name === this.chosen()) ?? all[0] ?? null;
  });

  protected readonly modules = computed(() => {
    const wanted = this.filter().trim().toLowerCase();
    const modules = (this.application()?.Modules ?? []).filter((module) => !wanted || `${module.Name} ${module.Title ?? ''}`.toLowerCase().includes(wanted));
    const usage = this.usage();
    return this.order() === 'published'
      ? [...modules].sort((a, b) => (b.PublishedAt ?? '').localeCompare(a.PublishedAt ?? ''))
      : this.order() === 'use'
        ? [...modules].sort((a, b) => (usage.get(b.Name)?.Users ?? 0) - (usage.get(a.Name)?.Users ?? 0) || (usage.get(b.Name)?.Count ?? 0) - (usage.get(a.Name)?.Count ?? 0))
        : [...modules].sort((a, b) => (a.Title || a.Name).localeCompare(b.Title || b.Name, 'pt-BR'));
  });

  /** The publications of the application on screen, the newest first. */
  protected readonly timeline = computed(() => this.publications().filter((publication) => publication.App === this.application()?.Name));

  protected readonly totals = computed(() => {
    const application = this.application();
    const modules = application?.Modules ?? [];
    const week = Date.now() - 7 * 86400000;
    const latest = modules.map((module) => module.PublishedAt).filter((at): at is string => !!at).sort().pop() ?? null;
    return {
      all: modules.length,
      up: modules.filter((module) => module.State === 'Up').length,
      attention: modules.filter((module) => module.State !== 'Up').length + (application?.Orphans.length ?? 0),
      published: this.timeline().filter((publication) => publication.Kind !== 'first-seen' && Date.parse(publication.At) >= week).length,
      latest,
      mostUsed: modules.map((module) => ({ module, use: this.usage().get(module.Name) })).filter((item) => (item.use?.Users ?? 0) > 0)
        .sort((a, b) => b.use!.Users - a.use!.Users || b.use!.Count - a.use!.Count)[0] ?? null,
      unchecked: modules.length > 0 && modules.every((module) => module.Online === null),
    };
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 15000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  protected state(module: WebModule) {
    return STATES[module.State];
  }

  protected megabytes(bytes: number): number {
    return megabytes(bytes);
  }

  /** What is wrong with a module, in words; empty when nothing is. */
  protected why(module: WebModule): string {
    if (module.State === 'Incomplete') {
      return 'Está no manifesto, mas o arquivo de entrada não está no disco.';
    }
    if (module.State === 'Down') {
      return module.Status ? `O servidor web respondeu ${module.Status} ao pedir o arquivo de entrada.` : 'O servidor web não respondeu ao pedir o arquivo de entrada.';
    }
    return '';
  }

  /** The version the module is in, in a few words. */
  protected version(module: WebModule): string {
    const current = module.Versions[0]?.Version;
    return current ? 'v' + current : 'sem versão informada';
  }

  protected readonly describe = (publication: WebPublication): string => {
    if (publication.Kind === 'first-seen') {
      return `${publication.Count} ${publication.Count === 1 ? 'módulo visto' : 'módulos vistos'} pela primeira vez`;
    }
    if (publication.Kind === 'removed') {
      return 'saiu do manifesto';
    }
    const parts: string[] = [publication.Kind === 'new' ? 'módulo novo' : 'publicado'];
    if (publication.ToVersion) {
      parts.push(publication.FromVersion && publication.FromVersion !== publication.ToVersion ? `versão ${publication.FromVersion} → ${publication.ToVersion}` : `versão ${publication.ToVersion}`);
    }
    if (publication.ToBuild !== null) {
      parts.push(publication.FromBuild !== null && publication.FromBuild !== publication.ToBuild ? `build ${publication.FromBuild} → ${publication.ToBuild}` : `build ${publication.ToBuild}`);
    }
    return parts.join(' · ');
  };

  protected subject(publication: WebPublication): string {
    if (publication.Module === '*') {
      return 'Aplicação';
    }
    if (publication.Module === '(shell)') {
      return 'Aplicação (casca)';
    }
    const module = this.application()?.Modules.find((item) => item.Name === publication.Module);
    return module?.Title || publication.Module;
  }

  protected open(module: WebModule): void {
    this.dialog.open(WebModuleDialog, {
      data: { module: { ...module, Problem: this.why(module) || null }, pages: this.pages().filter((page) => (page.Page + '/').includes('/' + module.Name + '/')), publications: this.timeline().filter((publication) => publication.Module === module.Name), describe: this.describe },
      maxWidth: '620px',
      width: 'calc(100vw - 32px)',
    });
  }

  /** Asks the traffic how much the screens of each module were used. Without traffic, the cards go without it. */
  private measure(names: string[]): void {
    if (!names.length) {
      return;
    }
    this.api.trafficPages({ minutes: 1440, limit: 200, names: [...new Set(names)] }).subscribe({
      next: (answer) => {
        this.usage.set(new Map(answer.Named.map((item) => [item.Name, item])));
        this.pages.set(answer.Pages);
      },
      error: () => undefined,
    });
  }

  private refresh(): void {
    this.api.frontends().subscribe({
      next: (answer) => {
        this.applications.set(answer.Frontends);
        this.publications.set(answer.Publications);
        this.problem.set('');
        this.outdated.set(false);
        this.measure(answer.Frontends.flatMap((application) => application.Modules.map((module) => module.Name)));
      },
      error: (failure: HttpErrorResponse) => {
        if (failure.status === 501) {
          this.outdated.set(true);
          this.problem.set('');
        } else if (failure.status !== 401) {
          this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.');
        }
      },
    });
  }
}
