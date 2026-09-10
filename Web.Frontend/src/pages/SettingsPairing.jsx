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
  const [useHttps, setUseHttps] = useState(true);
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
  const currentPort = useHttps ? (pairingInfo?.httpsPort || 5001) : (pairingInfo?.httpPort || 5000);
  const currentScheme = useHttps ? 'https' : 'http';
  const fullUrl = `${currentScheme}://${currentIp}:${currentPort}`;
  const activePayload = `${fullUrl}/?paired=true`;

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
        <div className="grid set-pairing-grid">
          <div className="flex-column flex-align-center text-center p-3 border rounded-lg bg-surface">
            <span className="text-xs font-bold uppercase color-primary mb-2">Escaneo Rápido (Cámara Móvil)</span>
            <div ref={qrRef} className="qr-code-container mb-2" />
            <p className="text-xs text-muted mb-3 set-qr-hint">
              Apunta con la cámara de tu teléfono o tablet para abrir y sincronizar el Punto de Venta.
            </p>
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
            <label className="cursor-pointer flex-align-center gap-2 text-xs font-medium w-full text-left p-2 rounded bg-background border">
              <input
                type="checkbox"
                checked={useHttps}
                onChange={(e) => setUseHttps(e.target.checked)}
              />
              <span>Usar HTTPS (Puerto {pairingInfo?.httpsPort || 5001} para cámara web en móviles)</span>
            </label>
          </div>

          <div className="flex-column gap-3 justify-center">
            <h4 className="text-sm font-bold flex-align-center gap-2 mb-1">
              <Server size={16} className="color-primary" /> Datos para Conexión Manual
            </h4>
            <div className="p-3 border rounded-lg bg-surface">
              <div className="text-xs text-muted mb-0.5">Nombre del Servidor / Hostname:</div>
              <div className="font-bold text-sm">{pairingInfo?.serverName || pairingInfo?.machineName || 'POS-SERVER'}</div>
            </div>
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
            <div className="p-3 border rounded-lg text-xs flex-align-start gap-2 set-wifi-alert">
              <Wifi size={18} className="flex-shrink-0 set-wifi-icon" />
              <span>
                <strong>Nota de Red:</strong> Asegúrese de que los teléfonos, tablets o terminales estén conectados a la <strong>misma red Wi-Fi / LAN</strong> que este equipo servidor.
              </span>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}