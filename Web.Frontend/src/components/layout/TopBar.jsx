import { Menu, AlertTriangle } from 'lucide-react';
import ThemeToggle from '../ui/ThemeToggle';
import { formatNumberEs } from '../../utils/formatters';

export default function TopBar({ title, exchangeRate, isRateOutdated, onMenuClick }) {
  return (
    <header className="topbar">
      <div className="topbar-left">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Abrir menú">
          <Menu size={20} />
        </button>
        <h1 className="topbar-title">{title}</h1>
      </div>

      <div className="topbar-right">
        {exchangeRate > 0 && (
          <div
            className={`topbar-rate ${isRateOutdated ? 'rate-outdated' : ''}`}
            title={isRateOutdated ? 'Tasa no actualizada en más de 24 horas. Se recomienda sincronizar o ingresar la tasa oficial.' : undefined}
          >
            Bs.S {formatNumberEs(exchangeRate)}
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
