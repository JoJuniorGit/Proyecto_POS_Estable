import { useEffect, useRef, useState } from 'react';
import { BrowserMultiFormatReader } from '@zxing/library';
import { Loader2, CameraOff, ShieldAlert } from 'lucide-react';
import Modal from '../ui/Modal';
import './BarcodeScannerModal.css';

// Same-code cool-down so a barcode held still in front of the camera
// doesn't keep re-adding the product to the cart.
const SAME_CODE_COOLDOWN_MS = 3000;

// La API de cámara solo existe en contextos seguros: HTTPS, localhost o loopback (127.0.0.1).
// Con http:// + IP de red (ej. http://192.168.1.10:5000) navigator.mediaDevices no existe.
const INSECURE_CONTEXT_MESSAGE =
  'La cámara requiere una conexión segura (HTTPS o localhost). Esta página se abrió con http:// y una IP de red. ' +
  'Abra el sistema en esta PC con http://localhost:5000, o configure HTTPS para usarlo desde otros dispositivos.';

export default function BarcodeScannerModal({ isOpen, onClose, onCodeScanned }) {
  const videoRef = useRef(null);
  const onCodeScannedRef = useRef(onCodeScanned);
  const lastCodeRef = useRef(null);
  const lastDecodedAtRef = useRef(0);

  const [starting, setStarting] = useState(false);
  const [status, setStatus] = useState({ type: 'info', text: '' });

  // Keep the latest callback without restarting the camera.
  useEffect(() => {
    onCodeScannedRef.current = onCodeScanned;
  }, [onCodeScanned]);

  useEffect(() => {
    if (!isOpen) return;

    let reader = null;
    let disposed = false;

    const handleDecoded = (text) => {
      if (!text || !text.trim() || disposed) return;
      const now = Date.now();
      if (lastCodeRef.current === text && now - lastDecodedAtRef.current < SAME_CODE_COOLDOWN_MS) {
        return;
      }
      lastCodeRef.current = text;
      lastDecodedAtRef.current = now;

      const trimmed = text.trim();
      // Copy to clipboard (best effort — requires a secure context).
      if (navigator.clipboard?.writeText) {
        navigator.clipboard.writeText(trimmed).catch(() => {});
      }
      setStatus({ type: 'ok', text: `Código copiado: ${trimmed}` });
      onCodeScannedRef.current?.(trimmed);
    };

    const start = async () => {
      setStarting(true);
      setStatus({ type: 'info', text: 'Solicitando acceso a la cámara…' });

      // Guarda explícita: sin contexto seguro no hay API de cámara.
      if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        setStarting(false);
        setStatus({ type: 'error', text: INSECURE_CONTEXT_MESSAGE });
        return;
      }

      try {
        reader = new BrowserMultiFormatReader();
        // deviceId null -> cámara predeterminada (facingMode 'environment' en móviles).
        await reader.decodeFromVideoDevice(null, videoRef.current, (result) => {
          if (result) handleDecoded(result.getText());
        });
        if (disposed) return;
        setStarting(false);
        setStatus({ type: 'info', text: 'Apunte la cámara a un código de barras…' });
      } catch (err) {
        if (disposed) return;
        setStarting(false);
        setStatus({ type: 'error', text: friendlyCameraError(err) });
      }
    };

    start();

    return () => {
      disposed = true;
      try {
        reader?.reset();
      } catch {
        // Best effort al cerrar.
      }
    };
  }, [isOpen]);

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Escanear código de barras" maxWidth="520px">
      <div className="scanner-video-wrap">
        <video ref={videoRef} className="scanner-video" muted playsInline />

        {starting && (
          <div className="scanner-overlay">
            <Loader2 className="animate-spin" size={28} />
            <span>Iniciando cámara…</span>
          </div>
        )}

        {!starting && status.type === 'error' && (
          <div className="scanner-overlay error">
            {status.text === INSECURE_CONTEXT_MESSAGE ? <ShieldAlert size={28} /> : <CameraOff size={28} />}
            <span>{status.text}</span>
          </div>
        )}

        {!starting && status.type !== 'error' && <div className="scanner-frame" />}
      </div>

      <div className={`scanner-status ${status.type}`}>{status.text}</div>

      <p className="scanner-privacy">
        Privacidad: la cámara se usa solo para escanear. No se guardan ni se envían imágenes.
      </p>

      <div className="customer-modal-footer">
        <button type="button" className="btn btn-outline" onClick={onClose} style={{ flex: 1 }}>
          Cerrar
        </button>
      </div>
    </Modal>
  );
}

function friendlyCameraError(err) {
  const name = err?.name || '';
  const message = err?.message || '';

  if (name === 'NotAllowedError' || name === 'PermissionDeniedError' || message.includes('Permission denied')) {
    return 'Acceso a la cámara denegado. Permita el acceso en el navegador (o en los ajustes de privacidad de Windows) y vuelva a intentarlo.';
  }
  if (name === 'NotFoundError' || name === 'DevicesNotFoundError' || message.includes('not found') || message.includes('No camera')) {
    return 'No se encontró una cámara en este dispositivo.';
  }
  if (name === 'NotReadableError' || name === 'TrackStartError' || message.includes('in use')) {
    return 'No se pudo acceder a la cámara; puede estar en uso por otra aplicación.';
  }
  // Errores de contexto inseguro (navigator.mediaDevices indefinido).
  if (message.includes('getUserMedia') || message.includes('mediaDevices')) {
    return INSECURE_CONTEXT_MESSAGE;
  }
  return `No se pudo iniciar la cámara: ${message || 'error desconocido'}`;
}
