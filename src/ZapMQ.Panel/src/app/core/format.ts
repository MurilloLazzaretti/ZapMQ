import { Pipe, PipeTransform } from '@angular/core';

const integer = new Intl.NumberFormat('pt-BR');
const compact = new Intl.NumberFormat('pt-BR', { notation: 'compact', maximumFractionDigits: 1 });
const decimal = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });
const dateTime = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'medium' });
const timeOnly = new Intl.DateTimeFormat('pt-BR', { timeStyle: 'medium' });

/** 12.345 → "12.345"; with compact, "12,3 mil". */
@Pipe({ name: 'num' })
export class NumPipe implements PipeTransform {
  transform(value: number | null | undefined, style: 'full' | 'compact' | 'decimal' = 'full'): string {
    if (value === null || value === undefined) {
      return '—';
    }
    return style === 'compact' ? compact.format(value) : style === 'decimal' ? decimal.format(value) : integer.format(value);
  }
}

@Pipe({ name: 'when' })
export class WhenPipe implements PipeTransform {
  transform(value: string | null | undefined, style: 'full' | 'time' = 'full'): string {
    if (!value) {
      return '—';
    }
    const date = new Date(value);
    return style === 'time' ? timeOnly.format(date) : dateTime.format(date);
  }
}

/** How long ago, or how long something has lasted: "há 3 min", "2 h 10 min". */
export function span(milliseconds: number): string {
  const seconds = Math.max(0, Math.round(milliseconds / 1000));
  if (seconds < 60) {
    return `${seconds} s`;
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 48) {
    return minutes % 60 ? `${hours} h ${minutes % 60} min` : `${hours} h`;
  }
  return `${Math.floor(hours / 24)} d`;
}

@Pipe({ name: 'ago', pure: false })
export class AgoPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? `há ${span(Date.now() - new Date(value).getTime())}` : '—';
  }
}

@Pipe({ name: 'since', pure: false })
export class SincePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? span(Date.now() - new Date(value).getTime()) : '—';
  }
}

export function seconds(value: number): string {
  return span(value * 1000);
}
