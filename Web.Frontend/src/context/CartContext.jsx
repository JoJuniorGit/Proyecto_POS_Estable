import { createContext, useContext, useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useExchangeRate } from './ExchangeRateContext';
import { useAuth } from './AuthContext';
import {
  startSale,
  getSale,
  addItemToSale,
  updateItemQuantity,
  removeItemFromSale,
  updateSaleExchangeRate,
  updateSaleCustomer,
  updatePriceList,
} from '../services/salesApi';
import { getLineAmounts } from '../utils/formatters';

const CartStateContext = createContext(null);
const CartActionsContext = createContext(null);

export function CartProvider({ children }) {
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();
  const [currentSale, setCurrentSale] = useState(() => {
    try {
      if (typeof window !== 'undefined') {
        const cached = sessionStorage.getItem('active_pos_sale_cache');
        if (cached) {
          const parsed = JSON.parse(cached);
          if (parsed && (parsed.status === 'Pending' || parsed.status === 'OnHold')) {
            return parsed;
          }
        }
      }
    } catch (e) {
      console.warn('[CartContext] Error cargando caché de venta:', e);
    }
    return null;
  });
  const [selectedItemId, setSelectedItemId] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const cartRequestIdRef = useRef(0);
  const inFlightCreateRef = useRef(null);

// Sincronizar la venta activa en sessionStorage para persistencia en recargas.
  // 8.5-WEB2: Debounce de 400ms — evita JSON.stringify en cada render intermedio (ráfagas de
  // actualizaciones de tasa/ítems producen una sola escritura persistida).
  useEffect(() => {
    if (typeof window === 'undefined') return;
    const timer = setTimeout(() => {
      if (currentSale?.id && (currentSale.status === 'Pending' || currentSale.status === 'OnHold')) {
        sessionStorage.setItem('active_pos_sale_id', String(currentSale.id));
        sessionStorage.setItem('active_pos_sale_cache', JSON.stringify(currentSale));
        sessionStorage.setItem('active_pos_has_items', (currentSale.items?.length > 0) ? 'true' : 'false');
      } else if (!currentSale) {
        sessionStorage.removeItem('active_pos_sale_cache');
        sessionStorage.removeItem('active_pos_has_items');
      }
    }, 400);
    return () => clearTimeout(timer);
  }, [currentSale]);

// Inicializar o crear nueva venta
  const createNewSale = useCallback(async () => {
    // 8.7-M10: sin usuario autenticado NO se crean ventas Pending huérfanas (cashierId null).
    if (!user?.id) {
      setError('Inicie sesión para iniciar una venta.');
      return null;
    }
    if (inFlightCreateRef.current) {
      return inFlightCreateRef.current;
    }
    setLoading(true);
    setError(null);
    const promise = (async () => {
      try {
        const sale = await startSale(user?.id);
        setCurrentSale(sale);
        setSelectedItemId(null);
        if (sale?.id) {
          sessionStorage.setItem('active_pos_sale_id', String(sale.id));
          sessionStorage.setItem('active_pos_sale_cache', JSON.stringify(sale));
          sessionStorage.setItem('active_pos_has_items', (sale.items?.length > 0) ? 'true' : 'false');
        }
        return sale;
      } catch (err) {
        console.error('[CartContext] Error al crear nueva venta:', err);
        setError('No se pudo iniciar la sesión de venta. Intente de nuevo.');
      } finally {
        setLoading(false);
        inFlightCreateRef.current = null;
      }
    })();
    inFlightCreateRef.current = promise;
    return promise;
  }, [user?.id]);

  // Cargar venta existente (para editar pedido en espera)
  const loadExistingSale = useCallback(async (saleId) => {
    setLoading(true);
    setError(null);
    try {
      const sale = await getSale(saleId);
      setCurrentSale(sale);
      setSelectedItemId(null);
      if (sale?.id) {
        sessionStorage.setItem('active_pos_sale_id', String(sale.id));
      }
      return sale;
    } catch (err) {
      console.error('[CartContext] Error cargando venta para edición:', err);
      setError('No se pudo cargar la venta solicitada.');
    } finally {
      setLoading(false);
    }
  }, []);

// Al montar, restaurar venta activa de la sesión o crear una nueva.
  // 8.7-M10: SOLO cuando hay usuario autenticado (user.id). En la pantalla de login no se crean
  // ventas Pending huérfanas sin cajero asignado; al iniciar sesión el efecto se re-ejecuta.
  useEffect(() => {
    if (!user?.id) return;

    const restoreOrStartSale = async () => {
      const savedSaleId = sessionStorage.getItem('active_pos_sale_id');
      if (savedSaleId) {
        try {
          const sale = await getSale(Number(savedSaleId));
          if (sale && (sale.status === 'Pending' || sale.status === 'OnHold')) {
            setCurrentSale(sale);
            return;
          }
        } catch {
          sessionStorage.removeItem('active_pos_sale_id');
          sessionStorage.removeItem('active_pos_sale_cache');
          sessionStorage.removeItem('active_pos_has_items');
        }
      }
      createNewSale();
    };

    restoreOrStartSale();
  }, [user?.id, createNewSale]);

// Si cambia la tasa global de cambio y hay venta pendiente o en espera, notificar al backend o actualizar
  // 8.5-WEB2: Debounce trailing de 1500ms — cada ráfaga de SignalR (o cambios rápidos de tasa) produce
  // UNA escritura al final, no un write por evento.
  useEffect(() => {
    if (!currentSale?.id || !(exchangeRate > 0) || !(currentSale.status === 'Pending' || currentSale.status === 'OnHold')) {
      return;
    }

    const timer = setTimeout(() => {
      updateSaleExchangeRate(currentSale.id, exchangeRate)
        .then(updatedSale => {
          if (updatedSale) setCurrentSale(updatedSale);
        })
        .catch(err => console.warn('[CartContext] Error actualizando tasa en venta:', err.message));
    }, 1500);

    return () => clearTimeout(timer);
  }, [exchangeRate, currentSale?.id, currentSale?.status]);

  const validateOnHoldRules = useCallback((prospectiveTotalUSD) => {
    if (currentSale?.status !== 'OnHold') return true;
    const totalPaidUSD = currentSale.totalPaidUSD || (currentSale.payments?.reduce((acc, p) => acc + (p.amount || 0), 0)) || 0;
    if (prospectiveTotalUSD < (totalPaidUSD - 0.01)) {
      setError(`No se puede reducir el total del pedido por debajo de lo ya abonado por el cliente ($${totalPaidUSD.toFixed(2)} USD).`);
      return false;
    }
    return true;
  }, [currentSale?.status, currentSale?.totalPaidUSD, currentSale?.payments]);

  // Eliminar ítem
  const removeItem = useCallback(async (itemId) => {
    if (!currentSale?.id) return;

    if (currentSale.status === 'OnHold') {
      const currentItems = currentSale.items || [];
      const prospectiveItems = currentItems.filter(i => i.id !== itemId);
      const prospectiveTotal = prospectiveItems.reduce((acc, i) => acc + i.subtotal, 0);
      if (!validateOnHoldRules(prospectiveTotal)) {
        return;
      }
    }

    const reqId = ++cartRequestIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const rateToUse = exchangeRate > 0 ? exchangeRate : (currentSale.appliedRate || 1);
      const updatedSale = await removeItemFromSale(currentSale.id, itemId, rateToUse);
      if (reqId === cartRequestIdRef.current) {
        setCurrentSale(updatedSale);
        if (selectedItemId === itemId) {
          setSelectedItemId(null);
        }
      }
    } catch (err) {
      console.error('[CartContext] Error eliminando item:', err);
      if (reqId === cartRequestIdRef.current) {
        setError(err.message || 'Error al eliminar producto del carrito');
      }
    } finally {
      if (reqId === cartRequestIdRef.current) {
        setLoading(false);
      }
    }
  }, [currentSale?.id, currentSale?.status, currentSale?.items, currentSale?.appliedRate, exchangeRate, selectedItemId, validateOnHoldRules]);

  // Agregar ítem al carrito
  const addItem = useCallback(async (product, quantity = 1) => {
    let sale = currentSale;
    if (!sale || (sale.status !== 'Pending' && sale.status !== 'OnHold')) {
      sale = await createNewSale();
    }
    if (!sale?.id) return;

    if (sale.status === 'OnHold') {
      const price = product.priceUSD || product.unitPriceUSD || 0;
      const currentTotal = (sale.items || []).reduce((acc, i) => acc + (i.subtotal || 0), 0);
      if (!validateOnHoldRules(currentTotal + (price * quantity))) {
        return;
      }
    }

    const reqId = ++cartRequestIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const rateToUse = exchangeRate > 0 ? exchangeRate : (sale.appliedRate || 1);
      const updatedSale = await addItemToSale(sale.id, product.id, quantity, rateToUse);
      if (reqId === cartRequestIdRef.current) {
        setCurrentSale(updatedSale);
      }
      return updatedSale;
    } catch (err) {
      console.error('[CartContext] Error agregando item:', err);
      if (reqId === cartRequestIdRef.current) {
        setError(err.message || 'Error al agregar producto al carrito');
      }
    } finally {
      if (reqId === cartRequestIdRef.current) {
        setLoading(false);
      }
    }
  }, [currentSale, createNewSale, exchangeRate, validateOnHoldRules]);

  // Cambiar cantidad
  const updateQuantity = useCallback(async (itemId, newQuantity) => {
    if (!currentSale?.id) return;

    if (newQuantity === '' || newQuantity === null || newQuantity === undefined || typeof newQuantity !== 'number' || isNaN(newQuantity)) {
      return;
    }

    // Regla de negocio POS: Reducir cantidad nunca debe eliminar el producto del carrito.
    // Para eliminar un ítem, el usuario debe utilizar explícitamente la acción de eliminar (removeItem).
    if (newQuantity <= 0) {
      return;
    }

    const currentItems = currentSale.items || [];
    const targetItem = currentItems.find(i => i.id === itemId);
    const isFrac = Boolean(targetItem?.isFractional);
    const validatedQty = isFrac ? Math.round(newQuantity * 1000) / 1000 : Math.max(1, Math.trunc(newQuantity));

    if (currentSale.status === 'OnHold') {
      const prospectiveItems = currentItems.map(i => i.id === itemId ? { ...i, quantity: validatedQty, subtotal: validatedQty * i.unitPrice } : i);
      const prospectiveTotal = prospectiveItems.reduce((acc, i) => acc + i.subtotal, 0);
      if (!validateOnHoldRules(prospectiveTotal)) {
        return;
      }
    }

    const reqId = ++cartRequestIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const rateToUse = exchangeRate > 0 ? exchangeRate : (currentSale.appliedRate || 1);
      const updatedSale = await updateItemQuantity(currentSale.id, itemId, validatedQty, rateToUse);
      if (reqId === cartRequestIdRef.current) {
        setCurrentSale(updatedSale);
      }
    } catch (err) {
      console.error('[CartContext] Error modificando cantidad:', err);
      if (reqId === cartRequestIdRef.current) {
        setError(err.message || 'Error al modificar la cantidad');
      }
    } finally {
      if (reqId === cartRequestIdRef.current) {
        setLoading(false);
      }
    }
  }, [currentSale?.id, currentSale?.status, currentSale?.items, currentSale?.appliedRate, exchangeRate, validateOnHoldRules]);

  // Cambiar lista de precios ("Retail" | "Wholesale")
  const changePriceList = useCallback(async (priceListType) => {
    if (!currentSale?.id) return;
    const reqId = ++cartRequestIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const updatedSale = await updatePriceList(currentSale.id, priceListType);
      if (reqId === cartRequestIdRef.current) {
        setCurrentSale(updatedSale);
      }
      return updatedSale;
    } catch (err) {
      console.error('[CartContext] Error cambiando lista de precios:', err);
      const msg = err.response?.data?.message || err.response?.data?.Message || err.message || 'Error al cambiar lista de precios';
      if (reqId === cartRequestIdRef.current) {
        setError(msg);
      }
      throw new Error(msg);
    } finally {
      if (reqId === cartRequestIdRef.current) {
        setLoading(false);
      }
    }
  }, [currentSale?.id]);

  // Limpiar carrito / Iniciar nueva venta
const resetCart = useCallback(async () => {
    // 8.5-WEB5: retorna si se pudo iniciar la nueva venta; el caller decide anunciar éxito o avisar.
    const sale = await createNewSale();
    return !!sale;
  }, [createNewSale]);

  // Actualizar cliente de la venta
  const updateCustomer = useCallback(async (customerId) => {
    if (!currentSale?.id) return;
    setLoading(true);
    setError(null);
    try {
      const updatedSale = await updateSaleCustomer(currentSale.id, customerId);
      setCurrentSale(updatedSale);
      return updatedSale;
    } catch (err) {
      console.error('[CartContext] Error actualizando cliente:', err);
      setError(err.message || 'Error al actualizar el cliente de la venta');
    } finally {
      setLoading(false);
    }
  }, [currentSale?.id]);

const items = useMemo(() => currentSale?.items || [], [currentSale?.items]);
  const subtotalUSD = useMemo(
    () => currentSale?.subtotal ?? items.reduce((acc, item) => acc + (item.subtotal || 0), 0),
    [currentSale?.subtotal, items]
  );
  const totalUSD = currentSale?.totalUSD ?? subtotalUSD;
  const rateToUse = exchangeRate > 0 ? exchangeRate : (currentSale?.appliedRate || 1);

  const { subtotalBsS, totalBsS } = useMemo(() => {
    const calculated = items.reduce((acc, item) => acc + getLineAmounts(item, rateToUse).subtotalBsS, 0);
    return {
      subtotalBsS: (currentSale?.subtotalBsS > 0) ? currentSale.subtotalBsS : calculated,
      totalBsS: (currentSale?.totalBsS > 0) ? currentSale.totalBsS : calculated,
    };
  }, [items, rateToUse, currentSale?.subtotalBsS, currentSale?.totalBsS]);

  const stateValue = useMemo(() => ({
    currentSale,
    items,
    selectedItemId,
    setSelectedItemId,
    loading,
    error,
    setError,
    subtotalUSD,
    totalUSD,
    subtotalBsS,
    totalBsS,
  }), [currentSale, items, selectedItemId, loading, error, subtotalUSD, totalUSD, subtotalBsS, totalBsS]);

  const actionsValue = useMemo(() => ({
    addItem,
    updateQuantity,
    removeItem,
    resetCart,
    createNewSale,
    loadExistingSale,
    updateCustomer,
    changePriceList,
  }), [addItem, updateQuantity, removeItem, resetCart, createNewSale, loadExistingSale, updateCustomer, changePriceList]);

  return (
    <CartStateContext.Provider value={stateValue}>
      <CartActionsContext.Provider value={actionsValue}>
        {children}
      </CartActionsContext.Provider>
    </CartStateContext.Provider>
  );
}

export function useCartState() {
  const context = useContext(CartStateContext);
  if (!context) {
    throw new Error('useCartState debe ser usado dentro de un CartProvider');
  }
  return context;
}

export function useCartActions() {
  const context = useContext(CartActionsContext);
  if (!context) {
    throw new Error('useCartActions debe ser usado dentro de un CartProvider');
  }
  return context;
}

export function useCart() {
  const state = useCartState();
  const actions = useCartActions();
  return { ...state, ...actions };
}
