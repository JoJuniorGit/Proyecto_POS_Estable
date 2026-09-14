import { useState, useEffect, useCallback, useRef } from 'react';
import { getPendingSalesPage, completeSale, addPaymentsBatchToHoldSale, cancelSale, claimSale, releaseSale } from '../services/salesApi';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { useAuth } from '../context/AuthContext';
import CheckoutModal from '../components/checkout/CheckoutModal';
import EditSaleModal from '../components/pos/EditSaleModal';
import SuccessScreen from '../components/checkout/SuccessScreen';
import Modal from '../components/ui/Modal';
import PendingOrderDesktopRow from '../components/pending/PendingOrderDesktopRow';
import PendingOrderMobileCard from '../components/pending/PendingOrderMobileCard';
import { formatNumberEs } from '../utils/formatters';
import { isLockedByOther, getLockInfo } from '../utils/holdLock';
import { Search, Loader2, Clock, ChevronDown, RefreshCw, Trash2, AlertTriangle } from 'lucide-react';
import './PendingOrdersPage.css';

function ignoreLockReleaseFailure() {
  return undefined;
}

export default function PendingOrdersPage() {
  const [sales, setSales] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [expandedSaleId, setExpandedSaleId] = useState(null);
  const [selectedSaleId, setSelectedSaleId] = useState(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [showConfirmCancel, setShowConfirmCancel] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [completedLiquidation, setCompletedLiquidation] = useState(null);
  
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();
  const currentUserId = user?.id;
  const isElevated = user?.role === 'Admin' || user?.role === 'Manager';
  const activeLockRef = useRef(null);

  // 8.29-A05: clave de idempotencia estable POR LOTE de abonos (saleId -> batchId).
  // Se reutiliza en reintentos para que un fallo de red no duplique pagos ya acreditados;
  // el batch es atómico (todo-o-nada) y un replay no persiste NINGÚN abono repetido.
  const abonoBatchKeysRef = useRef(new Map());

  // Modals state
  const [selectedSaleForCheckout, setSelectedSaleForCheckout] = useState(null);
  const [selectedSaleForEdit, setSelectedSaleForEdit] = useState(null);

  // 8.14-N1: paginación de UI — página actual (offset) y si hay más para el botón "Ver más".
  const PAGE_SIZE = 200;
  const [hasMore, setHasMore] = useState(false);
  const [pageOffset, setPageOffset] = useState(0);

  const loadPendingData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const { items, totalCount } = await getPendingSalesPage({ limit: PAGE_SIZE, offset: 0 });
      setSales(items || []);
      setPageOffset(items?.length || 0);
      setHasMore(totalCount > (items?.length || 0));
      const lockedSaleId = activeLockRef.current;
      if (lockedSaleId !== null && lockedSaleId !== undefined) {
        const lockedSale = (items || []).find((item) => item.id === lockedSaleId);
        const stillLocked = Boolean(lockedSale && lockedSale.claimedByUserId !== null && lockedSale.claimedByUserId !== undefined);
        if (!stillLocked) activeLockRef.current = null;
      }
    } catch (err) {
      console.error(err);
      setError('No se pudieron cargar las cuentas abiertas.');
    } finally {
      setLoading(false);
    }
  }, []);

  // 8.14-N1: carga la siguiente página y la agrega a la lista ("Ver más").
  const loadMore = useCallback(async () => {
    if (loading) return;
    setLoading(true);
    setError(null);
    try {
      const { items, totalCount } = await getPendingSalesPage({ limit: PAGE_SIZE, offset: pageOffset });
      if (items.length > 0) {
        setSales(prev => [...(prev || []), ...items]);
        setPageOffset(prev => prev + items.length);
      }
      setHasMore(totalCount > pageOffset + items.length);
    } catch (err) {
      console.error(err);
      setError('No se pudieron cargar más cuentas.');
    } finally {
      setLoading(false);
    }
  }, [loading, pageOffset]);

  useEffect(() => {
    loadPendingData();

    const handleRefreshSignal = () => loadPendingData();
    window.addEventListener('onHoldSalesUpdated', handleRefreshSignal);

    return () => {
      window.removeEventListener('onHoldSalesUpdated', handleRefreshSignal);
    };
    // 8.6-M3: exchangeRate NO depende del re-fetch — la lista se refresca con el evento
    // onHoldSalesUpdated (que el servidor emite al recalcular). Evita recargar por cada tick de tasa.
  }, [loadPendingData]);

  useEffect(() => {
    return () => {
      const lockedSaleId = activeLockRef.current;
      if (lockedSaleId === null || lockedSaleId === undefined) return;
      releaseSale(lockedSaleId).catch(ignoreLockReleaseFailure);
    };
  }, []);

  const toggleExpand = (id) => {
    setSelectedSaleId(id);
    setExpandedSaleId(prev => prev === id ? null : id);
  };

  const releaseActiveLock = useCallback(async () => {
    const lockedSaleId = activeLockRef.current;
    if (lockedSaleId === null || lockedSaleId === undefined) return;
    try {
      await releaseSale(lockedSaleId);
      activeLockRef.current = null;
    } catch {
      ignoreLockReleaseFailure();
    }
  }, []);

  const handleStartCheckout = async (sale) => {
    if (isLockedByOther(sale, currentUserId)) return;
    setError(null);
    try {
      await claimSale(sale.id, 'Checkout');
      activeLockRef.current = sale.id;
      setSelectedSaleForCheckout(sale);
    } catch (err) {
      await loadPendingData();
      setError(err.message || 'No se pudo reclamar el pedido.');
    }
  };

  const handleEditSale = async (sale) => {
    if (isLockedByOther(sale, currentUserId)) return;
    setError(null);
    try {
      await claimSale(sale.id, 'Editing');
      activeLockRef.current = sale.id;
      setSelectedSaleId(sale.id);
      setSelectedSaleForEdit(sale);
    } catch (err) {
      await loadPendingData();
      setError(err.message || 'No se pudo reclamar el pedido.');
    }
  };

  const handleCloseCheckout = async () => {
    setSelectedSaleForCheckout(null);
    await releaseActiveLock();
    await loadPendingData();
  };

  const handleCloseEdit = async () => {
    setSelectedSaleForEdit(null);
    await releaseActiveLock();
    await loadPendingData();
  };

  const handleForceRelease = async (sale) => {
    setError(null);
    try {
      await releaseSale(sale.id, true);
      if (activeLockRef.current === sale.id) activeLockRef.current = null;
      await loadPendingData();
    } catch (err) {
      setError(err.message || 'No se pudo liberar el pedido.');
    }
  };

  const selectedSale = sales.find(s => s.id === selectedSaleId) || sales.find(s => s.id === expandedSaleId);
  const selectedSaleTotalPaidUSD = selectedSale?.totalPaidUSD || (selectedSale?.payments?.reduce((acc, p) => acc + (p.amount || 0), 0)) || 0;
  const hasPayments = selectedSaleTotalPaidUSD > 0 || (selectedSale?.payments && selectedSale.payments.length > 0);
  const canCancelSelectedSale = Boolean(selectedSale && !hasPayments);
  const selectedSaleLockInfo = selectedSale ? getLockInfo(selectedSale, currentUserId) : null;
  const selectedSaleLockedByOther = Boolean(selectedSaleLockInfo?.isLockedByOther);

  const handleConfirmCancelSale = async () => {
    if (!selectedSale || !canCancelSelectedSale) return;
    setIsDeleting(true);
    setError(null);
    try {
      await cancelSale(selectedSale.id);
      setShowConfirmCancel(false);
      setSelectedSaleId(null);
      if (expandedSaleId === selectedSale.id) setExpandedSaleId(null);
      await loadPendingData();
      window.dispatchEvent(new CustomEvent('onHoldSalesUpdated'));
    } catch (err) {
      console.error('[PendingOrdersPage] Error al anular pedido:', err);
      const msg = err.response?.data?.message || err.response?.data || err.message || 'Error al anular el pedido.';
      setError(typeof msg === 'string' ? msg : 'Error al anular el pedido.');
      setShowConfirmCancel(false);
    } finally {
      setIsDeleting(false);
    }
  };

  const filteredSales = sales.filter(s => {
    if (!searchQuery) return true;
    const q = searchQuery.toLowerCase();
    const customerName = s.customer?.name?.toLowerCase() || '';
    const customerRif = s.customer?.cedulaOrRif?.toLowerCase() || '';
    const saleIdStr = s.id.toString();
    return customerName.includes(q) || customerRif.includes(q) || saleIdStr.includes(q);
  });

  return (
    <div className="pending-orders-container">
      {/* ── 1. Encabezado Adaptativo ── */}
      <div className="pending-orders-header">
        <div>
          <h1 className="pending-orders-title">
            <Clock className="color-primary" size={26} /> Cuentas Abiertas (Pedidos en Espera)
          </h1>
          <p className="pending-orders-desc">
            Gestiona abonos en Bolívares protegidos contra devaluación y liquida cuentas sin descontar inventario previamente.
          </p>
        </div>

        <div className="d-flex gap-2 flex-wrap flex-align-center">
          {selectedSale ? (
            <button
              className="btn btn-danger flex-align-center gap-2 font-bold ppo-danger-btn ppo-danger-btn-active"
              onClick={() => setShowConfirmCancel(true)}
              disabled={!canCancelSelectedSale || isDeleting || selectedSaleLockedByOther}
              title={selectedSaleLockedByOther ? selectedSaleLockInfo.label : (hasPayments ? "No se puede anular un pedido con abonos acumulados" : `Anular pedido #${selectedSale.id}`)}
            >
              <Trash2 size={16} /> Anular Pedido #{selectedSale.id}
            </button>
          ) : (
            <button
              className="btn btn-danger flex-align-center gap-2 font-bold ppo-danger-btn ppo-danger-btn-disabled"
              disabled={true}
              title="Seleccione un pedido en la lista para anularlo"
            >
              <Trash2 size={16} /> Anular Pedido
            </button>
          )}

          <button className="btn btn-outline flex-align-center gap-2" onClick={loadPendingData} disabled={loading}>
            <RefreshCw size={16} className={loading ? 'spin' : ''} /> Actualizar
          </button>
        </div>
      </div>

      {/* ── 2. Controles de Búsqueda y Tasa BCV ── */}
      <div className="pending-orders-controls-bar">
        <div className="pending-orders-search-wrapper">
          <input
            type="text"
            className="input-field w-full ppo-search-input"
            placeholder="Buscar por Cliente, Cédula/RIF o ID..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
          />
          <Search size={18} className="ppo-search-icon opacity-50" />
        </div>

        <div className="pending-orders-bcv-badge">
          Tasa BCV del Día: <span className="rate-value">{formatNumberEs(exchangeRate)} Bs/$</span>
        </div>
      </div>

      {error && (
        <div className="alert-box danger-alert mb-4">
          {error}
        </div>
      )}

      {loading && sales.length === 0 ? (
        <div className="ppo-loading text-center">
          <Loader2 size={40} className="spin color-primary mb-4 mx-auto" />
          <p>Cargando cuentas abiertas...</p>
        </div>
      ) : filteredSales.length === 0 ? (
        <div className="card p-5 text-center text-muted">
          <p className="ppo-faded">No hay pedidos en espera pendientes.</p>
        </div>
      ) : (
        <>
          {/* ── 3A. VISTA ESCRITORIO (TABLA TRADICIONAL) ── */}
          <div className="pending-desktop-view">
            <table>
              <thead>
                <tr>
                  <th>ID / Fecha</th>
                  <th>Cliente</th>
                  <th className="text-right">Total Factura</th>
                  <th className="text-right">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {filteredSales.map((sale) => (
                  <PendingOrderDesktopRow
                    key={sale.id}
                    sale={sale}
                    isExpanded={expandedSaleId === sale.id}
                    lockInfo={getLockInfo(sale, currentUserId)}
                    isElevated={isElevated}
                    onToggle={toggleExpand}
                    onCheckout={handleStartCheckout}
                    onEdit={handleEditSale}
                    onForceRelease={handleForceRelease}
                  />
                ))}
              </tbody>
            </table>
          </div>

          {/* ── 3B. VISTA MÓVIL (DISEÑO DE TARJETAS / CARD LAYOUT - OPCIÓN B) ── */}
          <div className="pending-mobile-view">
            {filteredSales.map((sale) => (
              <PendingOrderMobileCard
                key={sale.id}
                sale={sale}
                isExpanded={expandedSaleId === sale.id}
                lockInfo={getLockInfo(sale, currentUserId)}
                isElevated={isElevated}
                exchangeRate={exchangeRate}
                onToggle={toggleExpand}
                onCheckout={handleStartCheckout}
                onEdit={handleEditSale}
                onForceRelease={handleForceRelease}
              />
            ))}
          </div>

          {/* 8.14-N1: botón "Ver más" para paginar la cola sin perder las ya cargadas. */}
          {hasMore && (
            <div className="text-center mt-3 mb-1">
              <button
                type="button"
                className="btn btn-outline flex-align-center gap-2 mx-auto ppo-load-more"
                onClick={loadMore}
                disabled={loading}
              >
                {loading ? <Loader2 size={16} className="animate-spin" /> : <ChevronDown size={16} />}
                {loading ? 'Cargando...' : 'Ver más cuentas'}
              </button>
            </div>
          )}
        </>
      )}

      {/* Checkout Modal for Completing OnHold Sales */}
      {selectedSaleForCheckout && (
        <CheckoutModal
          isOpen={!!selectedSaleForCheckout}
          onClose={handleCloseCheckout}
          overrideSale={selectedSaleForCheckout}
          onCompleteSale={async (paymentList, roundingAdjustment, isPendingPickup, idempotencyKey) => {
            try {
              const targetSaleId = selectedSaleForCheckout.id;
              const currentPaidUsd = selectedSaleForCheckout.totalPaidUSD || 0;
              const totalUsd = selectedSaleForCheckout.totalUSD || 0;
              const paidNowUsd = paymentList.reduce((acc, p) => acc + (p.amount || 0), 0);
              const remainingDebtAfterUsd = totalUsd - (currentPaidUsd + paidNowUsd);
              const isFullyCompleted = remainingDebtAfterUsd <= 0.05;

              if (isFullyCompleted) {
                const invoiceNumber = await completeSale(targetSaleId, exchangeRate, paymentList, roundingAdjustment, null, isPendingPickup, idempotencyKey);
                setSelectedSaleForCheckout(null);
                await releaseActiveLock();
                await loadPendingData();
                window.dispatchEvent(new CustomEvent('pendingPickupsUpdated'));

                if (isPendingPickup) {
                  setCompletedLiquidation({
                    invoiceNumber: invoiceNumber > 0 ? invoiceNumber : targetSaleId,
                    title: "¡Pedido Pagado y Enviado a Retiros Pendientes!",
                    badgeText: `Factura N° ${invoiceNumber > 0 ? invoiceNumber.toString().padStart(6, '0') : targetSaleId}`,
                    message: "La factura fue pagada al 100% y los productos quedaron resguardados en custodia para su retiro físico posterior.",
                    buttonText: "Aceptar"
                  });
                } else {
                  setCompletedLiquidation({
                    invoiceNumber: invoiceNumber > 0 ? invoiceNumber : targetSaleId,
                    title: "¡Cuenta Liquidada con Éxito!",
                    badgeText: `Factura N° ${invoiceNumber > 0 ? invoiceNumber.toString().padStart(6, '0') : targetSaleId}`,
                    message: "La factura fue completada en su totalidad y el inventario ha sido descontado correctamente.",
                    buttonText: "Aceptar"
                  });
                }
              } else {
                // 8.29-A05: el lote de abonos se envía en UNA sola petición atómica
                // (POST /payments/batch, todo-o-nada) con ÚNICA clave estable por lote.
                let batchId = abonoBatchKeysRef.current.get(targetSaleId);
                if (!batchId) {
                  batchId = (typeof crypto !== 'undefined' && crypto.randomUUID
                    ? crypto.randomUUID()
                    : `abono-${targetSaleId}-${Date.now()}`);
                  abonoBatchKeysRef.current.set(targetSaleId, batchId);
                }
                await addPaymentsBatchToHoldSale(
                  targetSaleId,
                  paymentList.map((p) => ({
                    paymentMethodId: p.paymentMethodId,
                    amountBsS: p.amountBsS || p.amountLocal,
                    exchangeRate: exchangeRate,
                    referenceNumber: p.referenceNumber || null,
                  })),
                  batchId
                );
                setSelectedSaleForCheckout(null);
                await releaseActiveLock();
                await loadPendingData();
                // 8.29-A05: la clave de idempotencia del lote se libera solo DESPUÉS de
                // confirmar la recarga; si ésta falla, el reintento reutiliza la misma
                // clave y el servidor lo descarta (evita doble abono en ventana perdida).
                abonoBatchKeysRef.current.delete(targetSaleId);

                setCompletedLiquidation({
                  invoiceNumber: null,
                  title: "¡Abono Registrado con Éxito!",
                  badgeText: `Pedido N° #${targetSaleId}`,
                  message: "El abono fue registrado exitosamente en el historial de abonos anti-devaluación de la cuenta.",
                  buttonText: "Aceptar"
                });
              }
            } catch (err) {
              const msg = err.response?.data?.message || err.response?.data?.Message || (typeof err.response?.data === 'string' ? err.response?.data : null) || err.message || 'Error al procesar la operación.';
              setError(msg);
            }
          }}
        />
      )}

      {/* Overlay de Éxito / Confirmación de Liquidación */}
      {completedLiquidation && (
        <SuccessScreen
          invoiceNumber={completedLiquidation.invoiceNumber}
          title={completedLiquidation.title}
          badgeText={completedLiquidation.badgeText}
          message={completedLiquidation.message}
          buttonText={completedLiquidation.buttonText}
          type="checkout"
          onClose={() => setCompletedLiquidation(null)}
        />
      )}

      {/* Modal Dedicado de Edición de Pedido en Espera */}
      {selectedSaleForEdit && (
        <EditSaleModal
          isOpen={!!selectedSaleForEdit}
          onClose={handleCloseEdit}
          sale={selectedSaleForEdit}
          exchangeRate={exchangeRate}
          onSuccess={loadPendingData}
        />
      )}

      {/* Modal de Confirmación Estilizado para Anulación desde la Sección */}
      {showConfirmCancel && selectedSale && (
        <Modal isOpen={true} onClose={() => setShowConfirmCancel(false)} title="Confirmar Anulación de Pedido" maxWidth="440px">
          <div className="text-center py-2">
            <div className="ppo-cancel-icon">
              <AlertTriangle size={24} />
            </div>
            <h3 className="font-bold text-base mb-2 text-primary">¿Está seguro de que desea anular el Pedido #{selectedSale.id}?</h3>
            <p className="text-xs text-muted mb-4 ppo-confirm-note">
              Esta acción anulará el pedido sin descontar caja y liberará las reservas asociadas. Esta acción no se puede deshacer.
            </p>
            <div className="d-flex justify-center gap-3">
              <button type="button" className="btn btn-outline" onClick={() => setShowConfirmCancel(false)} disabled={isDeleting}>
                Cancelar
              </button>
              <button type="button" className="btn btn-danger d-inline-flex flex-align-center gap-1" onClick={handleConfirmCancelSale} disabled={isDeleting}>
                {isDeleting ? <Loader2 size={16} className="animate-spin" /> : <Trash2 size={16} />}
                Sí, Anular Pedido
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
