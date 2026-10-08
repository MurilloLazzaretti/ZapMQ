import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/** One figure of the overview: a label, a number and, if it matters, a tone. */
@Component({
  selector: 'zap-stat',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="icon" [class]="tone()"><mat-icon [svgIcon]="icon()" /></div>
    <div class="text">
      <span class="label">{{ label() }}</span>
      <span class="value num">{{ value() }}<small>{{ unit() }}</small></span>
      @if (hint()) {
        <span class="hint">{{ hint() }}</span>
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      gap: 14px;
      align-items: center;
      padding: 18px 20px;
      background: var(--zap-surface);
      border: 1px solid var(--zap-border);
      border-radius: var(--zap-radius);
      box-shadow: var(--zap-shadow);
      min-width: 0;
    }
    .icon {
      width: 44px;
      height: 44px;
      border-radius: 14px;
      display: grid;
      place-items: center;
      flex: none;
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .icon.ok { background: var(--zap-ok-container); color: var(--zap-ok); }
    .icon.warn { background: var(--zap-warn-container); color: var(--zap-warn); }
    .icon.danger { background: var(--zap-danger-container); color: var(--zap-danger); }
    .icon.info { background: var(--zap-info-container); color: var(--zap-info); }
    .icon.neutral { background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface-variant); }
    .text { display: grid; min-width: 0; }
    .label { font: var(--mat-sys-label-medium); color: var(--mat-sys-on-surface-variant); }
    .value { font: var(--mat-sys-headline-small); font-weight: 650; letter-spacing: -0.02em; line-height: 1.15; }
    .value small { font: var(--mat-sys-label-medium); color: var(--mat-sys-on-surface-variant); margin-left: 4px; letter-spacing: 0; }
    .hint { font: var(--mat-sys-body-small); color: var(--mat-sys-on-surface-variant); }
    @media (max-width: 560px) {
      :host { flex-direction: column; align-items: flex-start; gap: 10px; padding: 14px 16px; }
      .icon { width: 36px; height: 36px; border-radius: 12px; }
      .icon mat-icon { width: 20px; height: 20px; }
      .value { font: var(--mat-sys-title-large); font-weight: 650; }
    }
  `,
})
export class Stat {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly icon = input.required<string>();
  readonly unit = input('');
  readonly hint = input('');
  readonly tone = input<'primary' | 'ok' | 'warn' | 'danger' | 'info' | 'neutral'>('primary');
}
