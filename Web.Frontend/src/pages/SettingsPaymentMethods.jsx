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
    <div className="card mb-4 p-3 sm:p-4">
      <h3 className="card-title mb-3 flex-align-center gap-2 text-base font-bold">
        <CreditCard size={20} className="color-primary flex-shrink-0" /> Métodos de Pago Habilitados
      </h3>

      {loadingMethods ? (
        <div className="p-4 text-center text-muted">
          <Loader2 className="animate-spin mb-2 inline-block" size={24} />
          <div>Cargando métodos de pago...</div>
        </div>
      ) : (
        <>
          <div className="settings-mobile-cards-view mb-4">
            {methods.map((m) => (
              <div key={m.id} className="settings-method-card p-3 mb-3 border rounded-lg bg-surface shadow-xs">
                <div className="flex-between flex-align-center mb-2.5 pb-2 border-bottom">
                  <div className="flex-align-center gap-2 min-w-0">
                    <span className="font-bold text-base color-primary">{m.name}</span>
                    <span className="set-currency-chip">{m.currency || 'Bs.S'}</span>
                  </div>
                  <div className="flex-align-center gap-2">
                    <button
                      type="button"
                      className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'} text-xs font-bold px-3 set-state-btn`}
                      onClick={() => handleToggleActive(m)}
                      title="Alternar estado activo"
                      aria-label={`Alternar estado activo para ${m.name}`}
                    >
                      {m.isActive ? 'Activo' : 'Inactivo'}
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-outline text-danger p-1 set-delete-btn"
                      onClick={() => handleDeleteMethod(m)}
                      title="Eliminar método de pago"
                      aria-label={`Eliminar método ${m.name}`}
                    >
                      <Trash2 size={15} />
                    </button>
                  </div>
                </div>
                <div className="flex-between flex-align-center text-xs">
                  <div className="flex-align-center gap-1.5">
                    <span className="text-muted text-xs">Tipo:</span>
                    <button
                      type="button"
                      className={`btn btn-xs ${m.isCash ? 'btn-success' : 'btn-outline'} text-xs font-bold set-chip-btn`}
                      onClick={() => handleToggleCash(m)}
                      style={{
                        backgroundColor: m.isCash ? '#DCFCE7' : 'transparent',
                        color: m.isCash ? '#166534' : 'inherit',
                        borderColor: m.isCash ? '#86EFAC' : 'var(--border)'
                      }}
                      title="Clic para cambiar entre Físico y Digital"
                      aria-label={`Cambiar tipo de ${m.name}. Actualmente ${m.isCash ? 'Físico' : 'Digital'}`}
                    >
                      {m.isCash ? 'Físico' : 'Digital'}
                    </button>
                  </div>
                  <div className="flex-align-center gap-1.5">
                    <span className="text-muted text-xs">Requiere Ref.:</span>
                    <button
                      type="button"
                      className={`btn btn-xs ${m.requiresReference ? 'btn-primary' : 'btn-outline'} text-xs font-bold set-chip-btn`}
                      onClick={() => handleToggleRef(m)}
                      title="Alternar requerimiento de referencia"
                      aria-label={`Alternar requerimiento de referencia para ${m.name}`}
                    >
                      {m.requiresReference ? 'Sí' : 'No'}
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>

          <div className="overflow-x-auto settings-desktop-table-view mb-4">
            <table className="cart-table mb-2">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th className="text-center">Tipo</th>
                  <th className="text-center">Moneda</th>
                  <th className="text-center">Requiere Referencia</th>
                  <th className="text-center">Estado</th>
                  <th className="text-center">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {methods.map((m) => (
                  <tr key={m.id}>
                    <td className="font-medium">{m.name}</td>
                    <td className="text-center">
                      <button
                        type="button"
                        className={`btn btn-sm ${m.isCash ? 'btn-success' : 'btn-outline'} text-xs font-bold set-table-chip`}
                        style={{
                          backgroundColor: m.isCash ? '#DCFCE7' : 'transparent',
                          color: m.isCash ? '#166534' : 'inherit',
                          borderColor: m.isCash ? '#86EFAC' : 'var(--border)'
                        }}
                        onClick={() => handleToggleCash(m)}
                        title="Clic para cambiar entre Físico y Digital"
                        aria-label={`Cambiar tipo de método ${m.name}. Actualmente ${m.isCash ? 'Físico' : 'Digital'}`}
                      >
                        {m.isCash ? 'Físico' : 'Digital'}
                      </button>
                    </td>
                    <td className="text-center">
                      <span className="set-currency-chip">{m.currency || 'Bs.S'}</span>
                    </td>
                    <td className="text-center">
                      <button
                        type="button"
                        className={`btn btn-sm ${m.requiresReference ? 'btn-primary' : 'btn-outline'}`}
                        onClick={() => handleToggleRef(m)}
                        title="Alternar requerimiento de referencia"
                        aria-label={`Alternar requerimiento de referencia para ${m.name}`}
                      >
                        {m.requiresReference ? 'Sí' : 'No'}
                      </button>
                    </td>
                    <td className="text-center">
                      <button
                        type="button"
                        className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'}`}
                        onClick={() => handleToggleActive(m)}
                        title="Alternar activación en POS"
                        aria-label={`Alternar activación para ${m.name}`}
                      >
                        {m.isActive ? 'Activo' : 'Inactivo'}
                      </button>
                    </td>
                    <td className="text-center">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline text-danger p-1.5 set-delete-btn"
                        onClick={() => handleDeleteMethod(m)}
                        title="Eliminar método de pago"
                        aria-label={`Eliminar método ${m.name}`}
                      >
                        <Trash2 size={16} />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      <form onSubmit={handleAddMethod} className="border-top pt-3 mt-2">
        <h4 className="font-bold mb-3 text-sm sm:text-base">Agregar Nuevo Método de Pago</h4>

        <div className="border rounded-lg p-3 mb-3">
          <div className="font-bold text-sm mb-1">Comisión del método (opcional)</div>
          <p className="text-xs text-muted mb-2">
            Es un costo del negocio y no se suma al cliente. El mantenimiento de cuenta y los gastos mensuales del POS no se ingresan acá: se registran como gasto operativo del período.
          </p>
          <div className="form-row align-end flex-wrap gap-3">
            <div className="form-group mb-0">
              <label className="form-label text-xs text-muted mb-1 block">Tipo de comisión</label>
              <select
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
            <div className="form-group mb-0">
              <label className="form-label text-xs text-muted mb-1 block">Porcentaje (%)</label>
              <input
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
            <div className="form-group mb-0">
              <label className="form-label text-xs text-muted mb-1 block">Fijo por transacción</label>
              <input
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
            <div className="form-group mb-0">
              <label className="form-label text-xs text-muted mb-1 block">Moneda de la comisión</label>
              <select
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
        </div>

        <div className="form-row align-end flex-wrap gap-3">
          <div className="form-group flex-2 mb-0 set-form-name">
            <label className="form-label text-xs text-muted mb-1 block">Nombre del Método</label>
            <input
              type="text"
              className="form-input"
              placeholder="Ej. Pago Móvil Banesco"
              value={newMethodName}
              onChange={(e) => setNewMethodName(e.target.value)}
              required
            />
          </div>
          <div className="form-group mb-0 flex-align-center pb-2">
            <label className="form-label cursor-pointer flex-align-center gap-2 text-sm mb-0" title="Desmarcado por defecto: se creará como Digital">
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
          <div className="form-group mb-0 flex-align-center pb-2">
            <label className="form-label cursor-pointer flex-align-center gap-2 text-sm mb-0">
              <input
                type="checkbox"
                checked={newMethodRequiresRef}
                onChange={(e) => setNewMethodRequiresRef(e.target.checked)}
              />
              <span>Req. Referencia</span>
            </label>
          </div>
          <div className="form-group mb-0">
            <button type="submit" className="btn btn-primary flex-center gap-2">
              <Plus size={16} /> Agregar
            </button>
          </div>
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
    </div>
  );
}