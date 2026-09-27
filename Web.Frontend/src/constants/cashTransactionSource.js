export const CashTransactionSource = Object.freeze({
  Opening: 0,
  SalePayment: 1,
  CashAdvance: 2,
  ManualAdjustment: 3,
  Closing: 4,
  CashIn: 5,
  CashOut: 6,
});

export const SOURCE_DEFINITIONS = Object.freeze([
  Object.freeze({ id: CashTransactionSource.Opening, key: 'opening', label: 'Apertura' }),
  Object.freeze({ id: CashTransactionSource.SalePayment, key: 'sale', label: 'Venta POS' }),
  Object.freeze({ id: CashTransactionSource.CashAdvance, key: 'advance', label: 'Adelanto Efectivo' }),
  Object.freeze({ id: CashTransactionSource.ManualAdjustment, key: 'adjustment', label: 'Ajuste Manual' }),
  Object.freeze({ id: CashTransactionSource.Closing, key: 'closing', label: 'Cierre Caja' }),
  Object.freeze({ id: CashTransactionSource.CashIn, key: 'cashin', label: 'Ingreso de Caja' }),
  Object.freeze({ id: CashTransactionSource.CashOut, key: 'cashout', label: 'Retiro de Caja' }),
]);

const SOURCE_NAME_ALIASES = Object.freeze({ Sale: 'SalePayment' });

function resolveSourceId(source) {
  if (typeof source === 'number') {
    return Number.isInteger(source) ? source : undefined;
  }

  if (typeof source !== 'string') {
    return undefined;
  }

  const id = CashTransactionSource[SOURCE_NAME_ALIASES[source] || source];
  return typeof id === 'number' ? id : undefined;
}

export function getSourceDefinition(source) {
  const id = resolveSourceId(source);
  return SOURCE_DEFINITIONS.find((definition) => definition.id === id);
}

export function getSourceLabel(source) {
  const definition = getSourceDefinition(source);
  if (definition) return definition.label;
  return typeof source === 'string' ? source : 'Movimiento';
}

export function getSourceIdByFilterKey(key) {
  const definition = SOURCE_DEFINITIONS.find((entry) => entry.key === key);
  return definition ? definition.id : undefined;
}

export function matchesSourceFilter(filterKey, source) {
  if (filterKey === 'all') return true;

  const sourceId = getSourceIdByFilterKey(filterKey);
  return sourceId !== undefined && resolveSourceId(source) === sourceId;
}
