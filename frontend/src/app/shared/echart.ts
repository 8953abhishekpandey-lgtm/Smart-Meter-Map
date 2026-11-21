import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, effect, inject, input, output, viewChild } from '@angular/core';
import * as echarts from 'echarts/core';
import { BarChart, LineChart } from 'echarts/charts';
import { GridComponent, MarkLineComponent, TooltipComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import type { EChartsCoreOption } from 'echarts/core';

echarts.use([BarChart, LineChart, GridComponent, TooltipComponent, MarkLineComponent, CanvasRenderer]);

/** Thin, tree-shaken ECharts wrapper: reactive options, auto-resize, click events. */
@Component({
  selector: 'sm-echart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div #host class="chart" [style.height.px]="height()" role="img" [attr.aria-label]="ariaLabel()"></div>`,
  styles: [`.chart { width: 100%; }`],
})
export class EChart {
  readonly options = input.required<EChartsCoreOption>();
  readonly height = input(220);
  readonly ariaLabel = input('Chart');
  readonly pointClick = output<{ dataIndex: number; name: string }>();

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private chart?: echarts.ECharts;

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      this.chart = echarts.init(this.host().nativeElement, undefined, { renderer: 'canvas' });
      this.chart.on('click', (p: { dataIndex: number; name: string }) => this.pointClick.emit({ dataIndex: p.dataIndex, name: p.name }));
      this.chart.setOption(this.options(), true);
      const ro = new ResizeObserver(() => this.chart?.resize());
      ro.observe(this.host().nativeElement);
      destroyRef.onDestroy(() => {
        ro.disconnect();
        this.chart?.dispose();
      });
    });
    effect(() => {
      const options = this.options();
      this.chart?.setOption(options, true);
    });
  }
}
