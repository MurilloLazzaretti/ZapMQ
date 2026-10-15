import { Injectable, signal } from '@angular/core';
import { CatalogObject } from './models';

/**
 * What the list of the objects of a database had when an object was opened from it: the ones
 * picked to be carried and the last one opened, so that going back finds the list as it was.
 * The filters are in the address of the list.
 */
@Injectable({ providedIn: 'root' })
export class ObjectListMemory {
  readonly picked = signal<ReadonlyMap<string, CatalogObject & { database: string }>>(new Map());
  /** Database, kind, schema and name of the last object opened. */
  readonly last = signal('');
}
