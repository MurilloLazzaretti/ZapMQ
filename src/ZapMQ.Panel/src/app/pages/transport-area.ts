import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { confirm } from '../shared/dialogs';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { kindIcon, kindName } from '../core/database';
import { size } from '../core/database';
import { AgoPipe } from '../core/format';
import { AreaItem, PackageDraft } from '../core/models';
import { Transport, itemAction, itemTone, targetIcon, targetName } from '../core/transport';
import { TransportTabs } from '../shared/transport-tabs';

/**
 * An open package: what goes in it, before it is closed as a version. Objects of the database
 * and applications are included from their own screens; a script is written or brought here.
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
  protected readonly transport = inject(Transport);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);

  /** The open package; null once it is not open any more. */
  protected readonly draft = signal<PackageDraft | null | undefined>(undefined);
  protected readonly items = signal<AreaItem[] | null>(null);
  protected readonly environment = signal('');
  /** The database of the environment, to open an object of the area on its own screen. */
  protected readonly database = signal('');
  protected readonly problem = signal('');
  protected readonly busy = signal(false);
  /** The form for a script is open. */
  protected readonly writing = signal(false);

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

  private get id(): string {
    return this.route.snapshot.paramMap.get('id') ?? '';
  }

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

  protected async addScript(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.addScript(this.id, { title: this.title, script: this.script }));
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
      const made = await firstValueFrom(this.api.closePackage({ draft: this.id, name: this.name, description: this.description }));
      if (this.transport.current()?.id === this.id) {
        this.transport.choose(null);
      }
      this.transport.refresh();
      void this.router.navigate(['/transporte/pacotes', made.package.id]);
    } catch (failure) {
      this.say(failure);
    } finally {
      this.busy.set(false);
    }
  }

  /** What is included from the screens of the panel goes in this one. */
  protected includeHere(): void {
    if (this.draft()) {
      this.transport.choose(this.draft()!);
    }
  }

  protected async saveNames(): Promise<void> {
    try {
      this.draft.set(await firstValueFrom(this.api.changeDraft(this.id, { name: this.name, description: this.description })));
      this.snack.open('Nome e descrição guardados', undefined, { duration: 2500 });
    } catch (failure) {
      this.say(failure);
    }
  }

  protected async discard(): Promise<void> {
    const draft = this.draft();
    if (!draft || !(await confirm(this.dialog, { title: `Descartar ${draft.name} v${draft.version}?`, message: 'O pacote aberto e o que foi incluído nele deixam de existir. Os objetos e as aplicações não mudam.', action: 'Descartar', danger: true }))) {
      return;
    }
    await firstValueFrom(this.api.deleteDraft(this.id)).catch(() => undefined);
    if (this.transport.current()?.id === this.id) {
      this.transport.choose(null);
    }
    void this.router.navigate(['/transporte/pacotes']);
  }

  private say(failure: unknown): void {
    this.snack.open((failure as HttpErrorResponse).error?.error ?? 'O serviço não aceitou o pedido', 'Fechar', { duration: 8000 });
  }

  private load(): void {
    this.api.area(this.id).subscribe({
      next: (answer) => {
        if (answer.draft && this.draft() === undefined) {
          this.name = answer.draft.name;
          this.description = answer.draft.description;
        }
        this.draft.set(answer.draft);
        this.items.set(answer.items);
        this.environment.set(answer.environment);
        this.problem.set('');
        this.transport.refresh();
      },
      error: (failure: HttpErrorResponse) => failure.status !== 401 && this.problem.set(failure.error?.error ?? 'Não foi possível falar com o serviço do ZapMQ.'),
    });
  }
}
