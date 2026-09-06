import { useState, useEffect, useRef } from 'react';
import { api } from '../services/api';
import { 
  getAllPaymentMethods, 
  createPaymentMethod, 
  updatePaymentMethod, 
  deletePaymentMethod 
} from '../services/paymentApi';
import { BrowserQRCodeSvgWriter } from '@zxing/library';
import { Settings, CreditCard, Plus, Loader2, Check, QrCode, Server, Wifi, Copy, RefreshCw, Trash2, DollarSign } from 'lucide-react';
import { useCurrencyFormat } from '../context/CurrencyFormatContext';

export default function SettingsPage() {
  const { currencyFormat, setCurrencyFormat, formatBsS, formatUSD } = useCurrencyFormat();
  const [methods, setMethods] = useState([]);
  const [loadingMethods, setLoadingMethods] = useState(false);
  const [newMethodName, setNewMethodName] = useState('');
  const [newMethodIsCash, setNewMethodIsCash] = useState(false);
  const [newMethodRequiresRef, setNewMethodRequiresRef] = useState(false);
  const [message, setMessage] = useState(null);

  const handleFormatChange = async (newFmt) => {
    try {
      await setCurrencyFormat(newFmt);
      setMessage({
        type: 'success',
        text: `Formato de moneda actualizado a "${newFmt === 'Venezuelan' ? 'Venezolano Contable (1.234,56)' : 'Internacional (1,234.56)'}".`,
      });
      setTimeout(() => setMessage(null), 3500);
    } catch (err) {
      setMessage({
        type: 'danger',
        text: err?.response?.data?.message || err?.message || 'Error al actualizar el formato de moneda.',
      });
    }
  };

  // Estados para Emparejamiento QR (por defecto HTTPS / Puerto 5001 para permitir uso de cámara en móviles)
  const [pairingInfo, setPairingInfo] = useState(null);
  const [selectedInterface, setSelectedInterface] = useState(null);
  const [useHttps, setUseHttps] = useState(true);
  const [loadingPairing, setLoadingPairing] = useState(false);
  const [copiedIp, setCopiedIp] = useState(false);
  const [copiedUrl, setCopiedUrl] = useState(false);
  const qrRef = useRef(null);

  const loadMethods = async () => {
    setLoadingMethods(true);
    try {
      const data = await getAllPaymentMethods();
      setMethods(data || []);
    } catch (err) {
      console.error('[SettingsPage] Error cargando métodos de pago:', err);
    } finally {
      setLoadingMethods(false);
    }
  };

  const loadPairingInfo = async () => {
    setLoadingPairing(true);
    try {
      const data = await api.get('/api/pairing/info');
      if (data) {
        setPairingInfo(data);
        if (data.networkInterfaces && data.networkInterfaces.length > 0) {
          const primary = data.networkInterfaces.find((i) => i.isPrimary) || data.networkInterfaces[0];
          setSelectedInterface(primary);
        }
      }
    } catch (err) {
      console.error('[SettingsPage] Error cargando datos de emparejamiento:', err);
    } finally {
      setLoadingPairing(false);
    }
  };

  useEffect(() => {
    loadMethods();
    loadPairingInfo();

    const handleMethodsUpdated = () => {
      loadMethods();
    };
    window.addEventListener('onPaymentMethodsUpdated', handleMethodsUpdated);
    return () => {
      window.removeEventListener('onPaymentMethodsUpdated', handleMethodsUpdated);
    };
  }, []);

  // Cálculo de URL y payload activo
  const currentIp = selectedInterface?.ipAddress || pairingInfo?.primaryIpAddress || (typeof window !== 'undefined' ? window.location.hostname : 'localhost') || '127.0.0.1';
  const currentPort = useHttps ? (pairingInfo?.httpsPort || 5001) : (pairingInfo?.httpPort || 5000);
  const currentScheme = useHttps ? 'https' : 'http';
  const fullUrl = `${currentScheme}://${currentIp}:${currentPort}`;
  const activePayload = `${fullUrl}/?paired=true`;

  // Renderizado dinámico del QR SVG
  useEffect(() => {
    if (!qrRef.current || !activePayload) return;
    try {
      const writer = new BrowserQRCodeSvgWriter();
      const svg = writer.write(activePayload, 200, 200);
      svg.style.backgroundColor = '#FFFFFF';
      svg.style.display = 'block';
      svg.style.borderRadius = '4px';

      // Fondo blanco explícito dentro del árbol SVG para máxima compatibilidad y contraste
      const bgRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
      bgRect.setAttribute('width', '100%');
      bgRect.setAttribute('height', '100%');
      bgRect.setAttribute('fill', '#FFFFFF');
      svg.insertBefore(bgRect, svg.firstChild);

      qrRef.current.innerHTML = '';
      qrRef.current.appendChild(svg);
    } catch (err) {
      console.error('[SettingsPage] Error generando QR SVG:', err);
    }
  }, [activePayload, loadingPairing]);

  const handleCopy = async (text, type) => {
    try {
      await navigator.clipboard.writeText(text);
      if (type === 'ip') {
        setCopiedIp(true);
        setTimeout(() => setCopiedIp(false), 2000);
      } else if (type === 'url') {
        setCopiedUrl(true);
        setTimeout(() => setCopiedUrl(false), 2000);
      }
    } catch {
      // Fallback manual
      const textarea = document.createElement('textarea');
      textarea.value = text;
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand('copy');
      document.body.removeChild(textarea);
      if (type === 'ip') {
        setCopiedIp(true);
        setTimeout(() => setCopiedIp(false), 2000);
      } else if (type === 'url') {
        setCopiedUrl(true);
        setTimeout(() => setCopiedUrl(false), 2000);
      }
    }
  };

  const handleToggleCash = async (method) => {
    const updated = { ...method, isCash: !method.isCash };
    setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    try {
      await updatePaymentMethod(method.id, updated);
      setMessage({ type: 'success', text: `Método "${method.name}" clasificado como ${updated.isCash ? 'Físico' : 'Digital'}.` });
    } catch (err) {
      console.error('[SettingsPage] Error actualizando tipo de método:', err);
      setMessage({ type: 'danger', text: err?.response?.data?.message || 'Error al cambiar tipo del método.' });
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
      setMessage({ type: 'danger', text: err?.response?.data?.message || 'Error al cambiar estado del método.' });
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
      setMessage({ type: 'danger', text: err?.response?.data?.message || 'Error al actualizar configuración de referencia.' });
      loadMethods();
    }
  };

  const handleDeleteMethod = async (method) => {
    const confirmed = window.confirm(
      `¿Está seguro de eliminar el método de pago "${method.name}"?\n\nSi posee transacciones históricas registradas, será archivado de forma segura sin afectar reportes ni auditorías.`
    );
    if (!confirmed) return;

    setMethods((prev) => prev.filter((m) => m.id !== method.id));
    try {
      await deletePaymentMethod(method.id);
      setMessage({ type: 'success', text: `Método "${method.name}" eliminado correctamente.` });
    } catch (err) {
      console.error('[SettingsPage] Error eliminando método:', err);
      setMessage({ type: 'danger', text: err?.response?.data?.message || 'Error al eliminar método de pago.' });
      loadMethods();
    }
  };

  const handleAddMethod = async (e) => {
    e.preventDefault();
    if (!newMethodName.trim()) return;

    try {
      const dto = {
        name: newMethodName.trim(),
        isActive: true,
        isCash: newMethodIsCash,
        requiresReference: newMethodRequiresRef,
      };
      await createPaymentMethod(dto);
      setNewMethodName('');
      setNewMethodIsCash(false);
      setNewMethodRequiresRef(false);
      setMessage({ type: 'success', text: 'Nuevo método de pago agregado correctamente.' });
      loadMethods();
    } catch (err) {
      console.error('[SettingsPage] Error agregando método:', err);
      setMessage({ type: 'danger', text: err?.response?.data?.message || 'Error al crear método de pago.' });
      loadMethods();
    }
  };

  return (
    <div className="settings-page" style={{ maxWidth: '900px', margin: '0 auto', padding: '16px' }}>
      <h2 className="page-title mb-4 font-bold text-xl sm:text-2xl flex-align-center gap-2">
        <Settings size={24} className="color-primary flex-shrink-0" />
        <span>Configuración del Sistema</span>
      </h2>

      {message && <div className={`alert alert-${message.type} mb-4 text-sm p-3`}>{message.text}</div>}

      {/* ── SECCIÓN 1: EMPAREJAMIENTO QR Y CONECTIVIDAD DE RED ── */}
      <div className="card mb-4 p-3 sm:p-4">
        <div className="flex-between flex-align-center mb-3">
          <h3 className="card-title flex-align-center gap-2 text-base font-bold mb-0">
            <QrCode size={20} className="color-primary flex-shrink-0" />
            <span>Emparejamiento de Dispositivos Móviles / Web</span>
          </h3>
          <button
            type="button"
            className="btn btn-sm btn-outline flex-align-center gap-1 text-xs"
            onClick={loadPairingInfo}
            disabled={loadingPairing}
            title="Recargar datos de red"
          >
            <RefreshCw size={14} className={loadingPairing ? 'animate-spin' : ''} />
            <span>Actualizar</span>
          </button>
        </div>

        {loadingPairing ? (
          <div className="p-4 text-center text-muted">
            <Loader2 className="animate-spin mb-2 inline-block" size={24} />
            <div>Cargando datos de emparejamiento de red...</div>
          </div>
        ) : (
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: '20px' }}>
            
            {/* Columna Izquierda: Código QR y Controles */}
            <div className="flex-column flex-align-center text-center p-3 border rounded-lg bg-surface">
              <span className="text-xs font-bold uppercase color-primary mb-2">Escaneo Rápido (Cámara Móvil)</span>
              
              <div 
                ref={qrRef} 
                className="qr-code-container mb-2" 
                style={{ 
                  backgroundColor: '#FFFFFF',
                  width: '224px', 
                  height: '224px', 
                  display: 'flex', 
                  alignItems: 'center', 
                  justifyContent: 'center',
                  padding: '12px',
                  borderRadius: '10px',
                  border: '1px solid #E2E8F0',
                  boxShadow: '0 4px 12px rgba(0, 0, 0, 0.2)'
                }}
              />

              <p className="text-xs text-muted mb-3" style={{ maxWidth: '240px' }}>
                Apunta con la cámara de tu teléfono o tablet para abrir y sincronizar el Punto de Venta.
              </p>

              {/* Selector de Interfaz de Red */}
              {pairingInfo?.networkInterfaces?.length > 1 && (
                <div className="w-full text-left mb-2.5">
                  <label className="text-xs text-muted font-semibold mb-1 block">Adaptador de Red:</label>
                  <select
                    className="form-input text-xs"
                    value={selectedInterface?.ipAddress || ''}
                    onChange={(e) => {
                      const iface = pairingInfo.networkInterfaces.find((i) => i.ipAddress === e.target.value);
                      setSelectedInterface(iface || null);
                    }}
                  >
                    {pairingInfo.networkInterfaces.map((iface) => (
                      <option key={iface.ipAddress} value={iface.ipAddress}>
                        {iface.name || iface.description} ({iface.ipAddress}){iface.isPrimary ? ' ★' : ''}
                      </option>
                    ))}
                  </select>
                </div>
              )}

              {/* Toggle HTTPS */}
              <label className="cursor-pointer flex-align-center gap-2 text-xs font-medium w-full text-left p-2 rounded bg-background border">
                <input
                  type="checkbox"
                  checked={useHttps}
                  onChange={(e) => setUseHttps(e.target.checked)}
                />
                <span>Usar HTTPS (Puerto {pairingInfo?.httpsPort || 5001} para cámara web en móviles)</span>
              </label>
            </div>

            {/* Columna Derecha: Datos de Conexión Manual */}
            <div className="flex-column gap-3 justify-center">
              <h4 className="text-sm font-bold flex-align-center gap-2 mb-1">
                <Server size={16} className="color-primary" /> Datos para Conexión Manual
              </h4>

              {/* Hostname */}
              <div className="p-3 border rounded-lg bg-surface">
                <div className="text-xs text-muted mb-0.5">Nombre del Servidor / Hostname:</div>
                <div className="font-bold text-sm">{pairingInfo?.serverName || pairingInfo?.machineName || 'POS-SERVER'}</div>
              </div>

              {/* Dirección IP */}
              <div className="p-3 border rounded-lg bg-surface flex-between flex-align-center">
                <div>
                  <div className="text-xs text-muted mb-0.5">Dirección IP Local:</div>
                  <div className="font-bold text-sm color-primary font-mono">{currentIp}</div>
                </div>
                <button
                  type="button"
                  className="btn btn-sm btn-outline flex-align-center gap-1 text-xs"
                  onClick={() => handleCopy(currentIp, 'ip')}
                >
                  {copiedIp ? <Check size={14} className="color-success" /> : <Copy size={14} />}
                  <span>{copiedIp ? 'Copiado' : 'Copiar IP'}</span>
                </button>
              </div>

              {/* URL Completa */}
              <div className="p-3 border rounded-lg bg-surface">
                <div className="flex-between flex-align-center mb-1">
                  <div className="text-xs text-muted">URL Completa para Navegador:</div>
                  <button
                    type="button"
                    className="btn btn-sm btn-primary flex-align-center gap-1 text-xs"
                    onClick={() => handleCopy(fullUrl, 'url')}
                  >
                    {copiedUrl ? <Check size={14} /> : <Copy size={14} />}
                    <span>{copiedUrl ? 'Copiado' : 'Copiar URL'}</span>
                  </button>
                </div>
                <div className="font-mono text-xs font-semibold break-all p-2 rounded bg-background border">
                  {fullUrl}
                </div>
              </div>

              {/* Alerta Wi-Fi */}
              <div className="p-3 border rounded-lg text-xs flex-align-start gap-2" style={{ backgroundColor: 'rgba(0, 128, 255, 0.08)', borderColor: 'rgba(0, 128, 255, 0.25)' }}>
                <Wifi size={18} className="flex-shrink-0" style={{ color: '#0080FF', marginTop: '2px' }} />
                <span>
                  <strong>Nota de Red:</strong> Asegúrese de que los teléfonos, tablets o terminales estén conectados a la <strong>misma red Wi-Fi / LAN</strong> que este equipo servidor.
                </span>
              </div>
            </div>

          </div>
        )}
      </div>

      {/* ── SECCIÓN 2: FORMATO DE MONEDA Y NÚMEROS ── */}
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

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', gap: '16px' }} className="mb-4">
          {/* Opción 1: Venezolano contable */}
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
                style={{ cursor: 'pointer' }}
              />
            </div>
            <p className="text-xs text-muted mb-2">
              Separador de miles: <strong>punto (.)</strong> | Separador decimal: <strong>coma (,)</strong>
            </p>
            <div className="font-mono text-xs p-2 rounded bg-background border">
              Ejemplo: <strong>172.786,94</strong>
            </div>
          </div>

          {/* Opción 2: Internacional */}
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
                style={{ cursor: 'pointer' }}
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

        {/* Vista Previa en Vivo */}
        <div className="p-3 border rounded-lg bg-surface">
          <span className="text-xs font-bold text-muted uppercase tracking-wider block mb-2">
            Vista Previa Activa en el Sistema:
          </span>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '8px' }} className="text-xs sm:text-sm">
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

      {/* Métodos de Pago */}
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
            {/* ── Tarjetas Independientes para Vista Móvil con Etiquetas de Contexto ── */}
            <div className="settings-mobile-cards-view mb-4">
              {methods.map((m) => (
                <div key={m.id} className="settings-method-card p-3 mb-3 border rounded-lg bg-surface shadow-xs">
                  
                  {/* Fila 1: Encabezado de Tarjeta (Nombre a la izquierda, Estado y Eliminar a la derecha) */}
                  <div className="flex-between flex-align-center mb-2.5 pb-2 border-bottom">
                    <span className="font-bold text-base color-primary">{m.name}</span>
                    <div className="flex-align-center gap-2">
                      <button
                        type="button"
                        className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'} text-xs font-bold px-3`}
                        onClick={() => handleToggleActive(m)}
                        style={{ borderRadius: '14px', minWidth: '76px' }}
                        title="Alternar estado activo"
                        aria-label={`Alternar estado activo para ${m.name}`}
                      >
                        {m.isActive ? 'Activo' : 'Inactivo'}
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline text-danger p-1"
                        onClick={() => handleDeleteMethod(m)}
                        title="Eliminar método de pago"
                        aria-label={`Eliminar método ${m.name}`}
                        style={{ color: '#DC2626', borderColor: '#FCA5A5' }}
                      >
                        <Trash2 size={15} />
                      </button>
                    </div>
                  </div>

                  {/* Fila 2: Detalles Secundarios con Tipo interactivo y Requiere Ref */}
                  <div className="flex-between flex-align-center text-xs">
                    <div className="flex-align-center gap-1.5">
                      <span className="text-muted text-xs">Tipo:</span>
                      <button
                        type="button"
                        className={`btn btn-xs ${m.isCash ? 'btn-success' : 'btn-outline'} text-xs font-bold`}
                        onClick={() => handleToggleCash(m)}
                        style={{
                          borderRadius: '10px',
                          padding: '2px 10px',
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
                        className={`btn btn-xs ${m.requiresReference ? 'btn-primary' : 'btn-outline'} text-xs font-bold`}
                        onClick={() => handleToggleRef(m)}
                        style={{ borderRadius: '10px', padding: '2px 10px' }}
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

            {/* Vista de Tabla Tradicional para Escritorio */}
            <div className="overflow-x-auto settings-desktop-table-view mb-4">
              <table className="cart-table mb-2">
                <thead>
                  <tr>
                    <th>Nombre</th>
                    <th className="text-center">Tipo</th>
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
                          className={`btn btn-sm ${m.isCash ? 'btn-success' : 'btn-outline'} text-xs font-bold`}
                          style={{
                            borderRadius: '12px',
                            padding: '3px 12px',
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
                          className="btn btn-sm btn-outline text-danger p-1.5"
                          onClick={() => handleDeleteMethod(m)}
                          title="Eliminar método de pago"
                          aria-label={`Eliminar método ${m.name}`}
                          style={{ color: '#DC2626', borderColor: '#FCA5A5' }}
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

        {/* Formulario para Agregar Método */}
        <form onSubmit={handleAddMethod} className="border-top pt-3 mt-2">
          <h4 className="font-bold mb-3 text-sm sm:text-base">Agregar Nuevo Método de Pago</h4>
          <div className="form-row align-end flex-wrap gap-3">
            <div className="form-group flex-2 mb-0" style={{ minWidth: '220px' }}>
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

            <div className="form-group mb-0 flex-align-center" style={{ paddingBottom: '8px' }}>
              <label className="form-label cursor-pointer flex-align-center gap-2 text-sm mb-0" title="Desmarcado por defecto: se creará como Digital">
                <input
                  type="checkbox"
                  checked={newMethodIsCash}
                  onChange={(e) => setNewMethodIsCash(e.target.checked)}
                />
                <span>Es Efectivo (Físico)</span>
              </label>
            </div>

            <div className="form-group mb-0 flex-align-center" style={{ paddingBottom: '8px' }}>
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
      </div>
    </div>
  );
}
