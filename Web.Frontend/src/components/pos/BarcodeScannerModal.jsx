import { useEffect, useRef, useState } from 'react';
import { BrowserMultiFormatReader, BarcodeFormat, DecodeHintType } from '@zxing/library';
import { Loader2, CameraOff, ShieldAlert, CheckCircle2, AlertCircle, XCircle, Flashlight, FlashlightOff } from 'lucide-react';
import Modal from '../ui/Modal';
import { getProductBySku } from '../../services/productsApi';
import { isValidBarcode } from '../../utils/barcodeValidator';
import { playScanSuccess, playScanWarning, playScanError } from '../../utils/soundEffects';
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

export default function BarcodeScannerModal({ isOpen, onClose, onCodeScanned, resolveProduct = getProductBySku }) {
  const videoRef = useRef(null);
  const onCodeScannedRef = useRef(onCodeScanned);
  const resolveProductRef = useRef(resolveProduct);
  const resultSeqRef = useRef(0);
  const lastCodeRef = useRef(null);
  const lastHitAtRef = useRef(0);
  // true mientras la cámara sigue viendo un código (se actualiza en CADA fotograma,
  // éxito o fallo): permite distinguir "código sostenido" de "re-presentado".
  const codeVisibleRef = useRef(false);
  const lastSuppressedWarnAtRef = useRef(0);

  const [starting, setStarting] = useState(false);
  const [status, setStatus] = useState({ type: 'info', text: '' });
  const [result, setResult] = useState(null);
  const [cooldownKey, setCooldownKey] = useState(0);
  const [hasTorch, setHasTorch] = useState(false);
  const [torchActive, setTorchActive] = useState(false);

  // Keep the latest callbacks without restarting the camera.
  useEffect(() => {
    onCodeScannedRef.current = onCodeScanned;
    resolveProductRef.current = resolveProduct;
  }, [onCodeScanned, resolveProduct]);

  // Al cerrar el modal: limpiar la tarjeta de resultado e invalidar cualquier
  // consulta de producto aún en vuelo (el resultado obsoleto se descarta).
  useEffect(() => {
    if (!isOpen) {
      resultSeqRef.current += 1;
      setResult(null);
    }
  }, [isOpen]);

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

      // Restricción estricta: Ignorar QR, URLs y texto arbitrario
      if (!isValidBarcode(trimmed)) {
        return;
      }

      const now = Date.now();

      // Debounce global entre registros consecutivos.
      if (now - lastHitAtRef.current < MIN_GLOBAL_INTERVAL_MS) return;

      // Enfriamiento SOLO para el mismo código: si el mismo producto se vuelve a
      // presentar dentro de 1.8 s se ignora. Un código distinto entra al instante.
      if (lastCodeRef.current === trimmed && now - lastHitAtRef.current < SAME_CODE_COOLDOWN_MS) {
        setCooldownKey(now);
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
        setCooldownKey(now);
        if (now - lastSuppressedWarnAtRef.current > 500) {
          lastSuppressedWarnAtRef.current = now;
          setStatus({ type: 'warn', text: 'Código repetido — retírelo de la vista para escanearlo de nuevo' });
        }
        return;
      }

      lastCodeRef.current = trimmed;
      lastHitAtRef.current = now;
      codeVisibleRef.current = true;
      setCooldownKey(0);

      // Copy to clipboard (best effort — requires a secure context).
      if (navigator.clipboard?.writeText) {
        navigator.clipboard.writeText(trimmed).catch(() => {});
      }
      setStatus({ type: 'ok', text: `Código copiado: ${trimmed}` });
      onCodeScannedRef.current?.(trimmed);

      // Indicación visual del resultado (espejo del escáner de escritorio):
      // resuelve el producto por SKU exacto y pinta la tarjeta según el estado.
      void showProductResult(trimmed);
    };

    // Resuelve el código contra el catálogo (SKU exacto) y actualiza la tarjeta
    // de resultado. Los resultados obsoletos (llegaron tarde o el modal se cerró)
    // se descartan comparando el número de secuencia.
    const showProductResult = async (code) => {
      const seq = ++resultSeqRef.current;
      try {
        let info = null;
        const resolver = resolveProductRef.current;
        if (resolver) {
          try {
            info = await resolver(code);
          } catch (err) {
            // Espejo del cliente de escritorio: SKU inexistente (404) o no numérico
            // (400) se tratan como "no encontrado", no como un error de lectura.
            if (!(err instanceof Error) || !/^Error (400|404):/.test(err.message)) throw err;
            info = null;
          }
        }

        if (seq !== resultSeqRef.current) return;

        if (!info) {
          playScanWarning();
          setResult({ key: seq, kind: 'notfound', title: 'Producto no encontrado', subtitle: code });
          return;
        }
        if (!info.isActive) {
          playScanError();
          setResult({ key: seq, kind: 'inactive', title: 'Producto inactivo', subtitle: `${code}  •  ${info.name}` });
          return;
        }
        if (info.isCashAdvance) {
          playScanWarning();
          setResult({ key: seq, kind: 'cashadvance', title: info.name, subtitle: `${code}  •  Sistema — requiere captura manual` });
          return;
        }

        playScanSuccess();
        const price = Number(info.priceBsS) > 0 ? `Bs.S ${Number(info.priceBsS).toFixed(2)}` : `USD ${Number(info.priceUSD).toFixed(2)}`;
        setResult({ key: seq, kind: 'found', title: info.name, subtitle: `${code}  •  ${price}` });
      } catch {
        if (seq !== resultSeqRef.current) return;
        playScanError();
        setResult({ key: seq, kind: 'error', title: 'No se pudo leer el código', subtitle: code });
      }
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
        // Restringir decodificación exclusivamente a formatos de códigos de barras estándar (1D)
        const hints = new Map();
        hints.set(DecodeHintType.POSSIBLE_FORMATS, [
          BarcodeFormat.EAN_13,
          BarcodeFormat.EAN_8,
          BarcodeFormat.UPC_A,
          BarcodeFormat.UPC_E,
          BarcodeFormat.CODE_128,
          BarcodeFormat.CODE_39,
          BarcodeFormat.ITF,
          BarcodeFormat.CODABAR,
        ]);

        // Segundo argumento = 0: sin pausa de 500 ms tras cada código detectado,
        // para permitir escanear varios productos uno tras otro rápidamente.
        reader = new BrowserMultiFormatReader(hints, 0);
        reader.timeBetweenDecodingAttempts = ATTEMPT_PACING_MS;

        // Resolución de captura limitada: decodificar fotogramas más pequeños es
        // mucho más rápido (los móviles por defecto entregan 720p/1080p).
        await reader.decodeFromConstraints(
          { video: { facingMode: 'environment', width: { ideal: 960 }, height: { ideal: 540 } } },
          videoRef.current,
          (result) => handleDecoded(result),
        );
        if (disposed) return;

        // Comprobar si la cámara soporta linterna (Torch)
        try {
          const stream = videoRef.current?.srcObject;
          const track = stream?.getVideoTracks?.()[0];
          const capabilities = track?.getCapabilities?.();
          if (capabilities?.torch) {
            setHasTorch(true);
          }
        } catch {
          // Ignorar si getCapabilities no está soportado
        }

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
      setHasTorch(false);
      setTorchActive(false);
      try {
        reader?.reset();
      } catch {
        // Best effort al cerrar.
      }
    };
  }, [isOpen]);

  const toggleTorch = async () => {
    try {
      const stream = videoRef.current?.srcObject;
      const track = stream?.getVideoTracks?.()[0];
      if (track) {
        const nextState = !torchActive;
        await track.applyConstraints({ advanced: [{ torch: nextState }] });
        setTorchActive(nextState);
      }
    } catch (err) {
      console.warn('[Scanner] No se pudo cambiar estado de linterna:', err);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Escanear código de barras" maxWidth="520px">
      <div className="scanner-video-wrap">
        <video ref={videoRef} className="scanner-video" muted playsInline />

        {hasTorch && (
          <button
            type="button"
            className={`scanner-torch-btn ${torchActive ? 'active' : ''}`}
            onClick={toggleTorch}
            title={torchActive ? 'Apagar linterna' : 'Encender linterna'}
            aria-label="Linterna"
          >
            {torchActive ? <FlashlightOff size={18} /> : <Flashlight size={18} />}
          </button>
        )}

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

      {result && (
        <div className={`scanner-result scanner-result--${result.kind}`} key={result.key}>
          {result.kind === 'found' && <CheckCircle2 size={20} />}
          {(result.kind === 'notfound' || result.kind === 'cashadvance') && <AlertCircle size={20} />}
          {(result.kind === 'inactive' || result.kind === 'error') && <XCircle size={20} />}
          <div className="scanner-result-body">
            <span className="scanner-result-title">{result.title}</span>
            {result.subtitle && <span className="scanner-result-subtitle">{result.subtitle}</span>}
            {cooldownKey > 0 && (
              <div className="scanner-cooldown-bar" key={cooldownKey}>
                <div className="scanner-cooldown-fill" />
              </div>
            )}
          </div>
        </div>
      )}

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
