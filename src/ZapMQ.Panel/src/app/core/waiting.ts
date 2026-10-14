import { HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { finalize } from 'rxjs';

/** After how long a request that has no answer yet is said to be waited for. Most answer sooner, and say nothing. */
const QUIET = 450;

/** What is answered by the Worker Control, through the ZapMQ, and may take longer. */
const WORKER_CONTROL = /api\/(workers|transport\/(targets|settings|files?|applications|area\/(objects|running|incoming))|transport\/packages\/[^/]+\/(check|items|revert))/;

/**
 * What the panel is waiting for an answer of, so that a screen that is being read again, or a
 * button that was pressed, does not look as if nothing was happening.
 */
@Injectable({ providedIn: 'root' })
export class Waiting {
  private readonly pending = signal<ReadonlyMap<number, string>>(new Map());
  private next = 0;

  /** Who is taking long to answer: the Worker Control, the ZapMQ, or nobody. */
  readonly who = computed(() => {
    const waited = [...this.pending().values()];
    return waited.includes('Worker Control') ? 'Worker Control' : waited.length ? 'ZapMQ' : '';
  });

  /** Counts a request from now; what is given back takes it out. */
  begin(url: string): () => void {
    const id = this.next++;
    const timer = setTimeout(() => this.pending.update((all) => new Map(all).set(id, WORKER_CONTROL.test(url) ? 'Worker Control' : 'ZapMQ')), QUIET);
    return () => {
      clearTimeout(timer);
      if (this.pending().has(id)) {
        this.pending.update((all) => {
          const left = new Map(all);
          left.delete(id);
          return left;
        });
      }
    };
  }
}

export const waitingInterceptor: HttpInterceptorFn = (request, next) => {
  const done = inject(Waiting).begin(request.url);
  return next(request).pipe(finalize(done));
};
