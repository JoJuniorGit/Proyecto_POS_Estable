import { useState, useEffect, useRef } from 'react';
import { api } from '../services/api';
import { getAllPaymentMethods } from '../services/paymentApi';
import { BrowserQRCodeSvgWriter } from '@zxing/library';
import { Settings, CreditCard, Plus, Save, Loader2, Check, X, QrCode, Server, Wifi, Copy, RefreshCw } from 'lucide-react';

export default function SettingsPage() {
  const [methods, setMethods] = useState([]);
  const [loadingMethods, setLoadingMethods] = useState(false);
  const [newMethodName, setNewMethodName] = useState('');
  const [newMethodIsCash, setNewMethodIsCash] = useState(false);
  const [newMethodRequiresRef, setNewMethodRequiresRef] = useState(false);
  const [message, setMessage] = useState(null);

  // Estados para Emparejamiento QR
  const [pairingInfo, setPairingInfo] = useState(null);
  const [selectedInterface, setSelectedInterface] = useState(null);
  const [useHttps, setUseHttps] = useState(typeof window !== 'undefined' && window.location?.protocol === 'https:');
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

  const handleToggleActive = async (method) => {
    try {
      const updated = { ...method, isActive: !method.isActive };
      await api.put(`/api/paymentmethods/${method.id}`, updated);
      setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    } catch (err) {
      console.error('[SettingsPage] Error actualizando método:', err);
      setMessage({ type: 'danger', text: 'Error al cambiar estado del método.' });
    }
  };

  const handleToggleRef = async (method) => {
    try {
      const updated = { ...method, requiresReference: !method.requiresReference };
      await api.put(`/api/paymentmethods/${method.id}`, updated);
      setMethods((prev) => prev.map((m) => (m.id === method.id ? updated : m)));
    } catch (err) {
      console.error('[SettingsPage] Error actualizando método:', err);
      setMessage({ type: 'danger', text: 'Error al actualizar configuración de referencia.' });
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
      await api.post('/api/paymentmethods', dto);
      setNewMethodName('');
      setNewMethodIsCash(false);
      setNewMethodRequiresRef(false);
      setMessage({ type: 'success', text: 'Nuevo método de pago agregado.' });
      loadMethods();
    } catch (err) {
      console.error('[SettingsPage] Error agregando método:', err);
      setMessage({ type: 'danger', text: 'Error al crear método de pago.' });
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
                className="bg-white p-2 rounded-lg border flex-center mb-2" 
                style={{ width: '216px', height: '216px', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
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
            {/* ── Requisito 1, 2 y 3: Tarjetas Independientes para Vista Móvil con Etiquetas de Contexto ── */}
            <div className="settings-mobile-cards-view mb-4">
              {methods.map((m) => (
                <div key={m.id} className="settings-method-card p-3 mb-3 border rounded-lg bg-surface shadow-xs">
                  
                  {/* Fila 1: Encabezado de Tarjeta (Nombre a la izquierda, Estado a la extrema derecha) */}
                  <div className="flex-between flex-align-center mb-2.5 pb-2 border-bottom">
                    <span className="font-bold text-base color-primary">{m.name}</span>
                    <button
                      type="button"
                      className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'} text-xs font-bold px-3`}
                      onClick={() => handleToggleActive(m)}
                      style={{ borderRadius: '14px', minWidth: '76px' }}
                    >
                      {m.isActive ? 'Activo' : 'Inactivo'}
                    </button>
                  </div>

                  {/* Fila 2: Detalles Secundarios con Etiquetas de Contexto ("Tipo:" y "Requiere Ref.:") */}
                  <div className="flex-between flex-align-center text-xs">
                    <div className="flex-align-center gap-1">
                      <span className="text-muted text-xs">Tipo:</span>
                      <span className="font-medium">{m.isCash ? 'Efectivo' : 'Digital / Banco'}</span>
                    </div>

                    <div className="flex-align-center gap-1.5">
                      <span className="text-muted text-xs">Requiere Ref.:</span>
                      <button
                        type="button"
                        className={`btn btn-xs ${m.requiresReference ? 'btn-primary' : 'btn-outline'} text-xs font-bold`}
                        onClick={() => handleToggleRef(m)}
                        style={{ borderRadius: '10px', padding: '2px 10px' }}
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
                  </tr>
                </thead>
                <tbody>
                  {methods.map((m) => (
                    <tr key={m.id}>
                      <td className="font-medium">{m.name}</td>
                      <td className="text-center">{m.isCash ? 'Efectivo' : 'Digital / Banco'}</td>
                      <td className="text-center">
                        <button
                          type="button"
                          className={`btn btn-sm ${m.requiresReference ? 'btn-primary' : 'btn-outline'}`}
                          onClick={() => handleToggleRef(m)}
                        >
                          {m.requiresReference ? 'Sí' : 'No'}
                        </button>
                      </td>
                      <td className="text-center">
                        <button
                          type="button"
                          className={`btn btn-sm ${m.isActive ? 'btn-primary' : 'btn-danger'}`}
                          onClick={() => handleToggleActive(m)}
                        >
                          {m.isActive ? 'Activo' : 'Inactivo'}
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
              <label className="form-label cursor-pointer flex-align-center gap-2 text-sm mb-0">
                <input
                  type="checkbox"
                  checked={newMethodIsCash}
                  onChange={(e) => setNewMethodIsCash(e.target.checked)}
                />
                <span>Es Efectivo</span>
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
