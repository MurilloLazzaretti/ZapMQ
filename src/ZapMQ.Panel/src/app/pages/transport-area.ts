import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { kindIcon, kindName } from '../core/database';
import { size } from '../core/database';
import { AgoPipe } from '../core/format';
import { AreaItem, TransportTarget } from '../core/models';
import { TARGET_KINDS, Transport, itemAction, itemTone, targetIcon, targetName } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/**
 * The area of the environment: what is waiting to go into a package. Objects of the database
 * are pointed at from their own screens; a script is written or brought here.
 */
@Component({
  selector: 'zap-transport-area',
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTooltipModule, TransportTabs, AgoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transport-area.html',
  styleUrl: './transport-area.scss',
})
export class TransportAreaPage implements OnInit {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  private readonly transport = inject(Transport);

  protected readonly items = signal<AreaItem[] | null>(null);
  protected readonly environment = signal('');
  /** The database of the environment, to open an object of the area on its own screen. */
  protected readonly database = signal('');
  protected readonly problem = signal('');
  protected readonly busy = signal(false);
  /** The form for a script is open. */
  protected readonly writing = signal(false);

  /** The list of what can be replaced on the machine is open. */
  protected readonly choosing = signal(false);
  protected readonly targets = signal<TransportTarget[] | null>(null);
  protected readonly targetsProblem = signal('');
  /** Where, on the machine, a new version is left to be taken; and what was left there under no known name. */
  protected readonly inbox = signal('');
  protected readonly unmatched = signal<string[]>([]);
  protected readonly targetKinds = TARGET_KINDS;
  protected readonly targetName = targetName;
  protected readonly targetIcon = targetIcon;
  protected readonly size = size;

  protected name = '';
  protected description = '';
  protected title = '';
  protected script = '';

  protected readonly kindName = kindName;
  protected readonly kindIcon = kindIcon;
  protected readonly itemAction = itemAction;
  protected readonly itemTone = itemTone;

  ngOnInit(): void {
    this.api.database().subscribe({ next: (state) => this.database.set(state.Databases[0] ?? ''), error: () => undefined });
    this.load();
  }

  protected async remove(item: AreaItem): Promise<void> {
    await firstValueFrom(this.api.removeFromArea(item.id)).catch(() => undefined);
    this.load();
  }

  /** A .sql file chosen from the machine becomes the text of the script. */
  protected async pick(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) {
      this.script = await file.text();
      this.title ||= file.name.replace(/\.sql$/i, '');
      this.writing.set(true);
    }
    input.value = '';
  }

  protected choose(): void {
    this.choosing.set(!this.choosing());
    if (this.choosing()) {
      this.loadTargets();
    }
  }

  private loadTargets(): void {
    this.api.transportTargets().subscribe({
      next: (answer) => {
        this.targets.set(answer.Targets);
        this.inbox.set(answer.Inbox ?? '');
        this.unmatched.set(answer.Unmatched ?? []);
        this.targetsProblem.set('');
      },
      error: (failure: HttpErrorResponse) =>
        this.targetsProblem.set(failure.status === 501 ? 'O Worker Control instalado é anterior a este recurso (existe a partir da versão 2.11).' : (failure.error?.error ?? 'Não foi possível perguntar ao Worker Control.')),
    });
  }

  protected of(kind: string): TransportTarget[] {
    return (this.targets() ?? []).filter((target) => target.Kind === kind);
  }

  /** What the target is running right now goes into the area. */
  protected async running(target: TransportTarget): Promise<void> {
    await this.place(this.api.addRunning(target.Kind, target.Name), `${target.Name}: o que está rodando foi para a área`);
  }

  /** The new version that was left for the target in the inbox goes into the area, and leaves the inbox. */
  protected async incoming(target: TransportTarget): Promise<void> {
    await this.place(this.api.addIncoming(target.Kind, target.Name), `${target.Name}: a versão nova foi para a área`);
    this.loadTargets();
  }

  private async place(request: ReturnType<Api['addRunning']>, done: string): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(request);
      this.snack.open(done, undefined, { duration: 3000 });
      this.load();
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  protected async addScript(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.addScript({ title: this.title, script: this.script }));
      this.title = '';
      this.script = '';
      this.writing.set(false);
      this.load();
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  protected async close(): Promise<void> {
    this.busy.set(true);
    try {
      const made = await firstValueFrom(this.api.closePackage({ name: this.name, description: this.description }));
      this.transport.refresh();
      void this.router.navigate(['/transporte/pacotes', made.package.id]);
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  private say(failure: unknown): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 8000 });
  }

  private load(): void {
    this.api.area().subscribe({
      next: (answer) => {
        this.items.set(answer.items);
        this.environment.set(answer.environment);
        this.problem.set('');
        this.transport.refresh();
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }
}
