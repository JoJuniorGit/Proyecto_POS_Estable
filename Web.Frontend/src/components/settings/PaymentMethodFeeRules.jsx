// F4-WU2 (Paso 14): tarjeta de reglas de comisión de medios de pago. Vive dentro del apartado
// "Métodos de Pago y Caja" de Configuración y es exclusiva de Admin (el endpoint F3 también lo es).
// La comisión es costo del negocio: nunca se agrega al checkout ni al cliente.
import { useEffect, useMemo, useState } from 'react';
import { Loader2, Pencil, Percent, Plus, Trash2 } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { normalizeRole } from '../../navigation/roleViews';
import { formatNumberEs, parseAmount } from '../../utils/formatters';
import {
  PAYMENT_METHOD_FEE_TYPE,
  createFeeRule,
  deleteFeeRule,
  getFeeRules,
  updateFeeRule,
} from '../../services/profitabilityApi';
import ConfirmModal from '../ui/ConfirmModal';
import './PaymentMethodFeeRules.css';

export const FEE_TYPE_OPTIONS = Object.freeze([
  { value: PAYMENT_METHOD_FEE_TYPE.Percentage, label: 'Porcentaje (%)' },
  { value: PAYMENT_METHOD_FEE_TYPE.Fixed, label: 'Monto fijo' },
  { value: PAYMENT_METHOD_FEE_TYPE.PercentagePlusFixed, label: 'Porcentaje + fijo' },
]);

const FEE_TYPE_PERCENTAGE = PAYMENT_METHOD_FEE_TYPE.Percentage;
const FEE_TYPE_FIXED = PAYMENT_METHOD_FEE_TYPE.Fixed;
const FEE_TYPE_COMBINED = PAYMENT_METHOD_FEE_TYPE.PercentagePlusFixed;

export function canManageFeeRules(role) {
  return normalizeRole(role) === 'Admin';
}

export function showFeePercentage(feeType) {
  return Number(feeType) !== FEE_TYPE_FIXED;
}

export function showFeeFixedAmount(feeType) {
  const normalized = Number(feeType);
  return normalized === FEE_TYPE_FIXED || normalized === FEE_TYPE_COMBINED;
}

function parseDecimal(rawValue) {
  const parsed = parseAmount(rawValue);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatPercentageValue(value) {
  const formatted = formatNumberEs(Number(value) || 0, 4)
    .replace(/0+$/, '')
    .replace(/[.,]$/, '');
  return `${formatted || '0'} %`;
}

function formatFixedValue(value) {
  return formatNumberEs(Number(value) || 0, 2);
}

export function formatFeeRuleValue(rule = {}) {
  const feeType = Number(rule.feeType);
  if (feeType === FEE_TYPE_FIXED) {
    return formatFixedValue(rule.fixedAmount);
  }
  if (feeType === FEE_TYPE_COMBINED) {
    return `${formatPercentageValue(rule.percentage)} + ${formatFixedValue(rule.fixedAmount)}`;
  }
  return formatPercentageValue(rule.percentage);
}

export function formatFeeRuleDate(value) {
  if (!value) return '—';
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value));
  return match ? `${match[3]}/${match[2]}/${match[1]}` : '—';
}

export function toFeeRuleDateInput(value) {
  if (!value) return '';
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value));
  return match ? `${match[1]}-${match[2]}-${match[3]}` : '';
}

function todayInputValue() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

function validatePercentage(rawValue) {
  const percentage = parseDecimal(rawValue);
  return percentage > 0 && percentage <= 100;
}

/**
 * Local validation mirroring PaymentMethodFeeRuleService: percentage (0, 100], fixed >= 0,
 * coherent effective range and a non-empty currency. Returns the exact message or null.
 */
export function validateFeeRuleForm(form = {}, options = {}) {
  const { requirePaymentMethod = true } = options;
  const currencyCode = String(form.currencyCode ?? '').trim();
  const effectiveFrom = String(form.effectiveFrom ?? '').trim();
  const effectiveTo = String(form.effectiveTo ?? '').trim();
  const feeType = Number(form.feeType);

  if (!currencyCode) {
    return 'La moneda de la comisión es obligatoria.';
  }

  if (!effectiveFrom) {
    return 'La fecha de inicio de vigencia es obligatoria.';
  }

  if (effectiveTo && effectiveFrom >= effectiveTo) {
    return 'La fecha de inicio de vigencia debe ser anterior a la fecha de fin de vigencia.';
  }

  if (feeType === FEE_TYPE_PERCENTAGE || feeType === FEE_TYPE_COMBINED) {
    if (!validatePercentage(form.percentage)) {
      return 'El porcentaje de comisión debe ser mayor que cero y menor o igual a 100.';
    }
  }

  if (feeType === FEE_TYPE_FIXED) {
    if (parseDecimal(form.fixedAmount) <= 0) {
      return 'El monto fijo de comisión debe ser mayor que cero.';
    }
  }

  if (feeType === FEE_TYPE_COMBINED) {
    const fixedText = String(form.fixedAmount ?? '').trim();
    if (fixedText && parseDecimal(fixedText) < 0) {
      return 'El monto fijo de comisión no puede ser negativo.';
    }
  }

  if (
    feeType !== FEE_TYPE_PERCENTAGE
    && feeType !== FEE_TYPE_FIXED
    && feeType !== FEE_TYPE_COMBINED
  ) {
    return 'El tipo de comisión seleccionado no es válido.';
  }

  if (requirePaymentMethod) {
    const methodId = Number(form.paymentMethodId);
    if (!Number.isInteger(methodId) || methodId <= 0) {
      return 'Seleccione un método de pago para la regla de comisión.';
    }
  }

  return null;
}

export function buildFeeRulePayload(form = {}, options = {}) {
  const { includePaymentMethod = true } = options;
  const feeType = Number(form.feeType);
  const percentage = feeType === FEE_TYPE_FIXED ? null : parseDecimal(form.percentage);
  const fixedAmount = feeType === FEE_TYPE_PERCENTAGE
    ? null
    : (String(form.fixedAmount ?? '').trim() ? parseDecimal(form.fixedAmount) : 0);

  const payload = {
    feeType,
    percentage,
    fixedAmount,
    currencyCode: String(form.currencyCode ?? '').trim(),
    effectiveFromUtc: `${String(form.effectiveFrom).trim()}T00:00:00.000Z`,
    effectiveToUtc: String(form.effectiveTo ?? '').trim()
      ? `${String(form.effectiveTo).trim()}T00:00:00.000Z`
      : null,
    isActive: form.isActive !== false,
  };

  if (includePaymentMethod) {
    payload.paymentMethodId = Number(form.paymentMethodId);
  }

  return payload;
}

function editableDecimal(value) {
  if (value === null || value === undefined || value === '') return '';
  return formatNumberEs(Number(value) || 0, 4)
    .replace(/0+$/, '')
    .replace(/[.,]$/, '');
}

function replaceOrAddRule(rules, saved) {
  const index = rules.findIndex((rule) => rule.id === saved.id);
  if (index === -1) return [...rules, saved];
  const next = [...rules];
  next[index] = saved;
  return next;
}

export default function PaymentMethodFeeRules(props) {
  const { user } = useAuth();
  if (!canManageFeeRules(user?.role)) {
    return null;
  }

  return <PaymentMethodFeeRulesCard {...props} />;
}

function PaymentMethodFeeRulesCard({ methods = [], setMessage }) {
  const { profile } = useMonetaryProfile();
  const [rules, setRules] = useState([]);
  const [loading, setLoading] = useState(true);
  const [status, setStatus] = useState(null);
  const [formOpen, setFormOpen] = useState(false);
  const [editingId, setEditingId] = useState(null);
  const [saving, setSaving] = useState(false);
  const [ruleToDelete, setRuleToDelete] = useState(null);
  const [reloadToken, setReloadToken] = useState(0);
  const [form, setForm] = useState(() => createEmptyForm(methods, profile, null));

  const eligibleMethods = useMemo(
    () => (Array.isArray(methods) ? methods : []).filter(
      (method) => !method.isCash && !method.isDeleted
    ),
    [methods]
  );

  const currencyOptions = useMemo(() => {
    const localCode = profile?.mode === 'Dual'
      ? profile?.secondaryCurrencyCode
      : profile?.primaryCurrencyCode;
    return [...new Set(['USD', localCode].filter(Boolean))];
  }, [profile]);

  useEffect(() => {
    const onFeesUpdated = () => setReloadToken((value) => value + 1);
    window.addEventListener('onPaymentMethodFeesUpdated', onFeesUpdated);
    return () => window.removeEventListener('onPaymentMethodFeesUpdated', onFeesUpdated);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    setLoading(true);
    getFeeRules(null, null, controller.signal)
      .then((data) => {
        if (active) setRules(Array.isArray(data) ? data : []);
      })
      .catch((error) => {
        if (!active || error?.name === 'AbortError') return;
        setStatus({
          type: 'danger',
          text: error?.message || 'No se pudieron cargar las reglas de comisión.',
        });
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [reloadToken]);

  const updateField = (field) => (event) => {
    const value = event.target.type === 'checkbox'
      ? event.target.checked
      : event.target.value;
    setForm((previous) => ({ ...previous, [field]: value }));
  };

  const startNewRule = () => {
    setEditingId(null);
    setForm(createEmptyForm(eligibleMethods, profile, currencyOptions[0]));
    setStatus(null);
    setFormOpen(true);
  };

  const startEditRule = (rule) => {
    setEditingId(rule.id);
    setForm({
      paymentMethodId: rule.paymentMethodId,
      feeType: Number(rule.feeType) || FEE_TYPE_PERCENTAGE,
      percentage: editableDecimal(rule.percentage),
      fixedAmount: editableDecimal(rule.fixedAmount),
      currencyCode: rule.currencyCode || currencyOptions[0] || 'USD',
      effectiveFrom: toFeeRuleDateInput(rule.effectiveFromUtc) || todayInputValue(),
      effectiveTo: toFeeRuleDateInput(rule.effectiveToUtc),
      isActive: rule.isActive !== false,
    });
    setStatus(null);
    setFormOpen(true);
  };

  const cancelEdit = () => {
    setFormOpen(false);
    setEditingId(null);
    setStatus(null);
  };

  const handleSave = async (event) => {
    event.preventDefault();
    if (saving) return;

    const isUpdate = Boolean(editingId);
    const validationError = validateFeeRuleForm(form, {
      requirePaymentMethod: !isUpdate,
    });
    if (validationError) {
      setStatus({ type: 'danger', text: validationError });
      return;
    }

    setSaving(true);
    setStatus(null);
    try {
      const payload = buildFeeRulePayload(form, { includePaymentMethod: !isUpdate });
      const saved = isUpdate
        ? await updateFeeRule(editingId, payload)
        : await createFeeRule(payload);
      setRules((previous) => replaceOrAddRule(previous, saved));
      setFormOpen(false);
      setEditingId(null);
      const message = isUpdate
        ? 'Regla de comisión actualizada correctamente.'
        : 'Regla de comisión guardada correctamente.';
      setStatus({ type: 'success', text: message });
      setMessage?.({ type: 'success', text: message });
    } catch (error) {
      setStatus({
        type: 'danger',
        text: error?.message || 'No se pudo guardar la regla de comisión.',
      });
    } finally {
      setSaving(false);
    }
  };

  const confirmDeleteRule = async () => {
    if (!ruleToDelete) return;
    const target = ruleToDelete;
    setRuleToDelete(null);

    try {
      await deleteFeeRule(target.id);
      setRules((previous) => previous.map((rule) => (
        rule.id === target.id ? { ...rule, isActive: false } : rule
      )));
      const message = 'Regla de comisión desactivada correctamente.';
      setStatus({ type: 'success', text: message });
      setMessage?.({ type: 'success', text: message });
    } catch (error) {
      setStatus({
        type: 'danger',
        text: error?.message || 'No se pudo desactivar la regla de comisión.',
      });
    }
  };

  return (
    <section className="set-card" data-testid="payment-method-fee-rules">
      <header className="set-card-head">
        <span className="set-card-icon" aria-hidden="true">
          <Percent size={18} />
        </span>
        <div className="set-card-heading">
          <h3 className="set-card-title">Comisiones de Medios de Pago</h3>
          <p className="set-card-subtitle">
            Costo del negocio por cobros electrónicos. Nunca se suma al cliente ni al checkout.
          </p>
        </div>
        <div className="set-card-head-actions">
          <span className="set-status-pill set-status-pill--neutral">
            {rules.length} {rules.length === 1 ? 'regla' : 'reglas'}
          </span>
          <button
            type="button"
            className="btn btn-primary btn-sm"
            onClick={startNewRule}
            disabled={eligibleMethods.length === 0}
          >
            <Plus size={16} /> Nueva Regla
          </button>
        </div>
      </header>

      {status && (
        <div className={`set-alert set-alert--${status.type}`} role="status">
          {status.text}
        </div>
      )}

      {formOpen && (
        <form className="set-form-panel" onSubmit={handleSave}>
          <div className="set-form-head">
            <h4 className="set-form-title">
              {editingId ? 'Editar Regla de Comisión' : 'Nueva Regla de Comisión'}
            </h4>
            <p className="set-form-subtitle">
              La comisión se registra como costo del negocio y no modifica el precio cobrado al cliente.
            </p>
          </div>

          <div className="set-callout set-callout--info">
            <div>
              <span className="set-callout-title">Vigencia y moneda</span>
              <p>
                Defina el rango de fechas y la moneda en la que se expresa la comisión.
                Los cobros históricos conservan la regla que tenían aplicada.
              </p>
            </div>
          </div>

          <div className="set-form-grid set-form-grid--fees">
            <div className="form-group">
              <label className="form-label" htmlFor="pmf-payment-method">Método de pago</label>
              <select
                id="pmf-payment-method"
                className="form-input"
                value={form.paymentMethodId}
                onChange={updateField('paymentMethodId')}
                disabled={Boolean(editingId)}
                required
              >
                <option value="">Seleccione...</option>
                {eligibleMethods.map((method) => (
                  <option key={method.id} value={method.id}>{method.name}</option>
                ))}
              </select>
            </div>

            <div className="form-group">
              <label className="form-label" htmlFor="pmf-fee-type">Tipo de comisión</label>
              <select
                id="pmf-fee-type"
                className="form-input"
                value={form.feeType}
                onChange={updateField('feeType')}
              >
                {FEE_TYPE_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </div>

            {showFeePercentage(form.feeType) && (
              <div className="form-group">
                <label className="form-label" htmlFor="pmf-percentage">Porcentaje (%)</label>
                <input
                  id="pmf-percentage"
                  type="text"
                  inputMode="decimal"
                  className="form-input"
                  placeholder="Ej: 1,5"
                  value={form.percentage}
                  onChange={updateField('percentage')}
                />
              </div>
            )}

            {showFeeFixedAmount(form.feeType) && (
              <div className="form-group">
                <label className="form-label" htmlFor="pmf-fixed">Monto fijo</label>
                <input
                  id="pmf-fixed"
                  type="text"
                  inputMode="decimal"
                  className="form-input"
                  placeholder="Ej: 0,30"
                  value={form.fixedAmount}
                  onChange={updateField('fixedAmount')}
                />
              </div>
            )}

            <div className="form-group">
              <label className="form-label" htmlFor="pmf-currency">Moneda</label>
              <select
                id="pmf-currency"
                className="form-input"
                value={form.currencyCode}
                onChange={updateField('currencyCode')}
              >
                {currencyOptions.map((code) => (
                  <option key={code} value={code}>{code}</option>
                ))}
              </select>
            </div>

            <div className="form-group">
              <label className="form-label" htmlFor="pmf-from">Vigente desde</label>
              <input
                id="pmf-from"
                type="date"
                className="form-input"
                value={form.effectiveFrom}
                onChange={updateField('effectiveFrom')}
                required
              />
            </div>

            <div className="form-group">
              <label className="form-label" htmlFor="pmf-to">Vigente hasta</label>
              <input
                id="pmf-to"
                type="date"
                className="form-input"
                value={form.effectiveTo}
                onChange={updateField('effectiveTo')}
              />
            </div>

            <div className="set-check-field">
              <label className="set-check-label">
                <input
                  id="pmf-active"
                  type="checkbox"
                  checked={form.isActive}
                  onChange={updateField('isActive')}
                />
                <span>Activa</span>
              </label>
            </div>
          </div>

          <div className="set-form-actions">
            <button type="button" className="btn btn-outline btn-sm" onClick={cancelEdit}>
              Cancelar
            </button>
            <button type="submit" className="btn btn-primary btn-sm" disabled={saving}>
              {saving ? <Loader2 size={16} className="animate-spin" /> : null}
              Guardar Regla
            </button>
          </div>
        </form>
      )}

      <div className="settings-mobile-cards-view set-fee-mobile-list">
        {loading ? (
          <div className="set-loading">
            <Loader2 size={18} className="animate-spin" />
            <span>Cargando reglas de comisión...</span>
          </div>
        ) : rules.length === 0 ? (
          <div className="set-fee-mobile-empty">No hay reglas de comisión configuradas.</div>
        ) : (
          rules.map((rule) => (
            <article
              key={rule.id}
              className={`set-fee-mobile-card${rule.isActive === false ? ' is-inactive' : ''}`}
            >
              <div className="set-fee-mobile-head">
                <span className="set-fee-mobile-name">{rule.paymentMethodName}</span>
                <span className={`set-chip set-chip--static ${rule.isActive === false ? 'set-chip--danger' : 'set-chip--success'}`}>
                  {rule.isActive === false ? 'Inactiva' : 'Activa'}
                </span>
              </div>
              <div className="set-fee-mobile-grid">
                <div>
                  <span>Tipo</span>
                  <strong>{FEE_TYPE_OPTIONS.find((option) => option.value === Number(rule.feeType))?.label || '—'}</strong>
                </div>
                <div>
                  <span>Valor</span>
                  <strong className="font-mono">{formatFeeRuleValue(rule)}</strong>
                </div>
                <div>
                  <span>Moneda</span>
                  <strong>{rule.currencyCode}</strong>
                </div>
                <div>
                  <span>Desde</span>
                  <strong>{formatFeeRuleDate(rule.effectiveFromUtc)}</strong>
                </div>
                <div>
                  <span>Hasta</span>
                  <strong>{formatFeeRuleDate(rule.effectiveToUtc)}</strong>
                </div>
              </div>
              <div className="set-row-actions">
                <button
                  type="button"
                  className="set-icon-btn"
                  onClick={() => startEditRule(rule)}
                  aria-label={`Editar regla de comisión de ${rule.paymentMethodName}`}
                  title="Editar regla de comisión"
                >
                  <Pencil size={15} />
                </button>
                <button
                  type="button"
                  className="set-icon-btn set-icon-btn--danger"
                  onClick={() => setRuleToDelete(rule)}
                  disabled={rule.isActive === false}
                  aria-label={`Desactivar regla de comisión de ${rule.paymentMethodName}`}
                  title="Desactivar regla de comisión"
                >
                  <Trash2 size={15} />
                </button>
              </div>
            </article>
          ))
        )}
      </div>

      <div className="set-table-wrap pmf-table-wrap settings-desktop-table-view">
        <table className="set-table set-table--fees">
          <thead>
            <tr>
              <th>Método</th>
              <th>Tipo</th>
              <th>Valor</th>
              <th>Moneda</th>
              <th>Desde</th>
              <th>Hasta</th>
              <th>Estado</th>
              <th>Acciones</th>
            </tr>
          </thead>
          <tbody>
            {loading ? (
              <tr>
                <td colSpan={8} className="set-empty-cell">
                  <Loader2 size={18} className="animate-spin inline-block mr-2" />
                  Cargando reglas de comisión...
                </td>
              </tr>
            ) : rules.length === 0 ? (
              <tr>
                <td colSpan={8} className="set-empty-cell">
                  No hay reglas de comisión configuradas.
                </td>
              </tr>
            ) : (
              rules.map((rule) => (
                <tr key={rule.id} className={rule.isActive === false ? 'set-row-inactive' : ''}>
                  <td className="font-medium">{rule.paymentMethodName}</td>
                  <td>{FEE_TYPE_OPTIONS.find((option) => option.value === Number(rule.feeType))?.label || '—'}</td>
                  <td className="font-mono">{formatFeeRuleValue(rule)}</td>
                  <td>{rule.currencyCode}</td>
                  <td>{formatFeeRuleDate(rule.effectiveFromUtc)}</td>
                  <td>{formatFeeRuleDate(rule.effectiveToUtc)}</td>
                  <td>
                    <span className={`set-chip set-chip--static ${rule.isActive === false ? 'set-chip--danger' : 'set-chip--success'}`}>
                      {rule.isActive === false ? 'Inactiva' : 'Activa'}
                    </span>
                  </td>
                  <td>
                    <div className="set-row-actions pmf-row-actions">
                      <button
                        type="button"
                        className="set-icon-btn"
                        onClick={() => startEditRule(rule)}
                        aria-label={`Editar regla de comisión de ${rule.paymentMethodName}`}
                        title="Editar regla de comisión"
                      >
                        <Pencil size={15} />
                      </button>
                      <button
                        type="button"
                        className="set-icon-btn set-icon-btn--danger"
                        onClick={() => setRuleToDelete(rule)}
                        disabled={rule.isActive === false}
                        aria-label={`Desactivar regla de comisión de ${rule.paymentMethodName}`}
                        title="Desactivar regla de comisión"
                      >
                        <Trash2 size={15} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      <ConfirmModal
        isOpen={Boolean(ruleToDelete)}
        onClose={() => setRuleToDelete(null)}
        onConfirm={confirmDeleteRule}
        title="¿Desactivar regla de comisión?"
        message={`La regla de comisión de "${ruleToDelete?.paymentMethodName}" quedará inactiva. Los cobros históricos conservan su comisión registrada.`}
        confirmText="Desactivar Regla"
        cancelText="Cancelar"
        variant="danger"
      />
    </section>
  );
}

function createEmptyForm(methods = [], profile = null, fallbackCurrency = null) {
  const localCode = profile?.mode === 'Dual'
    ? profile?.secondaryCurrencyCode
    : profile?.primaryCurrencyCode;
  const currency = fallbackCurrency || ['USD', localCode].filter(Boolean)[0] || 'USD';
  return {
    paymentMethodId: methods[0]?.id ?? '',
    feeType: FEE_TYPE_PERCENTAGE,
    percentage: '',
    fixedAmount: '',
    currencyCode: currency,
    effectiveFrom: todayInputValue(),
    effectiveTo: '',
    isActive: true,
  };
}
