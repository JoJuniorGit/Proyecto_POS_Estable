import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle,
  ArrowLeft,
  CheckCircle2,
  ClipboardList,
  Eye,
  Loader2,
  Minus,
  Plus,
  RefreshCw,
  ScanLine,
  Trash2,
  XCircle,
} from 'lucide-react';
import {
  applyStockCount,
  cancelStockCount,
  closeStockCount,
  createStockCount,
  getReview,
  getStockCount,
  listStockCounts,
  removeItem,
  scanCode,
  setItemQuantity,
} from '../services/stockCountApi';
import { getCategories, toCategoryOptions } from '../services/categoriesApi';
import { useAuth } from '../context/AuthContext';
import { normalizeRole } from '../navigation/roleViews';
import RoleGuard from '../navigation/RoleGuard';
import BarcodeScannerModal from '../components/pos/BarcodeScannerModal';
import ConfirmModal from '../components/ui/ConfirmModal';
import { formatDate, formatQuantity, formatTime, formatUSD } from '../utils/formatters';
import './StockCountPage.css';

/**
 * 8.180-T2 (S5): Paso 6 — conteo físico ciego en una sola vista (lista → detalle).
 * El flujo de conteo NUNCA muestra existencia teórica ni costo: solo producto + cantidad contada.
 * El descuadre (teórico, Δ, valores) vive exclusivamente en el panel review Admin/Manager.
 */

const STOCK_COUNT_STATUS_META = Object.freeze({
  0: Object.freeze({ label: 'Abierta', tone: 'open', hint: 'Sesión abierta: escanee y corrija cantidades.' }),
  1: Object.freeze({ label: 'Cerrada', tone: 'closed', hint: 'Teórico congelado: liste el descuadre para aplicar.' }),
  2: Object.freeze({ label: 'Aplicada', tone: 'applied', hint: 'Ajustes aplicados al stock (terminal).' }),
  3: Object.freeze({ label: 'Cancelada', tone: 'cancelled', hint: 'Sesión descartada (terminal).' }),
});

const UNKNOWN_STATUS_META = Object.freeze({ label: 'Desconocida', tone: 'unknown', hint: '' });

const STATUS_NAME_TO_CODE = Object.freeze({
  open: 0,
  closed: 1,
  applied: 2,
  cancelled: 3,
  canceled: 3,
});

const STATUS_FILTERS = Object.freeze([
  { value: '', label: 'Todas' },
  { value: 'Open', label: 'Abiertas' },
  { value: 'Closed', label: 'Cerradas' },
  { value: 'Applied', label: 'Aplicadas' },
  { value: 'Cancelled', label: 'Canceladas' },
]);

/** El backend serializa `StockCountStatus` como numérico (0=Open … 3=Cancelled); tolera nombres. */
export function getStatusMeta(status) {
  if (typeof status === 'number') {
    return STOCK_COUNT_STATUS_META[status] ?? UNKNOWN_STATUS_META;
  }
  const raw = String(status ?? '').trim();
  if (raw !== '' && Number.isInteger(Number(raw))) {
    return STOCK_COUNT_STATUS_META[Number(raw)] ?? UNKNOWN_STATUS_META;
  }
  const code = STATUS_NAME_TO_CODE[raw.toLowerCase()];
  return code === undefined ? UNKNOWN_STATUS_META : STOCK_COUNT_STATUS_META[code];
}

/** Label + tono del estado, con contexto temporal de closedAt/appliedAt (appliedAt manda). */
export function buildStatusMeta(session) {
  const meta = getStatusMeta(session?.status);
  const stamp = session?.appliedAt || session?.closedAt || null;
  const context = stamp ? `${formatDate(stamp)} ${formatTime(stamp)}`.trim() : null;
  return { ...meta, context: context || null };
}

/** Crear/cerrar/aplicar/cancelar y ver el descuadre es exclusivo de Admin/Manager. */
export function canManageStockCount(role) {
  const normalized = normalizeRole(role);
  return normalized === 'Admin' || normalized === 'Manager';
}

export function formatSignedQuantity(value) {
  const numeric = Number(value) || 0;
  if (numeric > 0) return `+${formatQuantity(numeric)}`;
  if (numeric < 0) return `-${formatQuantity(Math.abs(numeric))}`;
  return formatQuantity(0);
}

export function getDifferenceTone(value) {
  const numeric = Number(value) || 0;
  if (numeric > 0) return 'surplus';
  if (numeric < 0) return 'shortage';
  return 'even';
}

/**
 * Puente entre el POST /scan y el resolver del HUD del escáner: consume la MISMA promesa ya
 * lanzada (identidad por código), garantizando un único POST por escaneo.
 */
export function resolvePendingScanRequest(request, code) {
  if (!request || request.code !== String(code ?? '').trim()) return null;
  return request.promise;
}

export default function StockCountPage() {
  return (
    <RoleGuard view="stock-count">
      <StockCountPageContent />
    </RoleGuard>
  );
}

function StockCountPageContent() {
  const { user } = useAuth();
  const isElevated = canManageStockCount(user?.role);

  const [sessions, setSessions] = useState([]);
  const [sessionsLoading, setSessionsLoading] = useState(true);
  const [sessionsError, setSessionsError] = useState(null);
  const [statusFilter, setStatusFilter] = useState('');
  const [selectedSessionId, setSelectedSessionId] = useState(null);
  const [detail, setDetail] = useState(null);
  const [items, setItems] = useState([]);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState(null);
  const [reloadToken, setReloadToken] = useState(0);
  const [scanFeedback, setScanFeedback] = useState(null);
  const [isScannerOpen, setIsScannerOpen] = useState(false);
  const [manualCode, setManualCode] = useState('');
  const [itemPendingId, setItemPendingId] = useState(null);
  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [createForm, setCreateForm] = useState({ name: '', scopeCategoryId: '', notes: '' });
  const [createError, setCreateError] = useState(null);
  const [isCreating, setIsCreating] = useState(false);
  const [categories, setCategories] = useState([]);
  const [review, setReview] = useState(null);
  const [reviewLoading, setReviewLoading] = useState(false);
  const [reviewError, setReviewError] = useState(null);
  const [pendingAction, setPendingAction] = useState(null);
  const [confirmState, setConfirmState] = useState(null);

  // El modal del escáner resuelve su HUD aparte del POST; este ref une ambas rutas a UNA promesa.
  const scanRequestRef = useRef(null);
  const manualInputRef = useRef(null);

  const selectedSession = detail?.session || sessions.find((s) => s.id === selectedSessionId) || null;
  const statusMeta = buildStatusMeta(selectedSession);
  const categoryOptions = useMemo(() => toCategoryOptions(categories), [categories]);

  const loadSessions = useCallback(async (filter) => {
    setSessionsLoading(true);
    setSessionsError(null);
    try {
      setSessions(await listStockCounts(filter || undefined));
    } catch (err) {
      console.error('[StockCountPage] Error cargando sesiones:', err);
      setSessionsError(err?.message || 'No se pudieron cargar las sesiones de conteo.');
    } finally {
      setSessionsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadSessions(statusFilter);
  }, [loadSessions, statusFilter]);

  // Categorías para el alcance de la sesión: carga no bloqueante (si falla solo queda "Todo el inventario").
  useEffect(() => {
    let cancelled = false;
    getCategories()
      .then((list) => {
        if (!cancelled) setCategories(list);
      })
      .catch((err) => console.error('[StockCountPage] Error cargando categorías:', err));
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    // Al cambiar de sesión se resetea todo el estado del detalle para no arrastrar datos
    // de otra sesión (ítems, review y feedback incluidos).
    setDetail(null);
    setItems([]);
    setReview(null);
    setReviewError(null);
    setScanFeedback(null);
    setDetailError(null);

    if (!selectedSessionId) return undefined;

    let cancelled = false;
    setDetailLoading(true);
    getStockCount(selectedSessionId)
      .then((data) => {
        if (cancelled) return;
        setDetail(data || null);
        setItems(Array.isArray(data?.items) ? data.items : []);
      })
      .catch((err) => {
        if (cancelled) return;
        console.error('[StockCountPage] Error cargando detalle:', err);
        setDetailError(err?.message || 'No se pudo cargar el detalle de la sesión.');
      })
      .finally(() => {
        if (!cancelled) setDetailLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [selectedSessionId, reloadToken]);

  const refreshDetail = useCallback(() => setReloadToken((token) => token + 1), []);

  const applyScannedItem = useCallback((item) => {
    if (!item) return;
    setItems((prev) => {
      const next = prev.filter((i) => i.productId !== item.productId);
      next.push(item);
      next.sort((a, b) => String(a.productName || '').localeCompare(String(b.productName || '')));
      return next;
    });
    setScanFeedback({
      type: 'success',
      text: `${item.productName} (${item.sku}) — contado: ${formatQuantity(item.countedQuantity)}`,
    });
  }, []);

  const runScan = useCallback(
    (rawCode, quantityDelta = 1) => {
      const code = String(rawCode ?? '').trim();
      if (!code || !selectedSessionId) return null;
      const promise = scanCode(selectedSessionId, code, quantityDelta)
        .then((item) => {
          applyScannedItem(item);
          return item;
        })
        .catch((err) => {
          setScanFeedback({
            type: 'error',
            text: err?.message || `No se pudo procesar el código ${code}.`,
          });
          throw err;
        });
      scanRequestRef.current = { code, promise };
      return promise;
    },
    [selectedSessionId, applyScannedItem]
  );

  const handleScannedCode = useCallback(
    (code) => {
      // Único POST por escaneo: el modal también pide resolver el producto, y resolveScannedProduct
      // consume la promesa de ESTE mismo scan (ver scanRequestRef), nunca dispara otro fetch.
      runScan(code)?.catch(() => {});
    },
    [runScan]
  );

  const resolveScannedProduct = useCallback(async (code) => {
    const pending = resolvePendingScanRequest(scanRequestRef.current, code);
    if (!pending) return null;
    try {
      const item = await pending;
      return {
        id: item.productId,
        sku: item.sku,
        name: item.productName,
        // El contrato ciego no expone precio: el HUD del escáner muestra $0.00 como confirmación.
        priceUSD: 0,
        priceBsS: 0,
        isActive: true,
      };
    } catch {
      return null;
    }
  }, []);

  const handleManualSubmit = async (event) => {
    event.preventDefault();
    const code = manualCode.trim();
    if (!code) return;
    setManualCode('');
    try {
      await runScan(code);
    } catch {
      // El feedback no bloqueante ya quedó registrado en runScan.
    } finally {
      manualInputRef.current?.focus();
    }
  };

  const updateItemQuantity = useCallback(
    async (productId, quantity) => {
      if (!selectedSessionId) return false;
      setItemPendingId(productId);
      setScanFeedback(null);
      try {
        const updated = await setItemQuantity(selectedSessionId, productId, quantity);
        setItems((prev) => prev.map((i) => (i.productId === updated.productId ? updated : i)));
        return true;
      } catch (err) {
        setScanFeedback({ type: 'error', text: err?.message || 'No se pudo actualizar la cantidad contada.' });
        return false;
      } finally {
        setItemPendingId(null);
      }
    },
    [selectedSessionId]
  );

  const handleAdjustItem = (item, delta) => {
    const next = Math.max(0, Number(item.countedQuantity) + delta);
    if (next === Number(item.countedQuantity)) return;
    updateItemQuantity(item.productId, next);
  };

  const handleBackToList = () => {
    setIsScannerOpen(false);
    setManualCode('');
    setSelectedSessionId(null);
  };

  const handleConfirmRemoveItem = async (productId) => {
    if (!selectedSessionId || productId == null) return;
    setItemPendingId(productId);
    try {
      await removeItem(selectedSessionId, productId);
      setItems((prev) => prev.filter((i) => i.productId !== productId));
      setScanFeedback({ type: 'success', text: 'Producto quitado de la sesión de conteo.' });
    } catch (err) {
      setScanFeedback({ type: 'error', text: err?.message || 'No se pudo quitar el producto.' });
    } finally {
      setItemPendingId(null);
    }
  };

  const handleOpenCreate = () => {
    setCreateForm({ name: '', scopeCategoryId: '', notes: '' });
    setCreateError(null);
    setIsCreateOpen(true);
  };

  const handleCreateSession = async (event) => {
    event.preventDefault();
    const name = createForm.name.trim();
    if (!name) {
      setCreateError('Ingrese un nombre para la sesión.');
      return;
    }
    setIsCreating(true);
    setCreateError(null);
    try {
      const created = await createStockCount({
        name,
        scopeCategoryId: createForm.scopeCategoryId ? Number(createForm.scopeCategoryId) : null,
        notes: createForm.notes.trim() ? createForm.notes.trim() : null,
      });
      setIsCreateOpen(false);
      await loadSessions(statusFilter);
      if (created?.id) setSelectedSessionId(created.id);
    } catch (err) {
      setCreateError(err?.message || 'No se pudo crear la sesión de conteo.');
    } finally {
      setIsCreating(false);
    }
  };

  const handleLoadReview = useCallback(async () => {
    if (!isElevated || !selectedSessionId) return;
    setReviewLoading(true);
    setReviewError(null);
    try {
      setReview(await getReview(selectedSessionId));
    } catch (err) {
      setReviewError(err?.message || 'No se pudo cargar el descuadre de la sesión.');
    } finally {
      setReviewLoading(false);
    }
  }, [isElevated, selectedSessionId]);

  const runSessionAction = async (kind) => {
    if (!selectedSessionId) return;
    setPendingAction(kind);
    setScanFeedback(null);
    try {
      const mutate = kind === 'close' ? closeStockCount : kind === 'apply' ? applyStockCount : cancelStockCount;
      const updated = await mutate(selectedSessionId);
      setDetail((prev) => (prev ? { ...prev, session: updated } : prev));
      setReview(null);
      await loadSessions(statusFilter);
      setReloadToken((token) => token + 1);
    } catch (err) {
      const verb = kind === 'close' ? 'cerrar' : kind === 'apply' ? 'aplicar' : 'cancelar';
      setScanFeedback({ type: 'error', text: err?.message || `No se pudo ${verb} la sesión.` });
    } finally {
      setPendingAction(null);
    }
  };

  const requestClose = () =>
    setConfirmState({
      kind: 'close',
      title: '¿Cerrar el conteo?',
      message:
        'Al cerrar se congela el teórico y el costo de cada ítem para el descuadre. Ya no se podrán escanear ni editar cantidades en esta sesión.',
      confirmText: 'Cerrar conteo',
      variant: 'warning',
    });

  const requestApply = () =>
    setConfirmState({
      kind: 'apply',
      title: '¿Aplicar los ajustes al stock?',
      message:
        'El stock quedará exactamente en las cantidades contadas y se registrará un movimiento de Kardex por cada diferencia. Esta acción es irreversible.',
      confirmText: 'Aplicar ajustes',
      variant: 'warning',
    });

  const requestCancel = () =>
    setConfirmState({
      kind: 'cancel',
      title: '¿Cancelar la sesión?',
      message: 'La sesión quedará descartada y no se aplicará ningún ajuste al stock.',
      confirmText: 'Sí, cancelar',
      variant: 'danger',
    });

  const requestRemoveItem = (item) =>
    setConfirmState({
      kind: 'remove',
      productId: item.productId,
      title: '¿Quitar el producto del conteo?',
      message: `${item.productName} (${item.sku}) se quitará de la sesión. Podrá volver a escanearlo después.`,
      confirmText: 'Quitar',
      variant: 'danger',
    });

  const handleConfirmAction = () => {
    const state = confirmState;
    setConfirmState(null);
    if (!state) return;
    if (state.kind === 'remove') {
      void handleConfirmRemoveItem(state.productId);
      return;
    }
    void runSessionAction(state.kind);
  };

  return (
    <div className="sc-page">
      {sessionsError && (
        <div className="alert alert-danger sc-list-alert">
          <span>{sessionsError}</span>
          <button type="button" className="btn btn-outline btn-sm" onClick={() => loadSessions(statusFilter)}>
            Reintentar
          </button>
        </div>
      )}

      {!selectedSessionId ? (
        <>
          <div className="sc-header">
            <div>
              <h1 className="sc-title">
                <ClipboardList size={24} /> Conteo físico
              </h1>
              <p className="sc-subtitle">
                Conteo ciego por escáner: solo producto y cantidad contada. El descuadre se revisa al cerrar la sesión.
              </p>
            </div>
            <div className="sc-header-actions">
              {isElevated && (
                <button type="button" className="btn btn-primary" onClick={handleOpenCreate}>
                  <Plus size={16} /> Nueva sesión
                </button>
              )}
              <button
                type="button"
                className="btn btn-outline"
                onClick={() => loadSessions(statusFilter)}
                disabled={sessionsLoading}
              >
                <RefreshCw size={16} className={sessionsLoading ? 'spin' : ''} /> Actualizar
              </button>
            </div>
          </div>

          {isElevated && isCreateOpen && (
            <form className="card sc-create-form" onSubmit={handleCreateSession}>
              <h2 className="sc-section-title">
                <ClipboardList size={18} /> Nueva sesión de conteo
              </h2>
              <div className="form-group">
                <label className="form-label" htmlFor="sc-create-name">
                  Nombre
                </label>
                <input
                  id="sc-create-name"
                  type="text"
                  className="form-input"
                  maxLength={80}
                  value={createForm.name}
                  onChange={(e) => setCreateForm((prev) => ({ ...prev, name: e.target.value }))}
                  placeholder="Ej: Conteo octubre 2026"
                  required
                />
              </div>
              <div className="form-group">
                <label className="form-label" htmlFor="sc-create-scope">
                  Categoría (opcional, incluye subcategorías)
                </label>
                <select
                  id="sc-create-scope"
                  className="form-select"
                  value={createForm.scopeCategoryId}
                  onChange={(e) => setCreateForm((prev) => ({ ...prev, scopeCategoryId: e.target.value }))}
                >
                  <option value="">Todo el inventario</option>
                  {categoryOptions.map((option) => (
                    <option key={option.value} value={option.value} title={option.path}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label className="form-label" htmlFor="sc-create-notes">
                  Notas (opcional)
                </label>
                <textarea
                  id="sc-create-notes"
                  className="form-input"
                  rows={2}
                  maxLength={300}
                  value={createForm.notes}
                  onChange={(e) => setCreateForm((prev) => ({ ...prev, notes: e.target.value }))}
                />
              </div>
              {createError && <div className="sc-feedback sc-feedback--error">{createError}</div>}
              <div className="sc-form-actions">
                <button type="button" className="btn btn-outline" onClick={() => setIsCreateOpen(false)} disabled={isCreating}>
                  Cancelar
                </button>
                <button type="submit" className="btn btn-primary" disabled={isCreating}>
                  {isCreating ? <Loader2 size={16} className="spin" /> : <CheckCircle2 size={16} />} Crear sesión
                </button>
              </div>
            </form>
          )}

          <div className="sc-filter-row">
            <label className="form-label" htmlFor="sc-status-filter">
              Estado
            </label>
            <select
              id="sc-status-filter"
              className="form-select sc-filter-select"
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
            >
              {STATUS_FILTERS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </div>

          {sessionsLoading && sessions.length === 0 ? (
            <div className="sc-loading">
              <Loader2 size={36} className="spin" />
              <p>Cargando sesiones…</p>
            </div>
          ) : sessions.length === 0 ? (
            <div className="card sc-empty">
              <ClipboardList size={28} />
              <p>No hay sesiones de conteo {statusFilter ? 'con ese estado' : 'registradas'}.</p>
            </div>
          ) : (
            <div className="sc-session-list">
              {sessions.map((session) => {
                const meta = buildStatusMeta(session);
                return (
                  <button
                    key={session.id}
                    type="button"
                    className="sc-session-card"
                    onClick={() => setSelectedSessionId(session.id)}
                  >
                    <span className="sc-session-card-main">
                      <span className="sc-session-name">{session.name}</span>
                      <span className="sc-session-meta">
                        {session.scopeCategoryPath || 'Todo el inventario'} · {session.itemsCount} ítem(s)
                      </span>
                      <span className="sc-session-meta">Creada {formatDate(session.createdAt)}</span>
                    </span>
                    <span className={`sc-badge sc-badge--${meta.tone}`}>{meta.label}</span>
                    {meta.context && <span className="sc-session-context">{meta.context}</span>}
                  </button>
                );
              })}
            </div>
          )}
        </>
      ) : (
        <>
          <div className="sc-header">
            <div className="sc-detail-head">
              <button type="button" className="btn btn-outline btn-sm sc-back-btn" onClick={handleBackToList}>
                <ArrowLeft size={15} /> Volver a la lista
              </button>
              <h1 className="sc-title">
                <ClipboardList size={22} /> {selectedSession?.name || `Sesión #${selectedSessionId}`}
              </h1>
              <span className={`sc-badge sc-badge--${statusMeta.tone}`}>
                {statusMeta.label}
                {statusMeta.context ? ` · ${statusMeta.context}` : ''}
              </span>
              <span className="sc-session-meta">{selectedSession?.scopeCategoryPath || 'Todo el inventario'}</span>
            </div>
            <button type="button" className="btn btn-outline" onClick={refreshDetail} disabled={detailLoading}>
              <RefreshCw size={16} className={detailLoading ? 'spin' : ''} /> Actualizar
            </button>
          </div>

          {detailError && <div className="alert alert-danger">{detailError}</div>}

          {scanFeedback && (
            <div className={`sc-feedback sc-feedback--${scanFeedback.type}`} role="status" aria-live="polite">
              {scanFeedback.text}
            </div>
          )}

          {detailLoading && !detail ? (
            <div className="sc-loading">
              <Loader2 size={32} className="spin" />
              <p>Cargando detalle…</p>
            </div>
          ) : (
            <>
              {selectedSession?.status === 0 && (
                <form className="sc-scan-bar" onSubmit={handleManualSubmit}>
                  <input
                    ref={manualInputRef}
                    type="text"
                    className="form-input sc-scan-input"
                    placeholder="Escanea o escribe el SKU / código de barras y presiona Enter"
                    value={manualCode}
                    onChange={(e) => setManualCode(e.target.value)}
                    aria-label="Código de producto a contar"
                    autoFocus
                  />
                  <button type="submit" className="btn btn-primary sc-scan-add" disabled={!manualCode.trim()}>
                    <Plus size={16} /> Contar
                  </button>
                  <button
                    type="button"
                    className="btn btn-outline sc-scan-camera"
                    onClick={() => setIsScannerOpen(true)}
                    aria-label="Escanear código de barras con la cámara"
                    title="Escanear código de barras con la cámara"
                  >
                    <ScanLine size={18} />
                  </button>
                </form>
              )}

              {items.length === 0 ? (
                <div className="card sc-empty">
                  <ScanLine size={26} />
                  <p>Sin ítems contados todavía. Escanea o escribe un código para comenzar.</p>
                </div>
              ) : (
                <ul className="sc-item-list">
                  {items.map((item) => {
                    const busy = itemPendingId === item.productId;
                    const isOpen = selectedSession?.status === 0;
                    return (
                      <li key={item.productId} className="sc-item-row">
                        <span className="sc-item-main">
                          <span className="sc-item-name">{item.productName}</span>
                          <span className="sc-item-sku">{item.sku}</span>
                        </span>
                        <span className="sc-item-stepper">
                          <button
                            type="button"
                            className="sc-stepper-btn"
                            aria-label={`Restar una unidad a ${item.productName}`}
                            disabled={!isOpen || busy}
                            onClick={() => handleAdjustItem(item, -1)}
                          >
                            <Minus size={14} />
                          </button>
                          <CountedQuantityInput item={item} disabled={!isOpen || busy} onCommit={updateItemQuantity} />
                          <button
                            type="button"
                            className="sc-stepper-btn"
                            aria-label={`Sumar una unidad a ${item.productName}`}
                            disabled={!isOpen || busy}
                            onClick={() => handleAdjustItem(item, 1)}
                          >
                            <Plus size={14} />
                          </button>
                        </span>
                        <button
                          type="button"
                          className="btn btn-danger-text sc-item-remove"
                          aria-label={`Quitar ${item.productName} del conteo`}
                          disabled={!isOpen || busy}
                          onClick={() => requestRemoveItem(item)}
                        >
                          <Trash2 size={15} />
                        </button>
                      </li>
                    );
                  })}
                </ul>
              )}

              {selectedSession && selectedSession.status !== 0 && (
                <div className="sc-terminal-note">{getStatusMeta(selectedSession.status).hint}</div>
              )}
            </>
          )}

          {/* REVIEW_PANEL_START — único punto de la vista que expone teórico/costo (solo Admin/Manager). */}
          {isElevated && selectedSession && (selectedSession.status === 0 || selectedSession.status === 1) && (
            <section className="card sc-review">
              <div className="sc-review-head">
                <h2 className="sc-section-title">
                  <Eye size={18} /> Descuadre (Admin/Manager)
                </h2>
                <button
                  type="button"
                  className="btn btn-outline"
                  onClick={handleLoadReview}
                  disabled={reviewLoading || pendingAction !== null}
                >
                  {reviewLoading ? <Loader2 size={16} className="spin" /> : <RefreshCw size={16} />}
                  {review ? 'Actualizar revisión' : 'Revisar descuadre'}
                </button>
              </div>

              {reviewError && <div className="sc-feedback sc-feedback--error">{reviewError}</div>}

              {review && (
                <>
                  {review.theoreticalIsPreview && (
                    <div className="sc-preview-note">
                      <AlertTriangle size={15} />
                      Teórico EN VIVO (sesión abierta): los valores pueden cambiar hasta cerrar la sesión.
                    </div>
                  )}

                  {review.items.length === 0 ? (
                    <p className="sc-empty-note">Sin productos contados en esta sesión.</p>
                  ) : (
                    <div className="sc-table-wrap">
                      <table className="sc-review-table">
                        <thead>
                          <tr>
                            <th>Producto</th>
                            <th className="text-right">Contado</th>
                            <th className="text-right">Teórico</th>
                            <th className="text-right">Δ</th>
                            <th className="text-right">Costo USD</th>
                            <th className="text-right">Valor USD</th>
                          </tr>
                        </thead>
                        <tbody>
                          {review.items.map((row) => (
                            <tr key={row.productId}>
                              <td>
                                <span className="sc-review-name">{row.productName}</span>
                                <span className="sc-item-sku">{row.sku}</span>
                              </td>
                              <td className="text-right">{formatQuantity(row.countedQuantity)}</td>
                              <td className="text-right">
                                {formatQuantity(row.theoreticalStock)}
                                {row.theoreticalIsPreview && <span className="sc-preview-chip">preview</span>}
                              </td>
                              <td className={`text-right sc-diff-${getDifferenceTone(row.difference)}`}>
                                {formatSignedQuantity(row.difference)}
                              </td>
                              <td className="text-right">{formatUSD(row.unitCostUSD)}</td>
                              <td className={`text-right sc-diff-${getDifferenceTone(row.differenceValueUsd)}`}>
                                {formatUSD(row.differenceValueUsd)}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}

                  <div className="sc-totals">
                    <div className="sc-total-card sc-diff-surplus">
                      <span className="sc-total-label">Sobrantes</span>
                      <span className="sc-total-value">{formatQuantity(review.totalSurplusQuantity)} und</span>
                      <span className="sc-total-sub">{formatUSD(review.totalSurplusValueUsd)}</span>
                    </div>
                    <div className="sc-total-card sc-diff-shortage">
                      <span className="sc-total-label">Faltantes</span>
                      <span className="sc-total-value">{formatQuantity(review.totalShortageQuantity)} und</span>
                      <span className="sc-total-sub">{formatUSD(review.totalShortageValueUsd)}</span>
                    </div>
                    <div className={`sc-total-card sc-diff-${getDifferenceTone(review.netValueUsd)}`}>
                      <span className="sc-total-label">Neto</span>
                      <span className="sc-total-value">{formatUSD(review.netValueUsd)}</span>
                      <span className="sc-total-sub">Sobrantes − Faltantes</span>
                    </div>
                  </div>

                  {review.unscannedInScopeCount > 0 && (
                    <div className="sc-warn-note">
                      <AlertTriangle size={15} />
                      {review.unscannedInScopeCount} producto(s) del alcance sin escanear: no se aplicarán ajustes sobre
                      ellos (conteo rotativo parcial).
                    </div>
                  )}

                  <div className="sc-review-actions">
                    {selectedSession.status === 0 && (
                      <button
                        type="button"
                        className="btn btn-primary"
                        onClick={requestClose}
                        disabled={pendingAction !== null}
                      >
                        {pendingAction === 'close' ? (
                          <Loader2 size={16} className="spin" />
                        ) : (
                          <CheckCircle2 size={16} />
                        )}{' '}
                        Cerrar conteo
                      </button>
                    )}
                    {selectedSession.status === 1 && (
                      <button
                        type="button"
                        className="btn btn-success"
                        onClick={requestApply}
                        disabled={pendingAction !== null}
                      >
                        {pendingAction === 'apply' ? (
                          <Loader2 size={16} className="spin" />
                        ) : (
                          <CheckCircle2 size={16} />
                        )}{' '}
                        Aplicar ajustes
                      </button>
                    )}
                    {(selectedSession.status === 0 || selectedSession.status === 1) && (
                      <button
                        type="button"
                        className="btn btn-danger"
                        onClick={requestCancel}
                        disabled={pendingAction !== null}
                      >
                        {pendingAction === 'cancel' ? <Loader2 size={16} className="spin" /> : <XCircle size={16} />}{' '}
                        Cancelar sesión
                      </button>
                    )}
                  </div>
                </>
              )}
            </section>
          )}
        </>
      )}

      <BarcodeScannerModal
        isOpen={isScannerOpen}
        onClose={() => {
          setIsScannerOpen(false);
          manualInputRef.current?.focus();
        }}
        onCodeScanned={handleScannedCode}
        resolveProduct={resolveScannedProduct}
      />

      <ConfirmModal
        isOpen={Boolean(confirmState)}
        onClose={() => setConfirmState(null)}
        onConfirm={handleConfirmAction}
        title={confirmState?.title || ''}
        message={confirmState?.message || ''}
        confirmText={confirmState?.confirmText || 'Confirmar'}
        variant={confirmState?.variant || 'warning'}
      />
    </div>
  );
}

/** Input de cantidad contada con commit por blur/Enter (PUT absoluto al backend). */
function CountedQuantityInput({ item, disabled, onCommit }) {
  const [draft, setDraft] = useState(() => formatQuantity(item.countedQuantity));
  const savingRef = useRef(false);

  useEffect(() => {
    setDraft(formatQuantity(item.countedQuantity));
  }, [item.productId, item.countedQuantity]);

  const commit = async () => {
    // Enter y blur pueden dispararse juntos (el input se deshabilita al guardar): un solo PUT.
    if (savingRef.current) return;
    const raw = String(draft).trim().replace(',', '.');
    const parsed = Number(raw);
    if (raw === '' || !Number.isFinite(parsed) || parsed < 0) {
      setDraft(formatQuantity(item.countedQuantity));
      return;
    }
    if (parsed === Number(item.countedQuantity)) return;
    savingRef.current = true;
    try {
      const saved = await onCommit(item.productId, parsed);
      if (!saved) setDraft(formatQuantity(item.countedQuantity));
    } finally {
      savingRef.current = false;
    }
  };

  return (
    <input
      type="number"
      className="sc-qty-input form-input"
      min="0"
      step="1"
      value={draft}
      disabled={disabled}
      aria-label={`Cantidad contada de ${item.productName}`}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          commit();
        }
      }}
    />
  );
}
