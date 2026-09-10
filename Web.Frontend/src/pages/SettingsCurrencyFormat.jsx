import { DollarSign } from 'lucide-react';

export default function SettingsCurrencyFormat({ currencyFormat, handleFormatChange, formatBsS, formatUSD }) {
  return (
    <div className="card mb-4 p-3 sm:p-4">
      <div className="flex-between flex-align-center mb-3">
        <h3 className="card-title flex-align-center gap-2 text-base font-bold mb-0">
          <DollarSign size={20} className="color-primary flex-shrink-0" />
          <span>Formato Numérico y de Moneda</span>
        </h3>
      </div>
      <p className="text-muted text-xs sm:text-sm mb-4">
        Seleccione cómo desea visualizar y formatear los montos monetarios en todo el sistema (cierres, checkout, reportes y catálogo).
      </p>

      <div className="grid gap-4 set-format-grid mb-4">
        <div
          onClick={() => handleFormatChange('Venezuelan')}
          className="p-3.5 border rounded-lg cursor-pointer transition-all"
          style={{
            borderColor: currencyFormat === 'Venezuelan' ? 'var(--primary)' : 'var(--border)',
            backgroundColor: currencyFormat === 'Venezuelan' ? 'rgba(37, 99, 235, 0.08)' : 'var(--bg-surface)',
            borderWidth: currencyFormat === 'Venezuelan' ? '2px' : '1px'
          }}
          role="button"
          tabIndex={0}
          aria-label="Seleccionar Formato Venezolano Contable"
        >
          <div className="flex-between flex-align-center mb-2">
            <span className="font-bold text-sm sm:text-base">Venezolano Contable</span>
            <input
              type="radio"
              name="currencyFormat"
              value="Venezuelan"
              checked={currencyFormat === 'Venezuelan'}
              onChange={() => handleFormatChange('Venezuelan')}
              className="cursor-pointer"
            />
          </div>
          <p className="text-xs text-muted mb-2">
            Separador de miles: <strong>punto (.)</strong> | Separador decimal: <strong>coma (,)</strong>
          </p>
          <div className="font-mono text-xs p-2 rounded bg-background border">
            Ejemplo: <strong>172.786,94</strong>
          </div>
        </div>

        <div
          onClick={() => handleFormatChange('International')}
          className="p-3.5 border rounded-lg cursor-pointer transition-all"
          style={{
            borderColor: currencyFormat === 'International' ? 'var(--primary)' : 'var(--border)',
            backgroundColor: currencyFormat === 'International' ? 'rgba(37, 99, 235, 0.08)' : 'var(--bg-surface)',
            borderWidth: currencyFormat === 'International' ? '2px' : '1px'
          }}
          role="button"
          tabIndex={0}
          aria-label="Seleccionar Formato Internacional"
        >
          <div className="flex-between flex-align-center mb-2">
            <span className="font-bold text-sm sm:text-base">Internacional</span>
            <input
              type="radio"
              name="currencyFormat"
              value="International"
              checked={currencyFormat === 'International'}
              onChange={() => handleFormatChange('International')}
              className="cursor-pointer"
            />
          </div>
          <p className="text-xs text-muted mb-2">
            Separador de miles: <strong>coma (,)</strong> | Separador decimal: <strong>punto (.)</strong>
          </p>
          <div className="font-mono text-xs p-2 rounded bg-background border">
            Ejemplo: <strong>172,786.94</strong>
          </div>
        </div>
      </div>

      <div className="p-3 border rounded-lg bg-surface">
        <span className="text-xs font-bold text-muted uppercase tracking-wider block mb-2">
          Vista Previa Activa en el Sistema:
        </span>
        <div className="grid gap-2 set-preview-grid text-xs sm:text-sm">
          <div className="flex-between p-2 rounded bg-background border">
            <span className="text-muted">Monto en Bolívares (Bs.S):</span>
            <span className="font-mono font-bold color-primary">{formatBsS(172786.94, 2)}</span>
          </div>
          <div className="flex-between p-2 rounded bg-background border">
            <span className="text-muted">Monto en Dólares ($):</span>
            <span className="font-mono font-bold color-primary">{formatUSD(1250.5, 2)}</span>
          </div>
        </div>
      </div>
    </div>
  );
}