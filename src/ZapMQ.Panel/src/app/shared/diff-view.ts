import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DiffLine, around } from '../core/diff';
import { NumPipe } from '../core/format';

/**
 * Two versions of a text line by line: what was taken out, what was put in, and how much
 * stayed the same in between. Without a comparison to make, it shows the lines as they are.
 */
@Component({
  selector: 'zap-diff',
  imports: [NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="diff" role="table" aria-label="Comparação linha a linha">
      @for (part of parts(); track $index) {
        @if (isLines(part)) {
          @for (line of $any(part).lines; track $index) {
            <div class="line" [class.plus]="compared() && line.kind === '+'" [class.minus]="compared() && line.kind === '-'">
              <span class="number">{{ line.before ?? '' }}</span><span class="number">{{ line.after ?? '' }}</span><span class="sign">{{ compared() ? line.kind : '' }}</span><span class="text">{{ line.text }}</span>
            </div>
          }
        } @else {
          <div class="gap">{{ $any(part).skipped | num }} {{ $any(part).skipped === 1 ? 'linha igual' : 'linhas iguais' }}</div>
        }
      }
    </div>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .diff {
      max-height: var(--zap-diff-height, 75vh);
      overflow: auto;
      padding: 8px 0 12px;
      font-family: var(--zap-mono);
      font-size: 12.5px;
      line-height: 1.55;
    }
    .line { display: flex; min-width: max-content; white-space: pre; }
    .line .number { flex: none; width: 4.5ch; padding-right: 1ch; text-align: right; color: var(--mat-sys-on-surface-variant); opacity: 0.6; user-select: none; }
    .line .sign { flex: none; width: 2ch; text-align: center; user-select: none; }
    .line .text { padding-right: 20px; color: var(--mat-sys-on-surface); }
    .line.plus { background: color-mix(in srgb, var(--zap-ok) 14%, transparent); }
    .line.minus { background: color-mix(in srgb, var(--zap-danger) 13%, transparent); }
    .gap {
      margin: 4px 0;
      padding: 2px 20px;
      background: var(--mat-sys-surface-container-high);
      color: var(--mat-sys-on-surface-variant);
      font-family: var(--mat-sys-body-small-font, inherit);
      font-size: 12px;
    }
  `,
})
export class DiffView {
  readonly lines = input.required<DiffLine[]>();
  /** There are two sides: the signs and the colours mean something. */
  readonly compared = input(true);
  /** Every line, or only the ones that changed with a few around them. */
  readonly whole = input(false);

  protected readonly parts = computed(() => (this.whole() || !this.compared() ? [{ lines: this.lines() }] : around(this.lines())));

  protected isLines(part: object): boolean {
    return 'lines' in part;
  }
}
