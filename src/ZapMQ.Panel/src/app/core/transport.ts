import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { Api } from './api';
import { PackageStatus, TransportSummary } from './models';

const STATUS: Record<PackageStatus, { text: string; tone: string }> = {
  Closed: { text: 'fechado', tone: 'info' },
  Pending: { text: 'aguardando aprovação', tone: 'warn' },
  Rejected: { text: 'recusado', tone: '' },
  Approved: { text: 'aprovado', tone: 'primary' },
  Applying: { text: 'aplicando', tone: 'primary' },
  Applied: { text: 'aplicado', tone: 'ok' },
  Partial: { text: 'aplicado em parte', tone: 'danger' },
  Failed: { text: 'falhou', tone: 'danger' },
};

export function statusName(status: PackageStatus): string {
  return STATUS[status]?.text ?? status;
}

export function statusTone(status: PackageStatus): string {
  return STATUS[status]?.tone ?? '';
}

const ACTIONS: Record<string, { text: string; tone: string }> = {
  Define: { text: 'levar', tone: 'info' },
  Drop: { text: 'apagar', tone: 'danger' },
  Script: { text: 'script', tone: 'primary' },
};

export function itemAction(action: string): string {
  return ACTIONS[action]?.text ?? action;
}

export function itemTone(action: string): string {
  return ACTIONS[action]?.tone ?? '';
}

/** What an entry of the history of a package says, in the words of the panel. */
export function historyText(what: string): string {
  return (
    {
      closed: 'Fechado',
      downloaded: 'Baixado como arquivo',
      received: 'Recebido',
      approved: 'Aprovado',
      cancelled: 'Aprovação cancelada',
      rejected: 'Recusado',
      applying: 'Aplicação iniciada',
      applied: 'Aplicado',
      partial: 'Aplicado em parte',
      failed: 'Falhou',
      interrupted: 'Aplicação interrompida',
    } as Record<string, string>
  )[what] ?? what;
}

/**
 * What every screen needs of the transport: how much is waiting, and the one way of putting
 * an object of the database in the area of the environment.
 */
@Injectable({ providedIn: 'root' })
export class Transport {
  private readonly api = inject(Api);
  private readonly snack = inject(MatSnackBar);

  readonly summary = signal<TransportSummary | null>(null);
  /** What asks for somebody: packages waiting to be approved and the ones that went wrong. */
  readonly waiting = signal(0);

  constructor() {
    this.refresh();
    setInterval(() => this.refresh(), 30000);
  }

  refresh(): void {
    this.api.transportSummary().subscribe({
      next: (summary) => {
        this.summary.set(summary);
        this.waiting.set(summary.pending + summary.troubled);
      },
      error: () => undefined,
    });
  }

  /** Puts an object in the area and says so; true when it went in. */
  async add(item: { database?: string; kind: string; schema: string; name: string; drop?: boolean }): Promise<boolean> {
    try {
      await firstValueFrom(this.api.addToArea(item));
      this.snack.open(`${item.schema}.${item.name} está na área de transporte${item.drop ? ', como exclusão' : ''}`, undefined, { duration: 3000 });
      this.refresh();
      return true;
    } catch (failure) {
      this.snack.open((failure as HttpErrorResponse).error?.error ?? 'Não foi possível incluir na área de transporte', 'Fechar', { duration: 7000 });
      return false;
    }
  }
}
