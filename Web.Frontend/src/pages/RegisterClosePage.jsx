import { useState, useEffect, useCallback } from 'react';
import { getActivePaymentMethods } from '../services/paymentApi';
import { api } from '../services/api';
import { closeShift, getCurrentShiftReport } from '../services/shiftApi';
import { useAuth } from '../context/AuthContext';
import { formatBsS, formatUSD, formatNumberEs } from '../utils/formatters';
import AtmAmountInput from '../components/ui/AtmAmountInput';
import Modal from '../components/ui/Modal';
import {
  Lock,
  User,
  Calendar,
  DollarSign,
  AlertTriangle,
  CheckCircle,
  Printer,
  LogOut,
  Loader2,
  ShieldAlert,
  FileText
} from 'lucide-react';

export default function RegisterClosePage() {
  const { user, logout } = useAuth();

  const [methods, setMethods] = useState([]);
  const [exchangeRate, setExchangeRate] = useState(0);
  const [declaredAmounts, setDeclaredAmounts] = useState({});
  const [loading, setLoading] = useState(true);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState(null);

  // Modal states
  const [isZeroConfirmOpen, setIsZeroConfirmOpen] = useState(false);
  const [isPreSendModalOpen, setIsPreSendModalOpen] = useState(false);

  // Post-close Z Report state
  const [zReport, setZReport] = useState(null);
  const [recoveryNotice, setRecoveryNotice] = useState(null);

  // Utility to determine currency for a payment method
  const getMethodCurrency = useCallback((method) => {
    const name = (method?.name || '').toLowerCase();
    if (name.includes('usd') || name.includes('dolar') || name.includes('$') || name.includes('divisa')) {
      return 'USD';
    }
    return 'Bs.S';
  }, []);

  // 1. Initial Fetch (Parallel API calls)
  const loadInitialData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [methodsData, rateData] = await Promise.all([
        getActivePaymentMethods(),
        api.get('/api/exchange-rate/today').catch(() => ({ Value: 0 })),
      ]);

      setMethods(methodsData || []);
      setExchangeRate(rateData?.Value || rateData?.value || 0);

      // Initialize declared amounts map
      const initialMap = {};
      (methodsData || []).forEach((m) => {
        initialMap[m.id] = 0;
      });
      setDeclaredAmounts(initialMap);
    } catch (err) {
      console.error('[RegisterClosePage] Error cargando datos iniciales:', err);
      setError('No se pudieron cargar los métodos de pago o la tasa del día.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadInitialData();
  }, [loadInitialData]);

  // 2. Higiene de Estado: Cleanup function on unmount
  useEffect(() => {
    return () => {
      setDeclaredAmounts({});
      setError(null);
    };
  }, []);

  const handleAmountChange = (methodId, numericValue) => {
    setDeclaredAmounts((prev) => ({
      ...prev,
      [methodId]: numericValue,
    }));
  };

  // Check if all declared amounts are zero
  const isAllZeroes = () => {
    return Object.values(declaredAmounts).every((val) => !val || val === 0);
  };

  // Step 1: Pre-submit trigger
  const handleProcessClick = (e) => {
    e.preventDefault();
    setError(null);

    if (isAllZeroes()) {
      setIsZeroConfirmOpen(true);
    } else {
      setIsPreSendModalOpen(true);
    }
  };

  const handleConfirmZeroes = () => {
    setIsZeroConfirmOpen(false);
    setIsPreSendModalOpen(true);
  };

  // Step 2: Final Submit to Backend
  const handleFinalSubmit = async () => {
    setIsPreSendModalOpen(false);
    setIsSubmitting(true);
    setError(null);

    // Build payload strictly in native currency per method
    const payloadAmounts = methods.map((m) => {
      const curr = getMethodCurrency(m);
      const amt = declaredAmounts[m.id] || 0;
      return {
        paymentMethodId: m.id,
        paymentMethodName: m.name,
        amount: amt,
        currency: curr,
      };
    });

    try {
      const report = await closeShift(
        payloadAmounts,
        user?.name || 'Cajero Activo',
        user?.cedula || 'V-00000000'
      );
      setZReport(report);
    } catch (err) {
      console.error('[RegisterClosePage] Error enviando cierre:', err);
      const msg = err.response?.data?.Message || err.message || '';
      
      // Idempotency / Recovery: If shift is already closed, try to recover existing Z Report
      if (msg.toLowerCase().includes('cerrado') || err.response?.status === 400 || err.response?.status === 409) {
        try {
          const recovered = await getCurrentShiftReport();
          setZReport(recovered);
          setRecoveryNotice('El turno actual ya había sido cerrado. Se ha recuperado el Reporte Z oficial.');
          return;
        } catch (recErr) {
          console.error('[RegisterClosePage] Error recuperando reporte:', recErr);
        }
      }

      // Network / Timeout error: keep form intact for retry
      setError(msg || 'Ocurrió un error al enviar el cierre de caja. Los montos ingresados se mantienen para reintentar.');
    } finally {
      setIsSubmitting(false);
    }
  };

  // Final logout action
  const handleFinalLogout = () => {
    logout();
    window.location.hash = 'login';
  };

  // If Z Report is ready (Successful close or recovery), render Report Z View
  if (zReport) {
    return (
      <div className="register-close-container printable-area" style={{ maxWidth: '850px', margin: '0 auto', padding: '16px' }}>
        {recoveryNotice && (
          <div className="alert alert-warning mb-4 flex-align-center gap-2 no-print">
            <ShieldAlert size={20} className="flex-shrink-0" />
            <span>{recoveryNotice}</span>
          </div>
        )}

        <div className="alert alert-info mb-4 flex-align-center gap-2 no-print" style={{ backgroundColor: 'rgba(99, 102, 241, 0.12)', borderColor: 'var(--accent-primary)', color: 'var(--text-primary)' }}>
          <CheckCircle size={18} className="flex-shrink-0" style={{ color: 'var(--accent-primary)' }} />
          <span style={{ fontSize: '0.875rem' }}>
            Comprobantes guardados automáticamente en las carpetas <strong>Descargas</strong> y <strong>Documentos\Registro de cierres</strong> del servidor local.
          </span>
        </div>

        <div className="card print-clean-card mb-4 p-3 sm:p-4">
          
          {/* Encabezado del Reporte Z */}
          <div className="text-center border-bottom pb-3 mb-3 zreport-header-box">
            <div className="flex-center gap-2 mb-1 flex-wrap">
              <FileText size={26} className="color-primary flex-shrink-0" />
              <h2 className="font-bold text-lg sm:text-2xl zreport-title" style={{ margin: 0 }}>
                REPORTE Z — CIERRE DE CAJA
              </h2>
            </div>
            <p className="text-muted text-xs sm:text-sm" style={{ margin: 0 }}>Comprobante Oficial de Arqueo y Descuadre de Caja</p>
          </div>

          {/* Bloque de Datos del Cajero y Turno */}
          <div className="grid grid-1 sm:grid-2 gap-2 mb-4 p-2.5 sm:p-3 rounded zreport-meta-box" style={{ backgroundColor: 'var(--bg-tertiary, rgba(128,128,128,0.1))', fontSize: '0.85rem' }}>
            <div className="zreport-meta-group">
              <div><strong>N° de Turno:</strong> #{zReport.shiftId || 'Z-001'}</div>
              <div><strong>Cajero:</strong> {zReport.cashierName || user?.name || 'Cajero'}</div>
              <div><strong>Cédula:</strong> {zReport.cashierCedula || user?.cedula || 'V-00000000'}</div>
            </div>
            <div className="zreport-meta-group sm:text-right">
              <div><strong>Fecha:</strong> {zReport.closedAt ? new Date(zReport.closedAt).toLocaleString('es-VE') : new Date().toLocaleString('es-VE')}</div>
              <div><strong>Tasa del Día:</strong> <span className="font-mono font-bold">{formatNumberEs(zReport.exchangeRate || exchangeRate)} Bs/$</span></div>
            </div>
          </div>

          {/* Vista de Tarjetas Responsivas para Móvil (Reporte Z) */}
          <div className="zreport-mobile-cards-view mb-4">
            {(zReport.details || []).map((d, idx) => {
              const isSurplus = d.difference > 0.05;
              const isShortage = d.difference < -0.05;
              const statusText = isSurplus ? 'Sobrante' : isShortage ? 'Faltante' : 'Cuadrado';
              const statusBadgeClass = isSurplus ? 'badge-success' : isShortage ? 'badge-danger' : 'badge-outline';
              const diffColor = isSurplus ? '#22c55e' : isShortage ? '#ef4444' : '#22c55e';
              const formatVal = (val, curr) => (curr === 'USD' ? formatUSD(val) : formatBsS(val));

              return (
                <div key={idx} className="zreport-mobile-card p-3.5 mb-3 border rounded-lg bg-surface shadow-xs" style={{ border: '1px solid var(--border)', borderRadius: '12px' }}>
                  
                  {/* Encabezado de la tarjeta: Nombre a la izquierda, Badge de moneda a su lado */}
                  <div className="flex-between flex-align-center mb-3 pb-2 border-bottom">
                    <div className="flex-align-center gap-2">
                      <span className="font-bold text-base color-primary">{d.paymentMethodName}</span>
                      <span className={`register-currency-badge ${d.currency === 'USD' ? 'currency-badge-usd' : 'currency-badge-bss'}`}>
                        {d.currency}
                      </span>
                    </div>
                    <span className={`badge ${statusBadgeClass} text-xs font-bold px-2.5 py-1`}>
                      {statusText}
                    </span>
                  </div>

                  {/* Cuerpo de la tarjeta: Los 3 montos apilados en lista con etiquetas contextuales */}
                  <div className="zreport-mobile-card-amounts flex flex-column gap-2 text-xs">
                    <div className="flex-between flex-align-center">
                      <span className="text-muted text-xs">Declarado:</span>
                      <span className="font-mono font-bold text-sm">{formatVal(d.declaredAmount, d.currency)}</span>
                    </div>

                    <div className="flex-between flex-align-center">
                      <span className="text-muted text-xs">Sistema:</span>
                      <span className="font-mono text-muted text-sm">{formatVal(d.systemAmount, d.currency)}</span>
                    </div>

                    <div className="flex-between flex-align-center pt-2 border-top mt-1">
                      <span className="text-muted font-medium text-xs">Diferencia:</span>
                      <span className="font-mono font-bold text-sm" style={{ color: diffColor }}>
                        {formatVal(d.difference, d.currency)}
                      </span>
                    </div>
                  </div>

                </div>
              );
            })}
          </div>

          {/* Tabla para Vista Escritorio */}
          <div className="overflow-x-auto border rounded mb-4 custom-scrollbar zreport-desktop-table-view">
            <table className="cart-table" style={{ width: '100%', minWidth: '600px', fontSize: '0.9rem' }}>
              <thead>
                <tr>
                  <th style={{ whiteSpace: 'nowrap' }}>Método de Pago</th>
                  <th className="text-center" style={{ whiteSpace: 'nowrap' }}>Moneda</th>
                  <th className="text-right" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>Monto Declarado</th>
                  <th className="text-right" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>Monto Sistema</th>
                  <th className="text-right" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>Diferencia</th>
                </tr>
              </thead>
              <tbody>
                {(zReport.details || []).map((d, idx) => {
                  const isSurplus = d.difference > 0.05;
                  const isShortage = d.difference < -0.05;
                  const statusText = isSurplus ? 'Sobrante' : isShortage ? 'Faltante' : 'Cuadrado';

                  const diffColor = isSurplus
                    ? '#22c55e'
                    : isShortage
                    ? '#ef4444'
                    : '#22c55e';

                  const formatVal = (val, curr) => (curr === 'USD' ? formatUSD(val) : formatBsS(val));

                  return (
                    <tr key={idx}>
                      <td className="font-bold" style={{ whiteSpace: 'nowrap' }}>{d.paymentMethodName}</td>
                      <td className="text-center font-mono" style={{ whiteSpace: 'nowrap' }}>
                        <span className={`register-currency-badge ${d.currency === 'USD' ? 'currency-badge-usd' : 'currency-badge-bss'}`}>
                          {d.currency}
                        </span>
                      </td>
                      <td className="text-right font-mono" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>{formatVal(d.declaredAmount, d.currency)}</td>
                      <td className="text-right font-mono text-muted" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>{formatVal(d.systemAmount, d.currency)}</td>
                      <td className="text-right font-mono font-bold" style={{ color: diffColor, whiteSpace: 'nowrap', paddingRight: '14px' }}>
                        {formatVal(d.difference, d.currency)}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* Botones de Acción (Full Width en Móvil con Margen Generoso) */}
          <div className="no-print zreport-actions-container pt-4 mt-4 border-top flex flex-column gap-3">
            <button
              type="button"
              className="btn btn-primary btn-lg w-full flex-center gap-2 zreport-btn-logout"
              onClick={handleFinalLogout}
              style={{ width: '100%', height: '48px', fontSize: '1rem', fontWeight: 'bold' }}
            >
              <LogOut size={20} /> Finalizar y Cerrar Sesión
            </button>

            <button
              type="button"
              className="btn btn-outline w-full flex-center gap-2 zreport-btn-print"
              onClick={() => window.print()}
              style={{ width: '100%', height: '44px', fontSize: '0.9rem' }}
            >
              <Printer size={18} /> Imprimir Reporte Z (Papel / PDF)
            </button>
          </div>

        </div>
      </div>
    );
  }

  // Active Blind Close Form View
  return (
    <div className="register-close-container" style={{ maxWidth: '800px', margin: '0 auto', padding: '16px' }}>
      <div className="page-header flex-between mb-4">
        <h2 className="page-title font-bold text-xl sm:text-2xl flex-align-center gap-2">
          <Lock size={24} className="color-primary flex-shrink-0" /> <span>Cierre Ciego de Caja (Blind Close)</span>
        </h2>
      </div>

      {error && (
        <div className="alert alert-danger mb-4 flex-align-center gap-2 text-sm p-3">
          <AlertTriangle size={20} className="flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {/* ── Requisito 5: Bloque de Información Superior con Alineación Vertical y Holgura ── */}
      <div className="card mb-4 p-3.5 sm:p-4 register-close-top-info bg-surface border" style={{ borderRadius: '12px' }}>
        <div className="register-close-info-container">
          
          <div className="flex flex-column gap-2.5">
            <div className="flex-align-center gap-2">
              <User size={18} className="color-primary flex-shrink-0" />
              <span className="text-sm">
                <strong>Cajero:</strong> {user?.name || 'Cajero Activo'} <span className="text-muted font-mono text-xs">({user?.cedula || 'V-00000000'})</span>
              </span>
            </div>

            <div className="flex-align-center gap-2">
              <DollarSign size={18} className="color-primary flex-shrink-0" />
              <span className="text-sm">
                <strong>Tasa del Día (Informativa):</strong> <span className="font-mono font-bold color-primary">Bs.S {formatNumberEs(exchangeRate)}</span> / USD
              </span>
            </div>
          </div>

          <div className="flex-align-center gap-2 justify-content-start sm:justify-content-end">
            <Calendar size={18} className="text-muted flex-shrink-0" />
            <span className="text-sm" style={{ whiteSpace: 'nowrap' }}>
              <strong>Fecha/Hora:</strong> {new Date().toLocaleString('es-VE')}
            </span>
          </div>

        </div>
      </div>

      {/* ── Requisito 1, 2, 3 & 4: Formulario de Declaración con Islas Independientes y Foco Resaltado ── */}
      <div className="card mb-4 p-3.5 sm:p-4">
        <h3 className="card-title mb-2 text-base font-bold flex-align-center gap-2">
          Declaración de Dinero Físico por Método de Pago
        </h3>
        <p className="text-muted text-xs sm:text-sm mb-4">
          Ingrese el dinero físico o montos acumulados por cada método de pago según la moneda nativa correspondiente.
        </p>

        {loading ? (
          <div className="p-5 text-center text-muted">
            <Loader2 className="animate-spin mb-2 inline-block" size={24} />
            <div>Cargando métodos de pago activos...</div>
          </div>
        ) : methods.length === 0 ? (
          <div className="p-4 text-center text-muted border border-dashed rounded">No hay métodos de pago activos configurados.</div>
        ) : (
          <form onSubmit={handleProcessClick}>
            
            {/* Requisito 1: Islas independientes con separación de 12px entre métodos */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }} className="mb-4">
              {methods.map((method) => {
                const currency = getMethodCurrency(method);
                const isUsd = currency === 'USD';

                return (
                  <div key={method.id} className="register-close-method-card">
                    
                    {/* Requisito 2 & 3: Título limpio sin subtítulo redundante + Badge simplificado USD / Bs.S */}
                    <div className="register-close-method-info">
                      <div className="font-bold flex-align-center gap-2 text-sm sm:text-base">
                        <span>{method.name}</span>
                        <span className={`register-currency-badge ${isUsd ? 'currency-badge-usd' : 'currency-badge-bss'}`}>
                          {isUsd ? 'USD' : 'Bs.S'}
                        </span>
                      </div>
                    </div>

                    {/* Requisito 4: Campo de entrada numérico con resalto en estado Focus */}
                    <div className="register-close-method-input-wrapper">
                      <AtmAmountInput
                        value={declaredAmounts[method.id] || 0}
                        onChange={(numericVal) => handleAmountChange(method.id, numericVal)}
                        placeholder="0,00"
                        prefix={isUsd ? '$' : 'Bs.S'}
                      />
                    </div>

                  </div>
                );
              })}
            </div>

            <button
              type="submit"
              className="btn btn-primary btn-lg btn-block flex-center gap-2"
              style={{ width: '100%', height: '48px', fontSize: '1rem', fontWeight: 600 }}
              disabled={isSubmitting || loading}
            >
              {isSubmitting ? (
                <>
                  <Loader2 className="animate-spin" size={20} /> Procesando Cierre...
                </>
              ) : (
                <>
                  <Lock size={20} /> Procesar Cierre
                </>
              )}
            </button>
          </form>
        )}
      </div>

      {/* Modal Secundario: Alerta de Todo en Cero */}
      <Modal
        isOpen={isZeroConfirmOpen}
        onClose={() => setIsZeroConfirmOpen(false)}
        title="⚠️ Advertencia de Declaración en Cero"
        maxWidth="480px"
      >
        <div className="p-3 text-center">
          <AlertTriangle size={48} className="color-warning mb-3 mx-auto" />
          <h4 className="font-bold mb-2 text-base sm:text-lg">¿Está seguro de que no hay dinero físico para ningún método de pago?</h4>
          <p className="text-muted text-xs sm:text-sm mb-4">
            Ha dejado todos los campos de métodos de pago en 0,00. Si confirma, se registrará la declaración en cero para este cierre de turno.
          </p>
          <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: '0.5rem', flexWrap: 'wrap' }}>
            <button type="button" className="btn btn-outline" onClick={() => setIsZeroConfirmOpen(false)}>
              Revisar Montos
            </button>
            <button type="button" className="btn btn-primary" onClick={handleConfirmZeroes}>
              Sí, Continuar
            </button>
          </div>
        </div>
      </Modal>

      {/* Modal Intermedio: Pre-Envío de Cierre Ciego */}
      <Modal
        isOpen={isPreSendModalOpen}
        onClose={() => setIsPreSendModalOpen(false)}
        title="Confirmar Declaración de Cierre Ciego"
        maxWidth="540px"
        centerTitle={true}
      >
        <div className="p-2">
          <p className="text-muted text-xs sm:text-sm mb-3">
            Verifique la lista de montos contados físicamente antes de enviar. El sistema procesará el arqueo de forma segura.
          </p>

          <div className="overflow-x-auto border rounded mb-4 custom-scrollbar">
            <table className="cart-table" style={{ width: '100%', minWidth: '400px', fontSize: '0.875rem' }}>
              <thead>
                <tr>
                  <th style={{ whiteSpace: 'nowrap' }}>Método de Pago</th>
                  <th className="text-center" style={{ whiteSpace: 'nowrap' }}>Moneda</th>
                  <th className="text-right" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>Monto Declarado</th>
                </tr>
              </thead>
              <tbody>
                {methods.map((m) => {
                  const curr = getMethodCurrency(m);
                  const amt = declaredAmounts[m.id] || 0;
                  return (
                    <tr key={m.id}>
                      <td className="font-bold" style={{ whiteSpace: 'nowrap' }}>{m.name}</td>
                      <td className="text-center font-mono" style={{ whiteSpace: 'nowrap' }}>
                        <span className={`register-currency-badge ${curr === 'USD' ? 'currency-badge-usd' : 'currency-badge-bss'}`}>
                          {curr}
                        </span>
                      </td>
                      <td className="text-right font-mono font-bold" style={{ whiteSpace: 'nowrap', paddingRight: '14px' }}>
                        {curr === 'USD' ? formatUSD(amt) : formatBsS(amt)}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: '0.5rem', flexWrap: 'wrap', paddingTop: '0.5rem', borderTop: '1px solid var(--border)' }}>
            <button type="button" className="btn btn-outline" onClick={() => setIsPreSendModalOpen(false)} disabled={isSubmitting}>
              Cancelar
            </button>
            <button
              type="button"
              className="btn btn-primary flex-align-center gap-2"
              onClick={handleFinalSubmit}
              disabled={isSubmitting}
            >
              {isSubmitting ? (
                <>
                  <Loader2 className="animate-spin" size={16} /> Procesando...
                </>
              ) : (
                <>
                  <CheckCircle size={16} /> Confirmar y Cerrar Caja
                </>
              )}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
