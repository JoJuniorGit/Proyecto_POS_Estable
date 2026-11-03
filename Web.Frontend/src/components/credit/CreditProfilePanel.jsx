import { useCallback, useEffect, useId, useState } from 'react';
import { getCreditProfile, updateCreditProfile } from '../../services/creditApi';
import {
  CREDIT_STATUS,
  creditStatusLabel,
  validateCreditProfileUpdate,
  canManageCreditProfileForm,
} from './creditLogic';
import { parseAmount, formatNumberEs } from '../../utils/formatters';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { resolveLocalCurrencyProfile } from '../register/registerFormLogic';
import { AlertCircle, CheckCircle2, Loader2, Lock, Unlock, Wallet } from 'lucide-react';

/**
 * 8.187-T5 (S1/S4): panel del perfil crediticio del cliente para el POS web (paridad con el
 * editor WPF de T4). Admin/Manager editan habilitación, cupo, plazo, gracia, notas y el bloqueo
 * manual (Active / ManuallyBlocked); el cajero solo lee. El estado Suspendido por Mora es
 * administrado por el servidor y no se edita acá. El disponible/utilización/mora los calcula
 * el servidor: este panel solo los presenta.
 */

/** Profundidad máxima que acepta el contrato del backend. */
const NOTES_MAX_LENGTH = 1000;

/**
 * 8.211-T4b2b2b (S9): etiquetas del perfil monetario activo para el panel crediticio. La deuda
 * se denomina en la primaria; la local sólo existe en `Dual`.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 */
export function resolveCreditCurrencyLabels(profile) {
  const local = resolveLocalCurrencyProfile(profile);
  return {
    isDual: local.isDual,
    primaryLabel: displayCurrencyCode(profile?.primaryCurrencyCode) || 'USD',
    localLabel: local.label || 'Bs.S',
  };
}

function profileToForm(profile) {
  return {
    isCreditEnabled: profile?.isCreditEnabled === true,
    creditLimitUSD: profile ? String(profile.creditLimitUSD) : '',
    paymentTermDays: profile ? String(profile.paymentTermDays) : '30',
    creditGraceDays: profile ? String(profile.creditGraceDays) : '0',
    creditNotes: profile?.creditNotes ?? '',
    creditStatus: Number(profile?.creditStatus) === CREDIT_STATUS.MANUALLY_BLOCKED
      ? CREDIT_STATUS.MANUALLY_BLOCKED
      : CREDIT_STATUS.ACTIVE,
  };
}

export default function CreditProfilePanel({ customer, canEdit = false }) {
  const uid = useId();
  const { profile: monetaryProfile, formatPrimary } = useMonetaryProfile();
  const { primaryLabel } = resolveCreditCurrencyLabels(monetaryProfile);
  const [profile, setProfile] = useState(null);
  const [form, setForm] = useState(null);
  // El perfil llega por GET al montar: sin datos aún, el panel arranca en estado de consulta.
  const [isLoading, setIsLoading] = useState(() => Boolean(customer?.id) && customer?.isDefault !== true);
  const [loadError, setLoadError] = useState(null);
  const [isSaving, setIsSaving] = useState(false);
  const [saveError, setSaveError] = useState(null);
  const [saveOk, setSaveOk] = useState(null);

  const isDefaultCustomer = customer?.isDefault === true;
  const isSystemSuspended = profile != null && Number(profile.creditStatus) === CREDIT_STATUS.SUSPENDED_BY_MORA;
  // 8.188: el formulario se reserva a Admin/Manager con perfil cargado y sin mora de sistema;
  // para el cajero no se renderiza (oculto por completo, no deshabilitado).
  const isEditable = canManageCreditProfileForm({ canEdit, isDefaultCustomer, profile, isSystemSuspended });

  const loadProfile = useCallback(async (customerId) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const data = await getCreditProfile(customerId);
      setProfile(data);
      setForm(profileToForm(data));
      setSaveError(null);
      setSaveOk(null);
    } catch (err) {
      setProfile(null);
      setForm(null);
      setLoadError(err?.message || 'No se pudo cargar el perfil crediticio del cliente.');
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!customer?.id || isDefaultCustomer) {
      setProfile(null);
      setForm(null);
      setLoadError(null);
      setIsLoading(false);
      return;
    }
    loadProfile(customer.id);
  }, [customer?.id, isDefaultCustomer, loadProfile]);

  const handleSave = async (event) => {
    event.preventDefault();
    if (!form || isSaving || !isEditable) return;

    setSaveError(null);
    setSaveOk(null);

    const payload = {
      isCreditEnabled: form.isCreditEnabled,
      creditLimitUSD: parseAmount(form.creditLimitUSD),
      paymentTermDays: Number(form.paymentTermDays),
      creditGraceDays: Number(form.creditGraceDays),
      creditNotes: form.creditNotes,
      creditStatus: form.creditStatus,
    };

    const errors = validateCreditProfileUpdate(payload);
    if (errors.length > 0) {
      setSaveError(errors[0]);
      return;
    }

    setIsSaving(true);
    try {
      const updated = await updateCreditProfile(customer.id, payload);
      setProfile(updated);
      setForm(profileToForm(updated));
      setSaveOk('Perfil crediticio actualizado correctamente.');
    } catch (err) {
      setSaveError(err?.message || 'No se pudo actualizar el perfil crediticio.');
    } finally {
      setIsSaving(false);
    }
  };

  const setManualStatus = (status) => {
    setForm((prev) => (prev ? { ...prev, creditStatus: status } : prev));
    setSaveError(null);
    setSaveOk(null);
  };

  return (
    <section className="credit-profile-panel" aria-label="Perfil crediticio del cliente">
      <div className="credit-profile-head">
        <h4>
          <Wallet size={16} /> Perfil de Crédito
        </h4>
        {profile && (
          <span className={`credit-status-badge${Number(profile.creditStatus) === CREDIT_STATUS.ACTIVE && profile.isCreditEnabled ? ' credit-status-badge--vigente' : ''}`}>
            {creditStatusLabel(profile.creditStatus)}
          </span>
        )}
      </div>

      {isDefaultCustomer ? (
        <p className="credit-profile-readonly-note">
          No se puede configurar crédito para el cliente Consumidor Final.
        </p>
      ) : isLoading && !profile ? (
        <p className="credit-profile-readonly-note" role="status">
          <Loader2 size={14} className="animate-spin" /> Cargando perfil crediticio...
        </p>
      ) : loadError ? (
        <div className="alert alert-danger credit-profile-alert" role="alert">
          <AlertCircle size={16} /> {loadError}
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={() => customer?.id && loadProfile(customer.id)}
          >
            Reintentar
          </button>
        </div>
      ) : profile ? (
        <>
          <div className="credit-profile-grid">
            <div className="credit-profile-stat">
              <span className="credit-profile-stat-label">Cupo autorizado</span>
              <span className="credit-profile-stat-value">{formatPrimary(profile.creditLimitUSD)}</span>
            </div>
            <div className="credit-profile-stat">
              <span className="credit-profile-stat-label">Deuda actual</span>
              <span className="credit-profile-stat-value">{formatPrimary(profile.currentDebtUSD)}</span>
            </div>
            <div className="credit-profile-stat">
              <span className="credit-profile-stat-label">Crédito disponible</span>
              <span className={`credit-profile-stat-value${Number(profile.availableCreditUSD) < 0 ? ' credit-profile-stat-value--danger' : ''}`}>
                {formatPrimary(profile.availableCreditUSD)}
              </span>
            </div>
            <div className="credit-profile-stat">
              <span className="credit-profile-stat-label">Utilización</span>
              <span className="credit-profile-stat-value">{formatNumberEs(profile.utilizationPercent, 2)}%</span>
            </div>
            <div className="credit-profile-stat">
              <span className="credit-profile-stat-label">Mora</span>
              <span className={`credit-profile-stat-value${Number(profile.overdueUSD) > 0 ? ' credit-profile-stat-value--danger' : ''}`}>
                {formatPrimary(profile.overdueUSD)} ({profile.overdueCount})
              </span>
            </div>
            <div className="credit-profile-stat">
              <span className="credit-profile-stat-label">Plazo / gracia</span>
              <span className="credit-profile-stat-value">{profile.paymentTermDays} / {profile.creditGraceDays} días</span>
            </div>
          </div>

          {!profile.isCreditEnabled && (
            <div className="alert alert-warning credit-profile-alert" role="status">
              La línea de crédito está deshabilitada para este cliente.
            </div>
          )}

          {isSystemSuspended ? (
            <p className="credit-profile-readonly-note">
              El crédito está suspendido por mora y es administrado por el sistema. Regularice la deuda para rehabilitar la línea.
            </p>
          ) : isEditable ? (
            <form className="credit-profile-form" onSubmit={handleSave}>
              {saveError && (
                <div className="alert alert-danger credit-profile-alert" role="alert">
                  <AlertCircle size={16} /> {saveError}
                </div>
              )}
              {saveOk && (
                <div className="alert alert-success credit-profile-alert" role="status">
                  <CheckCircle2 size={16} /> {saveOk}
                </div>
              )}

              <label className="credit-toolbar-check" htmlFor={`${uid}-enabled`}>
                <input
                  id={`${uid}-enabled`}
                  type="checkbox"
                  checked={form.isCreditEnabled}
                  onChange={(e) => setForm((prev) => ({ ...prev, isCreditEnabled: e.target.checked }))}
                  disabled={isSaving}
                />
                Línea de crédito habilitada
              </label>

              <div className="credit-profile-form-grid">
                <div className="form-group mb-0">
                  <label htmlFor={`${uid}-limit`}>Cupo autorizado ({primaryLabel})</label>
                  <input
                    id={`${uid}-limit`}
                    type="text"
                    inputMode="decimal"
                    className="form-control"
                    value={form.creditLimitUSD}
                    onChange={(e) => setForm((prev) => ({ ...prev, creditLimitUSD: e.target.value }))}
                    disabled={isSaving}
                  />
                </div>
                <div className="form-group mb-0">
                  <label htmlFor={`${uid}-term`}>Plazo de pago (días)</label>
                  <input
                    id={`${uid}-term`}
                    type="number"
                    min={1}
                    max={365}
                    className="form-control"
                    value={form.paymentTermDays}
                    onChange={(e) => setForm((prev) => ({ ...prev, paymentTermDays: e.target.value }))}
                    disabled={isSaving}
                  />
                </div>
                <div className="form-group mb-0">
                  <label htmlFor={`${uid}-grace`}>Días de gracia</label>
                  <input
                    id={`${uid}-grace`}
                    type="number"
                    min={0}
                    className="form-control"
                    value={form.creditGraceDays}
                    onChange={(e) => setForm((prev) => ({ ...prev, creditGraceDays: e.target.value }))}
                    disabled={isSaving}
                  />
                </div>
              </div>

              <div className="form-group mb-0 credit-profile-notes">
                <label htmlFor={`${uid}-notes`}>Notas crediticias (garantías, acuerdos)</label>
                <textarea
                  id={`${uid}-notes`}
                  className="form-control"
                  maxLength={NOTES_MAX_LENGTH}
                  value={form.creditNotes}
                  onChange={(e) => setForm((prev) => ({ ...prev, creditNotes: e.target.value }))}
                  disabled={isSaving}
                />
                <small className="form-text text-muted">{form.creditNotes.length} / {NOTES_MAX_LENGTH}</small>
              </div>

              <div>
                <span className="credit-profile-stat-label">Bloqueo manual</span>
                <div className="credit-profile-status-buttons" role="group" aria-label="Bloqueo manual del crédito">
                  <button
                    type="button"
                    className={`btn ${form.creditStatus === CREDIT_STATUS.ACTIVE ? 'btn-primary' : 'btn-outline'}`}
                    onClick={() => setManualStatus(CREDIT_STATUS.ACTIVE)}
                    aria-pressed={form.creditStatus === CREDIT_STATUS.ACTIVE}
                    disabled={isSaving}
                  >
                    <Unlock size={14} /> Activo
                  </button>
                  <button
                    type="button"
                    className={`btn ${form.creditStatus === CREDIT_STATUS.MANUALLY_BLOCKED ? 'btn-danger' : 'btn-outline'}`}
                    onClick={() => setManualStatus(CREDIT_STATUS.MANUALLY_BLOCKED)}
                    aria-pressed={form.creditStatus === CREDIT_STATUS.MANUALLY_BLOCKED}
                    disabled={isSaving}
                  >
                    <Lock size={14} /> Bloqueado Manualmente
                  </button>
                </div>
              </div>

              <div className="credit-profile-actions">
                <button type="submit" className="btn btn-primary" disabled={isSaving}>
                  {isSaving ? <Loader2 size={16} className="animate-spin" /> : <CheckCircle2 size={16} />} Guardar Perfil
                </button>
              </div>
            </form>
          ) : (
            <p className="credit-profile-readonly-note">
              {canEdit
                ? 'Seleccione un cliente para ver su perfil crediticio.'
                : 'Solo Admin/Manager pueden modificar el perfil crediticio.'}
            </p>
          )}
        </>
      ) : null}
    </section>
  );
}
