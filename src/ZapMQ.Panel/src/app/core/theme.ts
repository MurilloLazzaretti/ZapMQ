import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type ThemeChoice = 'auto' | 'light' | 'dark';

const KEY = 'zapmq.theme';

/** Light, dark, or whatever the system says. The choice stays in the browser. */
@Injectable({ providedIn: 'root' })
export class Theme {
  private readonly document = inject(DOCUMENT);
  private readonly media = this.document.defaultView?.matchMedia('(prefers-color-scheme: dark)');

  readonly choice = signal<ThemeChoice>(this.read());
  /** What is actually on screen, for whoever draws with colours of its own (the charts). */
  readonly dark = signal(false);

  constructor() {
    this.apply();
    this.media?.addEventListener('change', () => this.apply());
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
    try {
      localStorage.setItem(KEY, choice);
    } catch {
      // Private mode: the choice lasts for this visit.
    }
    this.apply();
  }

  /** Light → dark → system. */
  cycle(): void {
    this.set(this.choice() === 'light' ? 'dark' : this.choice() === 'dark' ? 'auto' : 'light');
  }

  private read(): ThemeChoice {
    try {
      const stored = localStorage.getItem(KEY);
      return stored === 'light' || stored === 'dark' ? stored : 'auto';
    } catch {
      return 'auto';
    }
  }

  private apply(): void {
    const root = this.document.documentElement;
    const choice = this.choice();
    root.classList.toggle('theme-light', choice === 'light');
    root.classList.toggle('theme-dark', choice === 'dark');
    this.dark.set(choice === 'dark' || (choice === 'auto' && !!this.media?.matches));
  }
}
