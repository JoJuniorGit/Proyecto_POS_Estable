function readQuantity(value) {
  if (value === null || value === undefined || value === '') return null;
  const quantity = Number(value);
  return Number.isFinite(quantity) ? Math.max(0, quantity) : null;
}

function normalizeQuantity(quantity) {
  const normalized = Number(quantity.toFixed(3));
  return Object.is(normalized, -0) ? 0 : normalized;
}

function formatUnits(quantity) {
  return String(normalizeQuantity(quantity));
}

export function clampQuantity(value, pending) {
  const maximum = readQuantity(pending) ?? 0;
  const rawValue = value === null || value === undefined ? '' : String(value).trim();

  if (rawValue === '') return { value: 0, isInvalid: false };

  const requested = Number(rawValue);
  if (!Number.isFinite(requested)) return { value: 0, isInvalid: true };

  return {
    value: Math.min(Math.max(requested, 0), maximum),
    isInvalid: requested < 0 || requested > maximum,
  };
}

export function computeProgress(items) {
  let totalUnits = 0;
  let deliveredUnits = 0;
  let pendingUnits = 0;

  for (const item of Array.isArray(items) ? items : []) {
    if (!item) continue;

    const itemDelivered = readQuantity(item.deliveredQuantity);
    const itemPending = readQuantity(item.pendingQuantity);
    const itemTotal = readQuantity(item.totalQuantity ?? item.quantity)
      ?? (itemDelivered ?? 0) + (itemPending ?? 0);
    const delivered = Math.min(itemTotal, itemDelivered ?? Math.max(0, itemTotal - (itemPending ?? itemTotal)));
    const pending = Math.min(Math.max(0, itemTotal - delivered), itemPending ?? Math.max(0, itemTotal - delivered));

    totalUnits += itemTotal;
    deliveredUnits += delivered;
    pendingUnits += pending;
  }

  totalUnits = normalizeQuantity(totalUnits);
  deliveredUnits = normalizeQuantity(deliveredUnits);
  pendingUnits = normalizeQuantity(pendingUnits);
  const percent = totalUnits > 0
    ? Number(Math.min(100, (deliveredUnits / totalUnits) * 100).toFixed(2))
    : 0;

  return {
    totalUnits,
    deliveredUnits,
    pendingUnits,
    percent,
    label: `Retirado: ${formatUnits(deliveredUnits)}/${formatUnits(totalUnits)}`,
  };
}

export function isConfirmEnabled(rows) {
  if (!Array.isArray(rows) || rows.length === 0) return false;

  let hasPositiveQuantity = false;
  for (const row of rows) {
    const quantity = Number(row?.quantity);
    const pending = readQuantity(row?.pendingQuantity) ?? 0;
    if (row?.isInvalid || !Number.isFinite(quantity) || quantity < 0 || quantity > pending) return false;
    if (quantity > 0) hasPositiveQuantity = true;
  }

  return hasPositiveQuantity;
}

export function buildDeliveryPayload(rows) {
  if (!Array.isArray(rows)) return [];

  return rows
    .filter((row) => {
      const quantity = Number(row?.quantity);
      return !row?.isInvalid && Number.isFinite(quantity) && quantity > 0;
    })
    .map((row) => ({
      saleItemId: row.saleItemId,
      quantity: Number(row.quantity),
    }));
}
