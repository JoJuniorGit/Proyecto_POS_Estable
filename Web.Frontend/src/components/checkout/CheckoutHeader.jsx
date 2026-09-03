import React from 'react';
import CustomerSelectorCard from './CustomerSelectorCard';
import { formatBsS } from '../../utils/formatters';

const CheckoutHeader = React.memo(function CheckoutHeader({
  activeSale,
  targetTotalUSD,
  targetTotalBsS,
  onSelectCustomer,
}) {
  return (
    <div className="bg-[#121824] p-4 rounded-xl border border-gray-800 flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
      <div>
        <h3 className="text-xl font-bold text-white">Procesar Pago</h3>
        <p className="text-sm text-gray-400">
          Total Venta: <span className="text-emerald-400 font-bold text-base">{formatBsS(targetTotalBsS)}</span> <span className="text-gray-400 text-xs font-normal">(${targetTotalUSD.toFixed(2)} USD)</span>
        </p>
      </div>
      <div className="w-full sm:w-auto">
        <CustomerSelectorCard
          currentCustomer={activeSale?.customer}
          onSelectCustomer={onSelectCustomer}
        />
      </div>
    </div>
  );
});

export default CheckoutHeader;
