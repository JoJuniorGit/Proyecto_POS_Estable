import { useEffect, useRef, useState } from 'react';
import { BrowserMultiFormatReader } from '@zxing/library';
import { Loader2, CameraOff, ShieldAlert } from 'lucide-react';
import Modal from '../ui/Modal';
import './BarcodeScannerModal.css';

// La API de cámara solo existe en contextos seguros: HTTPS, localhost o loopback (127.0.0.1).
// Con http:// + IP de red (ej. http://192.168.1.10:5000) navigator.mediaDevices no existe.
const INSECURE_CONTEXT_MESSAGE =
  'La cámara requiere una conexión segura (HTTPS o localhost). Esta página se abrió con http:// y una IP de red. ' +
  'Abra el sistema en esta PC con http://localhost:5000, o configure HTTPS para usarlo desde otros dispositivos.';

// Debounce mínimo entre dos registros cualesquiera (evita dobles disparos en fotogramas seguidos).
const MIN_GLOBAL_INTERVAL_MS = 150;
// Enfriamiento SOLO para el mismo código re-presentado de inmediato: evita dobles disparos
// accidentales del mismo producto. Un producto DISTINTO se escanea al instante, sin espera.
const SAME_CODE_COOLDOWN_MS = 1800;
// Pausa mínima entre intentos de decodificación fallidos (ritmo ~16 intentos/seg, sin saturar la CPU).
const ATTEMPT_PACING_MS = 60;

export default function BarcodeScannerModal({ isOpen, onClose, onCodeScanned }) {
  const videoRef = useRef(null);
  const onCodeScannedRef = useRef(onCodeScanned);
  const lastCodeRef = useRef(null);
  const lastHitAtRef = useRef(0);
  // true mientras la cámara sigue viendo un código (se actualiza en CADA fotograma,
  // éxito o fallo): permite distinguir "código sostenido" de "re-presentado".
  const codeVisibleRef = useRef(false);
  const lastSuppressedWarnAtRef = useRef(0);

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

    const handleDecoded = (result) => {
      if (disposed) return;

      // Fotograma sin código: la cámara ya no está viendo uno en este momento.
      if (!result || !result.getText || !result.getText().trim()) {
        codeVisibleRef.current = false;
        return;
      }

      const trimmed = result.getText().trim();
      const now = Date.now();

      // Debounce global entre registros consecutivos.
      if (now - lastHitAtRef.current < MIN_GLOBAL_INTERVAL_MS) return;

      // Enfriamiento SOLO para el mismo código: si el mismo producto se vuelve a
      // presentar dentro de 1.8 s se ignora. Un código distinto entra al instante.
      if (lastCodeRef.current === trimmed && now - lastHitAtRef.current < SAME_CODE_COOLDOWN_MS) {
        if (now - lastSuppressedWarnAtRef.current > 500) {
          lastSuppressedWarnAtRef.current = now;
          setStatus({ type: 'warn', text: 'Código repetido — espere un momento antes de volver a escanearlo' });
        }
        return;
      }

      // El mismo código sigue visible (nunca salió de la vista): se ignora para no
      // volver a agregar el producto sin querer. Solo se re-escanea si el código
      // sale de la vista (al menos un fotograma sin él) o aparece otro distinto.
      if (lastCodeRef.current === trimmed && codeVisibleRef.current) {
        if (now - lastSuppressedWarnAtRef.current > 500) {
          lastSuppressedWarnAtRef.current = now;
          setStatus({ type: 'warn', text: 'Código repetido — retírelo de la vista para escanearlo de nuevo' });
        }
        return;
      }

      lastCodeRef.current = trimmed;
      lastHitAtRef.current = now;
      codeVisibleRef.current = true;

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
        // Segundo argumento = 0: sin pausa de 500 ms tras cada código detectado,
        // para permitir escanear varios productos uno tras otro rápidamente.
        reader = new BrowserMultiFormatReader(null, 0);
        reader.timeBetweenDecodingAttempts = ATTEMPT_PACING_MS;

        // Resolución de captura limitada: decodificar fotogramas más pequeños es
        // mucho más rápido (los móviles por defecto entregan 720p/1080p).
        await reader.decodeFromConstraints(
          { video: { facingMode: 'environment', width: { ideal: 960 }, height: { ideal: 540 } } },
          videoRef.current,
          (result) => handleDecoded(result),
        );
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
