export const CLAIM_ACTION_LABELS = {
  Checkout: 'En proceso de pago',
  Editing: 'Editando pedido',
};

export function isLockedByOther(sale, currentUserId) {
  const claimedByUserId = sale?.claimedByUserId;
  if (claimedByUserId === null || claimedByUserId === undefined) return false;
  return claimedByUserId !== currentUserId;
}

export function getLockInfo(sale, currentUserId) {
  const claimedByUserId = sale?.claimedByUserId;
  const isLocked = claimedByUserId !== null && claimedByUserId !== undefined;
  const isMine = isLocked && claimedByUserId === currentUserId;
  const isLockedByOther = isLocked && !isMine;

  let label = null;
  if (isMine) {
    label = 'Bloqueado por ti';
  } else if (isLockedByOther) {
    const userName = sale?.claimedByUserName || 'otro cajero';
    const actionLabel = CLAIM_ACTION_LABELS[sale?.claimAction] || 'en proceso';
    label = `Bloqueado por ${userName} - ${actionLabel}`;
  }

  return { isLocked, isMine, isLockedByOther, label };
}
