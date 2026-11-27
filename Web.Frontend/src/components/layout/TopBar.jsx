import { Menu, AlertTriangle } from 'lucide-react';
import ThemeToggle from '../ui/ThemeToggle';
import { formatNumberEs } from '../../utils/formatters';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { MONETARY_MODE_DUAL } from '../../utils/monetaryProfile';

export default function TopBar({ title, exchangeRate, isRateOutdated, onMenuClick }) {
  // 8.211-T4b2b1 (S9): la tasa del día es USD -> moneda local. En Dual se rotula con la
  // secundaria del perfil; en Single no existe una tasa de conversión que mostrar.
  const { mode, secondaryCurrencySymbol } = useMonetaryProfile();
  const isDual = mode === MONETARY_MODE_DUAL;

  return (
    <header className="topbar">
      <div className="topbar-left">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Abrir menú">
          <Menu size={20} />
        </button>
        <h1 className="topbar-title">{title}</h1>
      </div>

      <div className="topbar-right">
        {isDual && exchangeRate > 0 && (
          <div
            className={`topbar-rate ${isRateOutdated ? 'rate-outdated' : ''}`}
            title={isRateOutdated ? 'Tasa no actualizada en más de 24 horas. Se recomienda sincronizar o ingresar la tasa oficial.' : undefined}
          >
            {secondaryCurrencySymbol} {formatNumberEs(exchangeRate)}
            {isRateOutdated && (
              <span className="rate-outdated-icon" title="Tasa desactualizada (>24h)">
                <span className="rate-outdated-icon" title="Tasa desactualizada (>24h)">
                <AlertTriangle size={13} />
              </span>
              </span>
            )}
          </div>
        )}
        <ThemeToggle />
      </div>
    </header>
  );
}
