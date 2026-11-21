import type { EChartsCoreOption } from 'echarts/core';
import { TrendPoint } from '../core/api/models';
import { INK, SERIES_BLUE, SERIES_BLUE_DARK, fmt, shortDate } from '../core/ui/format';

const axisLabel = { color: INK.muted, fontSize: 11 };
const tooltipBase = {
  trigger: 'axis' as const,
  backgroundColor: '#ffffff',
  borderColor: 'rgba(11,11,11,0.10)',
  textStyle: { color: INK.primary, fontSize: 12 },
  extraCssText: 'box-shadow: 0 4px 16px rgba(0,0,0,.12); border-radius: 8px;',
};

/**
 * Daily communication success % (single series, single axis). A line is used (not bars) because the
 * y-axis is zoomed to the data range; bars would require a zero baseline. Counts live in the tooltip.
 */
export function successTrendOptions(points: TrendPoint[], selectedDate: string | null): EChartsCoreOption {
  const min = points.length ? Math.min(...points.map((p) => p.successPct)) : 0;
  const yMin = Math.max(0, Math.floor((min - 3) / 5) * 5);
  const selectedIndex = selectedDate ? points.findIndex((p) => p.date === selectedDate) : points.length - 1;
  return {
    animation: false,
    grid: { left: 40, right: 12, top: 12, bottom: 28 },
    tooltip: {
      ...tooltipBase,
      axisPointer: { type: 'line', lineStyle: { color: INK.axis } },
      formatter: (params: { dataIndex: number }[]) => {
        const p = points[params[0].dataIndex];
        return `<b>${shortDate(p.date)}</b><br/>Communicating: <b>${fmt(p.communicating)}</b> of ${fmt(p.expected)}<br/>Success: <b>${p.successPct.toFixed(2)}%</b>`;
      },
    },
    xAxis: {
      type: 'category',
      data: points.map((p) => shortDate(p.date)),
      axisLine: { lineStyle: { color: INK.axis } },
      axisTick: { show: false },
      axisLabel: { ...axisLabel, interval: Math.ceil(points.length / 7) - 1 },
    },
    yAxis: {
      type: 'value',
      min: yMin,
      max: 100,
      splitLine: { lineStyle: { color: INK.grid } },
      axisLabel: { ...axisLabel, formatter: '{value}%' },
    },
    series: [
      {
        type: 'line',
        data: points.map((p, i) => ({
          value: p.successPct,
          symbolSize: i === selectedIndex ? 9 : 6,
          itemStyle: { color: i === selectedIndex ? SERIES_BLUE_DARK : SERIES_BLUE, borderColor: '#fff', borderWidth: 2 },
        })),
        symbol: 'circle',
        showSymbol: true,
        lineStyle: { width: 2, color: SERIES_BLUE },
        markLine: selectedIndex >= 0
          ? { symbol: 'none', silent: true, label: { show: false }, lineStyle: { color: INK.secondary, type: 'dashed', width: 1 },
              data: [{ xAxis: selectedIndex }] }
          : undefined,
      },
    ],
  };
}

/** Daily kWh bars (zero baseline). */
export function consumptionOptions(daily: { date: string; kwh: number; metersReporting: number }[]): EChartsCoreOption {
  return {
    animation: false,
    grid: { left: 52, right: 12, top: 12, bottom: 28 },
    tooltip: {
      ...tooltipBase,
      axisPointer: { type: 'shadow' },
      formatter: (params: { dataIndex: number }[]) => {
        const d = daily[params[0].dataIndex];
        return `<b>${shortDate(d.date)}</b><br/>Consumption: <b>${fmt(Math.round(d.kwh))} kWh</b><br/>Meters reporting: ${fmt(d.metersReporting)}`;
      },
    },
    xAxis: { type: 'category', data: daily.map((d) => shortDate(d.date)), axisTick: { show: false },
             axisLine: { lineStyle: { color: INK.axis } }, axisLabel: { ...axisLabel, interval: Math.ceil(daily.length / 7) - 1 } },
    yAxis: { type: 'value', splitLine: { lineStyle: { color: INK.grid } }, axisLabel: { ...axisLabel, formatter: (v: number) => fmt(v) } },
    series: [{ type: 'bar', data: daily.map((d) => d.kwh), barMaxWidth: 14, itemStyle: { color: SERIES_BLUE, borderRadius: [4, 4, 0, 0] } }],
  };
}
