import { Injectable, signal } from '@angular/core';

/**
 * How the comparison screen was left: which environment, and what was being looked at. The
 * comparison itself is kept by the service, for everybody.
 */
@Injectable({ providedIn: 'root' })
export class CompareMemory {
  readonly route = signal('');
  readonly filter = signal<string>('differ');
  readonly kind = signal('');
  readonly search = signal('');
}
