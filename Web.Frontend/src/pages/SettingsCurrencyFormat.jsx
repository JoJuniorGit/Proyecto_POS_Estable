import { DollarSign } from 'lucide-react';

export default function SettingsCurrencyFormat({ currencyFormat, handleFormatChange, formatBsS, formatUSD }) {
  return (
    <section className="set-card">
      <header className="set-card-head">
        <span className="set-card-icon" aria-hidden="true">
          <DollarSign size={18} />
        </span>
        <div className="set-card-heading">
          <h3 className="set-card-title">Formato Numérico y de Moneda</h3>
          <p className="set-card-subtitle">
            Seleccione cómo desea visualizar y formatear los montos monetarios en todo el sistema
            (cierres, checkout, reportes y catálogo).
          </p>
        </div>
        <div className="set-card-head-actions">
          <span className="set-status-pill set-status-pill--accent">Auto-guardado</span>
        </div>
      </header>

      <div className="set-choice-grid" role="radiogroup" aria-label="Formato de moneda">
        <label className={`set-choice-card${currencyFormat === 'Venezuelan' ? ' is-selected' : ''}`}>
          <span className="set-choice-head">
            <span className="set-choice-title">Venezolano Contable</span>
            <input
              type="radio"
              name="currencyFormat"
              value="Venezuelan"
              checked={currencyFormat === 'Venezuelan'}
              onChange={() => handleFormatChange('Venezuelan')}
              aria-label="Seleccionar Formato Venezolano Contable"
            />
          </span>
          <span className="set-choice-desc">
            Separador de miles: <strong>punto (.)</strong> | Separador decimal: <strong>coma (,)</strong>
          </span>
          <span className="set-choice-example">Ejemplo: 172.786,94</span>
        </label>

        <label className={`set-choice-card${currencyFormat === 'International' ? ' is-selected' : ''}`}>
          <span className="set-choice-head">
            <span className="set-choice-title">Internacional</span>
            <input
              type="radio"
              name="currencyFormat"
              value="International"
              checked={currencyFormat === 'International'}
              onChange={() => handleFormatChange('International')}
              aria-label="Seleccionar Formato Internacional"
            />
          </span>
          <span className="set-choice-desc">
            Separador de miles: <strong>coma (,)</strong> | Separador decimal: <strong>punto (.)</strong>
          </span>
          <span className="set-choice-example">Ejemplo: 172,786.94</span>
        </label>
      </div>

      <p className="set-hint">
        El formato se guarda automáticamente y se aplica a cierres, checkout, reportes y catálogo en todo el sistema.
      </p>

      <div className="set-preview-panel">
        <span className="set-preview-kicker">VISTA PREVIA EN VIVO</span>
        <div className="set-preview-items">
          <div className="set-preview-item">
            <span className="set-preview-label">Monto en Bolívares (Bs.S)</span>
            <span className="set-preview-value">{formatBsS(172786.94, 2)}</span>
          </div>
          <div className="set-preview-divider" aria-hidden="true" />
          <div className="set-preview-item">
            <span className="set-preview-label">Monto en Dólares (USD)</span>
            <span className="set-preview-value">{formatUSD(1250.5, 2)}</span>
          </div>
        </div>
      </div>
    </section>
  );
}
