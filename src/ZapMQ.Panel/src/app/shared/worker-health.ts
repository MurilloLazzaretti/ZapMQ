import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { Api } from '../core/api';
import { HealthSample } from '../core/models';
import { Series, TimeChart } from './chart';

/** How one worker has been doing: processor, memory and how fast it answers the keep-alive. */
@Component({
  imports: [MatDialogModule, MatButtonModule, TimeChart],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.group }} · pid {{ data.pid }}</h2>
    <mat-dialog-content>
      @if (samples().length > 1) {
        <h3>Processador <span class="muted">% de todos os núcleos</span></h3>
        <zap-time-chart [series]="cpu()" unit="%" />
        <h3>Memória <span class="muted">MB</span></h3>
        <zap-time-chart [series]="memory()" unit=" MB" />
        @if (keepAlive()[0].data.length) {
          <h3>Resposta do keep-alive <span class="muted">ms</span></h3>
          <zap-time-chart [series]="keepAlive()" unit=" ms" />
        }
      } @else {
        <p class="muted">Ainda não há medições suficientes deste processo. Elas são feitas a cada 30 segundos.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Fechar</button>
    </mat-dialog-actions>
  `,
  styles: `
    :host { --zap-chart-height: 170px; }
    h3 { margin: 12px 0 0; font: var(--mat-sys-title-small); }
    h3 .muted { font: var(--mat-sys-body-small); margin-left: 6px; }
  `,
})
export class WorkerHealthDialog implements OnInit, OnDestroy {
  protected readonly data = inject<{ group: string; pid: number }>(MAT_DIALOG_DATA);
  private readonly api = inject(Api);
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly samples = signal<HealthSample[]>([]);

  protected readonly cpu = computed<Series[]>(() => [
    { name: 'Processador', color: '#6d5efc', area: true, data: this.points((sample) => sample.CpuPercent) },
  ]);

  protected readonly memory = computed<Series[]>(() => [
    { name: 'Memória', color: '#22c1dc', area: true, data: this.points((sample) => (sample.MemoryBytes === null ? null : Math.round((sample.MemoryBytes / 1048576) * 10) / 10)) },
  ]);

  protected readonly keepAlive = computed<Series[]>(() => [
    { name: 'Keep-alive', color: '#2fb380', data: this.points((sample) => (sample.KeepAliveMs === null ? null : Math.round(sample.KeepAliveMs))) },
  ]);

  ngOnInit(): void {
    this.load();
    this.timer = setInterval(() => this.load(), 15000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  private points(value: (sample: HealthSample) => number | null): [number, number][] {
    return this.samples()
      .map((sample) => [new Date(sample.At).getTime(), value(sample)] as [number, number | null])
      .filter((point): point is [number, number] => point[1] !== null);
  }

  private load(): void {
    this.api.workerHealth(this.data.pid).subscribe({ next: (answer) => this.samples.set(answer.Samples), error: () => undefined });
  }
}
