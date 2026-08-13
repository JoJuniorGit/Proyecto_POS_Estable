import { Menu, DollarSign } from 'lucide-react';
import ThemeToggle from '../ui/ThemeToggle';
import { formatNumberEs } from '../../utils/formatters';

export default function TopBar({ title, exchangeRate, onMenuClick }) {
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
          <div className="topbar-rate">
            <DollarSign size={14} />
            Bs.S {formatNumberEs(exchangeRate)}
          </div>
        )}
        <ThemeToggle />
      </div>
    </header>
  );
}
