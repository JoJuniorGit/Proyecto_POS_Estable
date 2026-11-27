import { useState, useEffect, useRef } from 'react';
import { api } from '../services/api';
import { BrowserQRCodeSvgWriter } from '@zxing/library';
import { QrCode, RefreshCw, Loader2, Server, Copy, Check, Wifi } from 'lucide-react';
import { copyTextToClipboard } from '../utils/clipboard';

// 8.7-L6: pinta el QR de emparejamiento dentro de un contenedor dado (reutilizable de forma
// aislada para pruebas y con limpieza previa del contenedor).
export function renderPairingQr(container, payload, onError) {
  try {
    const writer = new BrowserQRCodeSvgWriter();
    const svg = writer.write(payload, 200, 200);
    svg.style.backgroundColor = '#FFFFFF';
    svg.style.display = 'block';
    svg.style.borderRadius = '4px';

    const bgRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    bgRect.setAttribute('width', '100%');
    bgRect.setAttribute('height', '100%');
    bgRect.setAttribute('fill', '#FFFFFF');
    svg.insertBefore(bgRect, svg.firstChild);

    container.innerHTML = '';
    container.appendChild(svg);
    return svg;
  } catch (err) {
    if (onError) onError(err);
    return null;
  }
}

export default function SettingsPairing({ pairingInfo, setPairingInfo }) {
  const [selectedInterface, setSelectedInterface] = useState(null);
  const useHttps = pairingInfo?.isHttpsEnabled === true;
  const [loadingPairing, setLoadingPairing] = useState(false);
  const [copiedIp, setCopiedIp] = useState(false);
  const [copiedUrl, setCopiedUrl] = useState(false);
  const qrRef = useRef(null);

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
    loadPairingInfo();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const currentIp = selectedInterface?.ipAddress || pairingInfo?.primaryIpAddress || (typeof window !== 'undefined' ? window.location.hostname : 'localhost') || '127.0.0.1';
  const currentPort = pairingInfo?.httpsPort || 5001;
  const fullUrl = useHttps ? `https://${currentIp}:${currentPort}` : '';

  let activePayload = useHttps ? `${fullUrl}/?paired=true` : '';
  if (useHttps && pairingInfo?.qrPayload) {
    try {
      const url = new URL(pairingInfo.qrPayload);
      const pairToken = url.searchParams.get('pair');
      if (pairToken) {
        activePayload = `${fullUrl}/?paired=true&pair=${pairToken}`;
      }
    } catch {}
  }

  useEffect(() => {
    if (!qrRef.current || !activePayload) return;
    // 8.7-L6: renderizado del QR centralizado en renderPairingQr (evita manipulación DOM dispersa).
    renderPairingQr(qrRef.current, activePayload, (err) => {
      console.error('[SettingsPage] Error generando QR SVG:', err);
    });
  }, [activePayload, loadingPairing]);

  const handleCopy = async (text, type) => {
    // 8.7-L6: copia centralizada (Clipboard API primero, fallback legacy solo como último recurso).
    const copied = await copyTextToClipboard(text);
    if (!copied) return;
    if (type === 'ip') {
      setCopiedIp(true);
      setTimeout(() => setCopiedIp(false), 2000);
    } else if (type === 'url') {
      setCopiedUrl(true);
      setTimeout(() => setCopiedUrl(false), 2000);
    }
  };

  return (
    <section className="set-card">
      <header className="set-card-head">
        <span className="set-card-icon" aria-hidden="true">
          <QrCode size={18} />
        </span>
        <div className="set-card-heading">
          <h3 className="set-card-title">Emparejamiento de Dispositivos Móviles / Web</h3>
          <p className="set-card-subtitle">
            Vincule teléfonos, tablets y terminales mediante el código QR o una conexión manual a la red local.
          </p>
        </div>
        <div className="set-card-head-actions">
          <span className={`set-status-pill ${useHttps ? 'set-status-pill--success' : 'set-status-pill--neutral'}`}>
            {useHttps ? 'HTTPS activo' : 'Red local'}
          </span>
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={loadPairingInfo}
            disabled={loadingPairing}
            title="Recargar datos de red"
          >
            <RefreshCw size={14} className={loadingPairing ? 'animate-spin' : ''} aria-hidden="true" />
            <span>Actualizar</span>
          </button>
        </div>
      </header>

      {loadingPairing ? (
        <div className="set-loading">
          <Loader2 className="animate-spin" size={24} />
          <span>Cargando datos de emparejamiento de red...</span>
        </div>
      ) : (
        <div className="set-pairing-grid">
          <div className="set-qr-card">
            <span className="set-panel-kicker">Escaneo Rápido (Cámara Móvil)</span>
            <div className="set-qr-frame">
              <div ref={qrRef} className="qr-code-container" />
            </div>
            <p className="set-qr-hint">
              Apunta con la cámara de tu teléfono o tablet para abrir y sincronizar el Punto de Venta.
            </p>

            {pairingInfo?.networkInterfaces?.length > 1 && (
              <div className="form-group">
                <label className="form-label" htmlFor="pairing-interface">Adaptador de Red:</label>
                <select
                  id="pairing-interface"
                  className="form-input"
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

            <div className={`set-callout ${useHttps ? 'set-callout--success' : 'set-callout--warning'}`}>
              <span>
                {useHttps
                  ? `HTTPS activo (puerto ${pairingInfo?.httpsPort || 5001}); la conexión remota usa canal cifrado.`
                  : 'HTTPS no está disponible; solo se anuncian direcciones HTTP del entorno local.'}
              </span>
            </div>
          </div>

          <div className="set-connection-card">
            <h4 className="set-panel-title">
              <Server size={16} className="color-primary" aria-hidden="true" />
              Datos para Conexión Manual
            </h4>

            <div className="set-info-card">
              <span className="set-info-label">Nombre del Servidor / Hostname:</span>
              <span className="set-info-value">{pairingInfo?.serverName || pairingInfo?.machineName || 'POS-SERVER'}</span>
            </div>

            <div className="set-info-card">
              <div className="set-info-row">
                <div>
                  <span className="set-info-label">Dirección IP Local:</span>
                  <span className="set-code set-code--accent">{currentIp}</span>
                </div>
                <button
                  type="button"
                  className={`btn btn-outline btn-sm set-copy-btn${copiedIp ? ' is-copied' : ''}`}
                  onClick={() => handleCopy(currentIp, 'ip')}
                >
                  {copiedIp ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
                  <span>{copiedIp ? 'Copiado' : 'Copiar IP'}</span>
                </button>
              </div>
            </div>

            <div className="set-info-card">
              <div className="set-info-row">
                <span className="set-info-label">URL Completa para Navegador:</span>
                <button
                  type="button"
                  className={`btn btn-primary btn-sm set-copy-btn${copiedUrl ? ' is-copied' : ''}`}
                  onClick={() => handleCopy(fullUrl, 'url')}
                  disabled={!useHttps}
                >
                  {copiedUrl ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
                  <span>{copiedUrl ? 'Copiado' : 'Copiar URL'}</span>
                </button>
              </div>
              <span className="set-code">
                {useHttps ? fullUrl : 'HTTPS no disponible; emparejamiento remoto deshabilitado.'}
              </span>
            </div>

            <div className="set-callout set-callout--info set-wifi-alert">
              <Wifi size={18} className="set-wifi-icon" aria-hidden="true" />
              <span>
                <strong>Nota de Red:</strong> Asegúrese de que los teléfonos, tablets o terminales estén
                conectados a la <strong>misma red Wi-Fi / LAN</strong> que este equipo servidor.
              </span>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
