import { Lock } from 'lucide-react';

export default function PendingOrderLockBadge({ lockInfo }) {
  if (!lockInfo?.isLocked) return null;

  return (
    <span className={`ppo-lock-badge${lockInfo.isMine ? ' ppo-lock-badge--mine' : ''}`}>
      <Lock size={12} /> {lockInfo.label}
    </span>
  );
}
