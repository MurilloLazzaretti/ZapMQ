import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgxEchartsDirective, provideEchartsCore } from 'ngx-echarts';
import { BarChart, LineChart } from 'echarts/charts';
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import type { EChartsCoreOption } from 'echarts/core';
import { Theme } from '../core/theme';

echarts.use([LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer]);

export const provideCharts = () => provideEchartsCore({ echarts });

export interface Series {
  name: string;
  /** [time in ms, value] */
  data: [number, number][];
  color: string;
  area?: boolean;
}

const time = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });
const timeFull = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const value = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });

/** A line chart over time, in the colours of the theme in use. */
@Component({
  selector: 'zap-time-chart',
  imports: [NgxEchartsDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="chart" echarts [options]="options()" [autoResize]="true"></div>`,
  styles: `
    :host { display: block; min-width: 0; }
    .chart { height: var(--zap-chart-height, 260px); width: 100%; }
  `,
})
export class TimeChart {
  private readonly theme = inject(Theme);

  readonly series = input.required<Series[]>();
  readonly unit = input('');

  protected readonly options = computed<EChartsCoreOption>(() => {
    const dark = this.theme.dark();
    const text = dark ? '#c9c5d0' : '#49454f';
    const line = dark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.07)';
    const unit = this.unit();
    // A short stretch of time needs the seconds to tell one label from the next.
    const moments = this.series().flatMap((item) => (item.data.length ? [item.data[0][0], item.data[item.data.length - 1][0]] : []));
    const brief = moments.length > 0 && Math.max(...moments) - Math.min(...moments) < 15 * 60 * 1000;

    return {
      animation: false,
      textStyle: { fontFamily: 'Inter Variable, system-ui, sans-serif' },
      color: this.series().map((item) => item.color),
      grid: { left: 8, right: 16, top: 36, bottom: 8, containLabel: true },
      legend: { top: 0, left: 0, icon: 'roundRect', itemWidth: 10, itemHeight: 10, textStyle: { color: text } },
      tooltip: {
        trigger: 'axis',
        backgroundColor: dark ? '#2b2930' : '#ffffff',
        borderColor: line,
        textStyle: { color: dark ? '#e6e0e9' : '#1d1b20', fontSize: 12 },
        axisPointer: { lineStyle: { color: line } },
        formatter: (points: { axisValue: number; marker: string; seriesName: string; value: [number, number] }[]) =>
          `<b>${timeFull.format(new Date(points[0].axisValue))}</b><br>` +
          points.map((point) => `${point.marker} ${point.seriesName}: <b>${value.format(point.value[1])}</b>${unit}`).join('<br>'),
      },
      xAxis: {
        type: 'time',
        axisLine: { lineStyle: { color: line } },
        axisTick: { show: false },
        axisLabel: { color: text, hideOverlap: true, formatter: (at: number) => (brief ? timeFull : time).format(new Date(at)) },
        splitLine: { show: false },
      },
      yAxis: {
        type: 'value',
        minInterval: unit ? undefined : 1,
        axisLabel: { color: text, formatter: (amount: number) => value.format(amount) },
        splitLine: { lineStyle: { color: line } },
      },
      series: this.series().map((item) => ({
        name: item.name,
        type: 'line',
        data: item.data,
        showSymbol: false,
        smooth: 0.25,
        lineStyle: { width: 2 },
        areaStyle: item.area ? { opacity: dark ? 0.18 : 0.12 } : undefined,
        emphasis: { focus: 'series' },
      })),
    };
  });
}
