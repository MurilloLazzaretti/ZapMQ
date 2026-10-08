import { CatalogObject, DatabaseAlert } from './models';

const DECIMAL = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });

/** What an alert of the database says, in the words of the panel. */
export function alertText(alert: DatabaseAlert): string {
  switch (alert.Kind) {
    case 'Missing':
      return `A instância não tem um banco chamado ${alert.Subject}`;
    case 'State':
      return `Banco ${alert.Subject} não está online`;
    case 'Blocking':
      return `Sessão bloqueada por outra há ${DECIMAL.format(Math.round(alert.Value ?? 0))} s`;
    case 'Disk':
      return `Disco ${alert.Subject} com ${DECIMAL.format(alert.Value ?? 0)}% livre`;
    case 'Backup':
      return alert.Value === null ? `Banco ${alert.Subject} sem backup completo` : `Último backup completo de ${alert.Subject} há ${DECIMAL.format(alert.Value)} h`;
    case 'Job':
      return `Job ${alert.Subject} falhou na última execução`;
    default:
      return `${alert.Kind} ${alert.Subject}`;
  }
}

/** A size given in kilobytes, in the unit that reads best. */
export function size(kilobytes: number | null | undefined): string {
  if (kilobytes === null || kilobytes === undefined) {
    return '—';
  }
  if (kilobytes >= 1024 * 1024) {
    return `${DECIMAL.format(kilobytes / 1024 / 1024)} GB`;
  }
  return kilobytes >= 1024 ? `${DECIMAL.format(kilobytes / 1024)} MB` : `${DECIMAL.format(kilobytes)} KB`;
}

const KINDS: Record<string, { one: string; many: string; icon: string }> = {
  Table: { one: 'Tabela', many: 'Tabelas', icon: 'table' },
  View: { one: 'View', many: 'Views', icon: 'visibility' },
  Procedure: { one: 'Procedure', many: 'Procedures', icon: 'terminal' },
  Function: { one: 'Function', many: 'Functions', icon: 'code' },
  Type: { one: 'Type', many: 'Types', icon: 'data_object' },
};

export function kindName(kind: string | null, many = false): string {
  const known = kind ? KINDS[kind] : undefined;
  return known ? (many ? known.many : known.one) : (kind ?? '');
}

export function kindIcon(kind: string | null): string {
  return (kind && KINDS[kind]?.icon) || 'data_object';
}

/** What sets an object apart within its kind: a function that gives a table, a type that is one. */
export function variety(item: Pick<CatalogObject, 'Variety'>): string {
  switch (item.Variety) {
    case 'FN':
      return 'escalar';
    case 'IF':
      return 'de tabela, em linha';
    case 'TF':
      return 'de tabela';
    case 'TT':
      return 'de tabela';
    default:
      return '';
  }
}
