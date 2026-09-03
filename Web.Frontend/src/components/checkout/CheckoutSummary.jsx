import React from 'react';
import { formatBsS } from '../../utils/formatters';

const CheckoutSummary = React.memo(function CheckoutSummary({
  paidUsd,
  paidBsS,
  remainingUsd,
  remainingBsS,
  isCustodyAllowed,
  isPendingPickup,
  onTogglePendingPickup,
  canFinalize,
  isProcessing,
  onFinalize,
}) {
  return (
    <div className="bg-[#121824] p-4 rounded-xl border border-gray-800 space-y-4">
      <div className="space-y-2 text-sm text-gray-300">
        <div className="flex justify-between">
          <span>Cubierto USD:</span>
          <span className="font-medium text-emerald-400">${paidUsd.toFixed(2)} USD</span>
        </div>
        <div className="flex justify-between">
          <span>Cubierto Bs.S:</span>
          <span className="font-medium text-emerald-400">{formatBsS(paidBsS)}</span>
        </div>
        <div className="flex justify-between border-t border-gray-800 pt-2 font-bold text-white">
          <span>Pendiente:</span>
          <span className={remainingUsd <= 0.05 ? 'text-emerald-400' : 'text-amber-400'}>
            ${remainingUsd.toFixed(2)} USD ({formatBsS(remainingBsS)})
          </span>
        </div>
      </div>

      {isCustodyAllowed && (
        <label className="flex items-center gap-2 text-sm text-gray-300 cursor-pointer pt-2 border-t border-gray-800">
          <input
            type="checkbox"
            checked={isPendingPickup}
            onChange={(e) => onTogglePendingPickup(e.target.checked)}
            className="w-4 h-4 rounded border-gray-700 bg-gray-900 text-emerald-500 focus:ring-emerald-500"
          />
          <span>Registrar como Mercancía en Custodia (Apartado Pagado)</span>
        </label>
      )}

      <button
        onClick={onFinalize}
        disabled={!canFinalize || isProcessing}
        className="w-full py-3 bg-emerald-600 hover:bg-emerald-500 disabled:opacity-50 text-white font-bold rounded-lg transition-colors flex items-center justify-center gap-2"
      >
        {isProcessing ? 'Procesando...' : 'Completar Venta'}
      </button>
    </div>
  );
});

export default CheckoutSummary;
