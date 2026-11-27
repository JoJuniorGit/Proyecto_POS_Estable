import { useState } from 'react';
import { CreditCard, Plus, Loader2, Trash2 } from 'lucide-react';
import { createPaymentMethod, updatePaymentMethod, deletePaymentMethod } from '../services/paymentApi';
import { PAYMENT_METHOD_FEE_TYPE } from '../services/profitabilityApi';
import { useMonetaryProfile } from '../context/MonetaryProfileContext';
import { parseAmount } from '../utils/formatters';
import ConfirmModal from '../components/ui/ConfirmModal';

const FEE_TYPE_PERCENTAGE = PAYMENT_METHOD_FEE_TYPE.Percentage;
const FEE_TYPE_FIXED = PAYMENT_METHOD_FEE_TYPE.Fixed;
const FEE_TYPE_COMBINED = PAYMENT_METHOD_FEE_TYPE.PercentagePlusFixed;

const FEE_TYPE_OPTIONS = [
  { value: FEE_TYPE_PERCENTAGE, label: 'Porcentaje (%)' },
  { value: FEE_TYPE_FIXED, label: 'Monto fijo por transacción' },
  { value: FEE_TYPE_COMBINED, label: 'Porcentaje + fijo' },
];

/**
 * Builds the optional commission block for the payment-method create request. Cash methods and
 * 0/0 never send a commission; the backend creates the fee rule atomically with the method.
 */
export function buildPaymentMethodCommission({
  isCash,
  feeType,
  percentage,
  fixedAmount,
  currencyCode,
}) {
  const type = Number(feeType);
  const parsedPercentage = type === FEE_TYPE_FIXED ? null : parseAmount(percentage);
  const parsedFixedAmount = type === FEE_TYPE_PERCENTAGE
    ? null
    : (String(fixedAmount ?? '').trim() ? parseAmount(fixedAmount) : 0);
  const hasPositiveAmount = (parsedPercentage ?? 0) > 0 || (parsedFixedAmount ?? 0) > 0;
  const hasPositiveCommission = !isCash && hasPositiveAmount;

  return {
    feeType: type,
    percentage: parsedPercentage,
    fixedAmount: parsedFixedAmount,
    hasPositiveAmount,
    hasPositiveCommission,
    commission: hasPositiveCommission
      ? {
          feeType: type,
          percentage: parsedPercentage,
          fixedAmount: parsedFixedAmount,
          currencyCode: String(currencyCode ?? '').trim(),
          isActive: true,
        }
      : null,
  };
}

export default function SettingsPaymentMethods({ methods, setMethods, setMessage, loadMethods, loadingMethods }) {
  const { profile } = useMonetaryProfile();
  const localCurrency = profile?.mode === 'Dual'
    ? profile?.secondaryCurrencyCode
    : profile?.primaryCurrencyCode;
  const feeCurrencyOptions = Array.from(new Set(['USD', localCurrency].filter(Boolean)));

  const [newMethodName, setNewMethodName] = useState('');
  const [newMethodIsCash, setNewMethodIsCash] = useState(false);
  const [newMethodRequiresRef, setNewMethodRequiresRef] = useState(false);
  const [newMethodFeeType, setNewMethodFeeType] = useState(FEE_TYPE_PERCENTAGE);
  const [newMethodPercentage, setNewMethodPercentage] = useState('');
  const [newMethodFixedAmount, setNewMethodFixedAmount] = useState('');
  const [newMethodFeeCurrency, setNewMethodFeeCurrency] = useState('USD');
  const [methodToDelete, setMethodToDelete] = useState(null);

  const handleToggleCash = async (method) => {
    const updated = { ...method, isCash: !method.isCash };
    setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    try {
      await updatePaymentMethod(method.id, updated);
      setMessage({ type: 'success', text: `Método "${method.name}" clasificado como ${updated.isCash ? 'Físico' : 'Digital'}.` });
    } catch (err) {
      console.error('[SettingsPage] Error actualizando tipo de método:', err);
      setMessage({ type: 'danger', text: err?.message || 'Error al cambiar tipo del método.' });
      loadMethods();
    }
  };

  const handleToggleActive = async (method) => {
    const updated = { ...method, isActive: !method.isActive };
    setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    try {
      await updatePaymentMethod(method.id, updated);
    } catch (err) {
      console.error('[SettingsPage] Error actualizando método:', err);
      setMessage({ type: 'danger', text: err?.message || 'Error al cambiar estado del método.' });
      loadMethods();
    }
  };

  const handleToggleRef = async (method) => {
    const updated = { ...method, requiresReference: !method.requiresReference };
    setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    try {
      await updatePaymentMethod(method.id, updated);
    } catch (err) {
      console.error('[SettingsPage] Error actualizando método:', err);
      setMessage({ type: 'danger', text: err?.message || 'Error al actualizar configuración de referencia.' });
      loadMethods();
    }
  };

  const handleDeleteMethod = (method) => {
    setMethodToDelete(method);
  };

  const confirmDeleteMethod = async () => {
    if (!methodToDelete) return;
    const method = methodToDelete;
    setMethodToDelete(null);

    setMethods((prev) => prev.filter((m) => m.id !== method.id));
    try {
      await deletePaymentMethod(method.id);
      setMessage({ type: 'success', text: `Método "${method.name}" eliminado correctamente.` });
    } catch (err) {
      console.error('[SettingsPage] Error eliminando método:', err);
      setMessage({ type: 'danger', text: err?.message || 'Error al eliminar método de pago.' });
      loadMethods();
    }
  };

  const handleAddMethod = async (e) => {
    e.preventDefault();
    if (!newMethodName.trim()) return;

    const {
      feeType,
      percentage,
      fixedAmount,
      hasPositiveAmount,
      hasPositiveCommission,
      commission,
    } = buildPaymentMethodCommission({
      isCash: newMethodIsCash,
      feeType: newMethodFeeType,
      percentage: newMethodPercentage,
      fixedAmount: newMethodFixedAmount,
      currencyCode: newMethodFeeCurrency,
    });

    if (newMethodIsCash && hasPositiveAmount) {
      setMessage({ type: 'danger', text: 'Un método de efectivo no puede tener comisión mayor que cero.' });
      return;
    }

    if (!newMethodIsCash && hasPositiveCommission) {
      if ((feeType === FEE_TYPE_PERCENTAGE || feeType === FEE_TYPE_COMBINED)
        && !(percentage > 0 && percentage <= 100)) {
        setMessage({ type: 'danger', text: 'El porcentaje de comisión debe ser mayor que 0 y menor o igual a 100.' });
        return;
      }
      if (feeType === FEE_TYPE_FIXED && !(fixedAmount > 0)) {
        setMessage({ type: 'danger', text: 'El monto fijo por transacción debe ser mayor que 0.' });
        return;
      }
      if (!newMethodFeeCurrency) {
        setMessage({ type: 'danger', text: 'La moneda de la comisión es obligatoria.' });
        return;
      }
    }

    try {
      const dto = {
        name: newMethodName.trim(),
        isActive: true,
        isCash: newMethodIsCash,
        requiresReference: newMethodRequiresRef,
        commission,
      };
      await createPaymentMethod(dto);
      setNewMethodName('');
      setNewMethodIsCash(false);
      setNewMethodRequiresRef(false);
      setNewMethodFeeType(FEE_TYPE_PERCENTAGE);
      setNewMethodPercentage('');
      setNewMethodFixedAmount('');
      setNewMethodFeeCurrency('USD');
      setMessage({ type: 'success', text: 'Nuevo método de pago agregado correctamente.' });
      loadMethods();
      window.dispatchEvent(new CustomEvent('onPaymentMethodFeesUpdated'));
    } catch (err) {
      console.error('[SettingsPage] Error agregando método:', err);
      setMessage({ type: 'danger', text: err?.message || 'Error al crear método de pago.' });
      loadMethods();
    }
  };

  return (
    <section className="set-card">
      <header className="set-card-head">
        <span className="set-card-icon" aria-hidden="true">
          <CreditCard size={18} />
        </span>
        <div className="set-card-heading">
          <h3 className="set-card-title">Métodos de Pago Habilitados</h3>
          <p className="set-card-subtitle">
            Administre los medios de cobro disponibles, su tipo y los requerimientos operativos del POS.
          </p>
        </div>
        <div className="set-card-head-actions">
          <span className="set-status-pill set-status-pill--neutral">
            {methods.length} {methods.length === 1 ? 'método' : 'métodos'}
          </span>
        </div>
      </header>

      {loadingMethods ? (
        <div className="set-loading">
          <Loader2 className="animate-spin" size={24} />
          <span>Cargando métodos de pago...</span>
        </div>
      ) : (
        <>
          <div className="settings-mobile-cards-view set-mobile-list">
            {methods.map((m) => (
              <div key={m.id} className="settings-method-card set-method-card">
                <div className="set-method-card-head">
                  <div className="set-method-name">
                    <span className="set-method-name-text">{m.name}</span>
                    <span className="set-chip set-chip--neutral set-chip--static">{m.currency || 'Bs.S'}</span>
                  </div>
                  <div className="set-method-actions">
                    <button
                      type="button"
                      className={`set-chip set-chip--button ${m.isActive ? 'set-chip--success' : 'set-chip--danger'}`}
                      onClick={() => handleToggleActive(m)}
                      title="Alternar estado activo"
                      aria-label={`Alternar estado activo para ${m.name}`}
                    >
                      {m.isActive ? 'Activo' : 'Inactivo'}
                    </button>
                    <button
                      type="button"
                      className="set-icon-btn set-icon-btn--danger"
                      onClick={() => handleDeleteMethod(m)}
                      title="Eliminar método de pago"
                      aria-label={`Eliminar método ${m.name}`}
                    >
                      <Trash2 size={15} />
                    </button>
                  </div>
                </div>

                <div className="set-method-card-meta">
                  <span className="set-method-meta-label">Tipo:</span>
                  <button
                    type="button"
                    className={`set-chip set-chip--button ${m.isCash ? 'set-chip--success' : 'set-chip--accent'}`}
                    onClick={() => handleToggleCash(m)}
                    title="Clic para cambiar entre Físico y Digital"
                    aria-label={`Cambiar tipo de ${m.name}. Actualmente ${m.isCash ? 'Físico' : 'Digital'}`}
                  >
                    {m.isCash ? 'Físico' : 'Digital'}
                  </button>
                  <span className="set-method-meta-label">Requiere Ref.:</span>
                  <button
                    type="button"
                    className={`set-chip set-chip--button ${m.requiresReference ? 'set-chip--accent' : 'set-chip--neutral'}`}
                    onClick={() => handleToggleRef(m)}
                    title="Alternar requerimiento de referencia"
                    aria-label={`Alternar requerimiento de referencia para ${m.name}`}
                  >
                    {m.requiresReference ? 'Sí' : 'No'}
                  </button>
                </div>
              </div>
            ))}
          </div>

          <div className="settings-desktop-table-view set-table-wrap">
            <table className="set-table set-table--payments">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Tipo</th>
                  <th>Moneda</th>
                  <th>Requiere Referencia</th>
                  <th>Estado</th>
                  <th>Acciones</th>
                </tr>
              </thead>
              <tbody>
                {methods.map((m) => (
                  <tr key={m.id}>
                    <td className="font-medium">{m.name}</td>
                    <td>
                      <button
                        type="button"
                        className={`set-chip set-chip--button ${m.isCash ? 'set-chip--success' : 'set-chip--accent'}`}
                        onClick={() => handleToggleCash(m)}
                        title="Clic para cambiar entre Físico y Digital"
                        aria-label={`Cambiar tipo de método ${m.name}. Actualmente ${m.isCash ? 'Físico' : 'Digital'}`}
                      >
                        {m.isCash ? 'Físico' : 'Digital'}
                      </button>
                    </td>
                    <td>
                      <span className="set-chip set-chip--neutral set-chip--static">{m.currency || 'Bs.S'}</span>
                    </td>
                    <td>
                      <button
                        type="button"
                        className={`set-chip set-chip--button ${m.requiresReference ? 'set-chip--accent' : 'set-chip--neutral'}`}
                        onClick={() => handleToggleRef(m)}
                        title="Alternar requerimiento de referencia"
                        aria-label={`Alternar requerimiento de referencia para ${m.name}`}
                      >
                        {m.requiresReference ? 'Sí' : 'No'}
                      </button>
                    </td>
                    <td>
                      <button
                        type="button"
                        className={`set-chip set-chip--button ${m.isActive ? 'set-chip--success' : 'set-chip--danger'}`}
                        onClick={() => handleToggleActive(m)}
                        title="Alternar activación en POS"
                        aria-label={`Alternar activación para ${m.name}`}
                      >
                        {m.isActive ? 'Activo' : 'Inactivo'}
                      </button>
                    </td>
                    <td>
                      <button
                        type="button"
                        className="set-icon-btn set-icon-btn--danger"
                        onClick={() => handleDeleteMethod(m)}
                        title="Eliminar método de pago"
                        aria-label={`Eliminar método ${m.name}`}
                      >
                        <Trash2 size={15} />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      <form onSubmit={handleAddMethod} className="set-form-panel">
        <div className="set-form-head">
          <h4 className="set-form-title">Nuevo Método de Pago</h4>
          <p className="set-form-subtitle">
            Registre el medio de cobro y, si corresponde, la comisión que asume el negocio por su uso.
          </p>
        </div>

        <div className="set-callout set-callout--info">
          <div>
            <span className="set-callout-title">Comisión del método (opcional)</span>
            <p>
              Es un costo del negocio y no se suma al cliente. El mantenimiento de cuenta y los gastos
              mensuales del POS no se ingresan acá: se registran como gasto operativo del período.
            </p>
          </div>
        </div>

        <div className="set-form-grid set-form-grid--commission">
          <div className="form-group">
            <label className="form-label" htmlFor="pm-new-fee-type">Tipo de comisión</label>
            <select
              id="pm-new-fee-type"
              className="form-input"
              value={newMethodFeeType}
              onChange={(e) => setNewMethodFeeType(Number(e.target.value))}
              disabled={newMethodIsCash}
            >
              {FEE_TYPE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </div>
          <div className="form-group">
            <label className="form-label" htmlFor="pm-new-percentage">Porcentaje (%)</label>
            <input
              id="pm-new-percentage"
              type="number"
              className="form-input"
              min="0"
              max="100"
              step="0.01"
              placeholder="0.75"
              value={newMethodPercentage}
              onChange={(e) => setNewMethodPercentage(e.target.value)}
              disabled={newMethodIsCash || newMethodFeeType === FEE_TYPE_FIXED}
            />
          </div>
          <div className="form-group">
            <label className="form-label" htmlFor="pm-new-fixed">Fijo por transacción</label>
            <input
              id="pm-new-fixed"
              type="number"
              className="form-input"
              min="0"
              step="0.01"
              placeholder="0.00"
              value={newMethodFixedAmount}
              onChange={(e) => setNewMethodFixedAmount(e.target.value)}
              disabled={newMethodIsCash || newMethodFeeType === FEE_TYPE_PERCENTAGE}
            />
          </div>
          <div className="form-group">
            <label className="form-label" htmlFor="pm-new-currency">Moneda de la comisión</label>
            <select
              id="pm-new-currency"
              className="form-input"
              value={newMethodFeeCurrency}
              onChange={(e) => setNewMethodFeeCurrency(e.target.value)}
              disabled={newMethodIsCash}
            >
              {feeCurrencyOptions.map((code) => (
                <option key={code} value={code}>{code}</option>
              ))}
            </select>
          </div>
        </div>

        <div className="set-form-grid set-form-grid--name">
          <div className="form-group">
            <label className="form-label" htmlFor="pm-new-name">Nombre del Método</label>
            <input
              id="pm-new-name"
              type="text"
              className="form-input"
              placeholder="Ej. Pago Móvil Banesco"
              value={newMethodName}
              onChange={(e) => setNewMethodName(e.target.value)}
              required
            />
          </div>
          <div className="set-check-field">
            <label className="set-check-label" title="Desmarcado por defecto: se creará como Digital">
              <input
                type="checkbox"
                checked={newMethodIsCash}
                onChange={(e) => {
                  setNewMethodIsCash(e.target.checked);
                  if (e.target.checked) {
                    setNewMethodPercentage('');
                    setNewMethodFixedAmount('');
                  }
                }}
              />
              <span>Es Efectivo (Físico)</span>
            </label>
          </div>
          <div className="set-check-field">
            <label className="set-check-label">
              <input
                type="checkbox"
                checked={newMethodRequiresRef}
                onChange={(e) => setNewMethodRequiresRef(e.target.checked)}
              />
              <span>Req. Referencia</span>
            </label>
          </div>
          <button type="submit" className="btn btn-primary set-submit">
            <Plus size={16} /> Agregar
          </button>
        </div>
      </form>

      <ConfirmModal
        isOpen={!!methodToDelete}
        onClose={() => setMethodToDelete(null)}
        onConfirm={confirmDeleteMethod}
        title="¿Eliminar método de pago?"
        message={`¿Está seguro de eliminar el método de pago "${methodToDelete?.name}"? Si posee transacciones históricas registradas, será archivado de forma segura sin afectar reportes ni auditorías.`}
        confirmText="Eliminar Método"
        cancelText="Cancelar"
        variant="danger"
      />
    </section>
  );
}
