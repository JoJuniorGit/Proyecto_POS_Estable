import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { BrowserMultiFormatReader, BarcodeFormat, DecodeHintType } from '@zxing/library';
import { 
  AlertCircle, 
  XCircle, 
  Plus, 
  Minus, 
  Barcode 
} from 'lucide-react';
import Modal from '../ui/Modal';
import BarcodeScannerHud from './BarcodeScannerHud';
import BarcodeScannerControls from './BarcodeScannerControls';
import { getProductBySku } from '../../services/productsApi';
import { isValidBarcode } from '../../utils/barcodeValidator';
import { playScanSuccess, playScanWarning, playScanError, closeAudioContext } from '../../utils/soundEffects';
import { checkBarcodeDetectorSupport, createNativeBarcodeDetector } from '../../utils/nativeBarcodeScanner';
import { isLaptopOrDesktopEnvironment, processMultiPassLaptopFrame } from '../../utils/laptopVisionEnhancer';
import { formatBsS, formatUSD } from '../../utils/formatters';
import './BarcodeScannerModal.css';

const INSECURE_CONTEXT_MESSAGE =
  'La cámara requiere una conexión segura (HTTPS o localhost). Esta página se abrió con http:// y una IP de red. ' +
  'Abra el sistema en esta PC con https://localhost:5001, o configure HTTPS para usarlo desde otros dispositivos.';

const MIN_GLOBAL_INTERVAL_MS = 150;
const SAME_CODE_COOLDOWN_MS = 2000;
const ATTEMPT_PACING_MS = 50;

export default function BarcodeScannerModal({ 
  isOpen, 
  onClose, 
  onCodeScanned, 
  resolveProduct = getProductBySku,
  currentSale = null,
  onUpdateQuantity = null,
}) {
  const videoRef = useRef(null);
  const filterCanvasRef = useRef(null);
  const onCodeScannedRef = useRef(onCodeScanned);
  const resolveProductRef = useRef(resolveProduct);
  const resultSeqRef = useRef(0);
  const lastCodeRef = useRef(null);
  const lastHitAtRef = useRef(0);
  const codeVisibleRef = useRef(false);
  const lastSuppressedWarnAtRef = useRef(0);
  const activeStreamRef = useRef(null);
  const zxingReaderRef = useRef(null);
  const nativeActiveRef = useRef(false);
  const laptopVisionActiveRef = useRef(false);
  const sessionCancelTokenRef = useRef(0);
  const boundingBoxRef = useRef(null);
  const boundingBoxTimerRef = useRef(null);

  // Detección de entorno: Laptop/PC vs Mobile
  const isLaptop = useMemo(() => isLaptopOrDesktopEnvironment(), []);

  const [starting, setStarting] = useState(false);
  const [status, setStatus] = useState({ type: 'info', text: '' });
  const [result, setResult] = useState(null);
  const [cooldownKey, setCooldownKey] = useState(0);
  const [recentScannedProductIds, setRecentScannedProductIds] = useState([]);
  const [laptopEnhancement, setLaptopEnhancement] = useState(true);
  
  // Controles de Hardware
  const [hasTorch, setHasTorch] = useState(false);
  const [torchActive, setTorchActive] = useState(false);
  const [zoomCapabilities, setZoomCapabilities] = useState(null);
  const [currentZoom, setCurrentZoom] = useState(1);
  const [videoDevices, setVideoDevices] = useState([]);
  const [currentDeviceId, setCurrentDeviceId] = useState('');
  const [engineType, setEngineType] = useState('zxing'); // 'native' | 'zxing'
  const [scanCount, setScanCount] = useState(0);

  // Data Binding puro: los últimos 3 productos derivan su cantidad del carrito central
  const recentScannedItems = useMemo(() => {
    return recentScannedProductIds
      .map((prodId) => {
        const line = currentSale?.items?.find((i) => i.productId === prodId);
        return line ? { ...line } : null;
      })
      .filter(Boolean);
  }, [recentScannedProductIds, currentSale?.items]);

  useEffect(() => {
    onCodeScannedRef.current = onCodeScanned;
    resolveProductRef.current = resolveProduct;
  }, [onCodeScanned, resolveProduct]);

  // Detención de recursos de hardware de la cámara
  const stopActiveStream = useCallback(() => {
    sessionCancelTokenRef.current += 1;
    nativeActiveRef.current = false;
    laptopVisionActiveRef.current = false;

    if (zxingReaderRef.current) {
      try {
        zxingReaderRef.current.reset();
      } catch (e) {
        console.warn('[Scanner] Error deteniendo ZXing:', e);
      }
      zxingReaderRef.current = null;
    }

    if (activeStreamRef.current) {
      try {
        activeStreamRef.current.getTracks().forEach((t) => {
          try { t.stop(); } catch {}
        });
      } catch {}
      activeStreamRef.current = null;
    }

    if (videoRef.current) {
      try {
        const streamObj = videoRef.current.srcObject;
        if (streamObj && typeof streamObj.getTracks === 'function') {
          streamObj.getTracks().forEach((t) => {
            try { t.stop(); } catch {}
          });
        }
      } catch {}

      try {
        videoRef.current.srcObject = null;
        videoRef.current.pause?.();
        videoRef.current.removeAttribute('src');
      } catch {}
    }

    setHasTorch(false);
    setTorchActive(false);
    setZoomCapabilities(null);
  }, []);

  const handleClose = useCallback(() => {
    stopActiveStream();
    closeAudioContext();
    onClose?.();
  }, [stopActiveStream, onClose]);

  useEffect(() => {
    if (!isOpen) {
      stopActiveStream();
      closeAudioContext();
      resultSeqRef.current += 1;
      setResult(null);
      setScanCount(0);
      setRecentScannedProductIds([]);
    }
  }, [isOpen, stopActiveStream]);

  const triggerBoundingBox = useCallback((cornerPoints) => {
    if (cornerPoints && cornerPoints.length >= 4) {
      boundingBoxRef.current = cornerPoints;
      if (boundingBoxTimerRef.current) clearTimeout(boundingBoxTimerRef.current);
      boundingBoxTimerRef.current = setTimeout(() => {
        boundingBoxRef.current = null;
      }, 700);
    }
  }, []);

  const handleDecodedCode = useCallback(async (trimmed, cornerPoints = null) => {
    if (!isValidBarcode(trimmed)) return;

    const now = Date.now();
    if (now - lastHitAtRef.current < MIN_GLOBAL_INTERVAL_MS) return;

    // Cooldown de 2.0 segundos exactos para el mismo código
    if (lastCodeRef.current === trimmed && now - lastHitAtRef.current < SAME_CODE_COOLDOWN_MS) {
      setCooldownKey(now);
      if (now - lastSuppressedWarnAtRef.current > 500) {
        lastSuppressedWarnAtRef.current = now;
        setStatus({ type: 'warn', text: 'Código repetido — espere 2.0s antes de volver a escanearlo' });
      }
      return;
    }

    if (lastCodeRef.current === trimmed && codeVisibleRef.current) {
      setCooldownKey(now);
      return;
    }

    codeVisibleRef.current = true;
    lastCodeRef.current = trimmed;
    lastHitAtRef.current = now;
    setCooldownKey(now);
    setScanCount((c) => c + 1);

    if (cornerPoints) triggerBoundingBox(cornerPoints);

    onCodeScannedRef.current?.(trimmed);

    const seq = ++resultSeqRef.current;
    setResult({ key: seq, kind: 'loading', title: 'Buscando producto…', subtitle: trimmed, rawProduct: null });

    try {
      const info = await resolveProductRef.current(trimmed);
      if (seq !== resultSeqRef.current) return;

      if (!info) {
        playScanWarning();
        setResult({ key: seq, kind: 'notfound', title: 'Producto no encontrado', subtitle: `${trimmed}  •  No registrado` });
        return;
      }

      if (info.isActive === false) {
        playScanError();
        setResult({ key: seq, kind: 'inactive', title: info.name, subtitle: `${trimmed}  •  Inactivo / Deshabilitado` });
        return;
      }

      playScanSuccess();
      const price = Number(info.priceBsS) > 0 ? formatBsS(Number(info.priceBsS)) : formatUSD(Number(info.priceUSD));
      
      // Actualizar la lista de los últimos 3 productos distintos escaneados
      setRecentScannedProductIds((prev) => [info.id, ...prev.filter((id) => id !== info.id)].slice(0, 3));

      setResult({ 
        key: seq, 
        kind: 'found', 
        title: info.name, 
        subtitle: `${trimmed}  •  ${price}`,
        rawProduct: info
      });
    } catch {
      if (seq !== resultSeqRef.current) return;
      playScanError();
      setResult({ key: seq, kind: 'error', title: 'No se pudo leer el código', subtitle: trimmed });
    }
  }, [triggerBoundingBox]);

  const applyHardwareCapabilities = (track, preserveTorch = false) => {
    if (!track) return;
    try {
      const capabilities = track.getCapabilities?.() || {};

      if (capabilities.torch) {
        setHasTorch(true);
        if (preserveTorch && torchActive) {
          track.applyConstraints({ advanced: [{ torch: true }] }).catch(() => {});
        }
      } else {
        setHasTorch(false);
        setTorchActive(false);
      }

      if (capabilities.zoom) {
        setZoomCapabilities(capabilities.zoom);
        setCurrentZoom(1);
      } else {
        setZoomCapabilities(null);
      }
    } catch {
      setHasTorch(false);
      setZoomCapabilities(null);
    }
  };

  const startScanningSession = async (targetDeviceId = '') => {
    const currentToken = ++sessionCancelTokenRef.current;
    setStarting(true);
    setStatus({ type: 'info', text: 'Iniciando cámara…' });

    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
      setStarting(false);
      setStatus({ type: 'error', text: INSECURE_CONTEXT_MESSAGE });
      return;
    }

    try {
      try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        if (sessionCancelTokenRef.current !== currentToken) return;
        const videoInputs = devices.filter((d) => d.kind === 'videoinput');
        setVideoDevices(videoInputs);
      } catch {}

      stopActiveStream();
      sessionCancelTokenRef.current = currentToken;

      const constraints = {
        video: targetDeviceId 
          ? { deviceId: { exact: targetDeviceId }, width: { ideal: 1920, min: 1280 }, height: { ideal: 1080, min: 720 }, frameRate: { ideal: 30 } }
          : { facingMode: { ideal: 'environment' }, width: { ideal: 1920, min: 1280 }, height: { ideal: 1080, min: 720 }, frameRate: { ideal: 30 } }
      };

      const stream = await navigator.mediaDevices.getUserMedia(constraints);

      if (sessionCancelTokenRef.current !== currentToken) {
        stream.getTracks().forEach((t) => {
          try { t.stop(); } catch {}
        });
        return;
      }

      activeStreamRef.current = stream;

      if (videoRef.current) {
        videoRef.current.srcObject = stream;
        await videoRef.current.play().catch(() => {});
      }

      if (sessionCancelTokenRef.current !== currentToken) {
        stopActiveStream();
        return;
      }

      const track = stream.getVideoTracks()[0];
      if (track) {
        const settings = track.getSettings?.() || {};
        if (settings.deviceId) setCurrentDeviceId(settings.deviceId);
        applyHardwareCapabilities(track, true);
      }

      // Móvil: Detección acelerada por GPU si soporta BarcodeDetector nativo
      const isNativeSupported = await checkBarcodeDetectorSupport();
      if (sessionCancelTokenRef.current !== currentToken) {
        stopActiveStream();
        return;
      }

      if (isNativeSupported && !isLaptop) {
        const nativeDetector = createNativeBarcodeDetector();
        if (nativeDetector) {
          setEngineType('native');
          setStarting(false);
          setStatus({ type: 'info', text: 'Apunte la cámara a un código de barras…' });

          nativeActiveRef.current = true;
          const runNativeLoop = async () => {
            if (!nativeActiveRef.current || sessionCancelTokenRef.current !== currentToken || !videoRef.current || !activeStreamRef.current) return;

            try {
              if (videoRef.current.readyState >= 2) {
                const barcodes = await nativeDetector.detect(videoRef.current);
                if (barcodes && barcodes.length > 0) {
                  const first = barcodes[0];
                  if (first.rawValue) {
                    handleDecodedCode(first.rawValue.trim(), first.cornerPoints);
                  }
                } else {
                  codeVisibleRef.current = false;
                }
              }
            } catch {}

            if (nativeActiveRef.current && sessionCancelTokenRef.current === currentToken) {
              setTimeout(runNativeLoop, ATTEMPT_PACING_MS);
            }
          };

          runNativeLoop();
          return;
        }
      }

      // Fallback: ZXing Library Reader con TRY_HARDER y Pipeline Multi-Pass
      setEngineType('zxing');
      const hints = new Map([
        [DecodeHintType.POSSIBLE_FORMATS, [
          BarcodeFormat.EAN_13,
          BarcodeFormat.EAN_8,
          BarcodeFormat.UPC_A,
          BarcodeFormat.UPC_E,
          BarcodeFormat.CODE_128,
          BarcodeFormat.CODE_39,
          BarcodeFormat.ITF,
          BarcodeFormat.CODABAR,
        ]],
        [DecodeHintType.TRY_HARDER, true]
      ]);

      const reader = new BrowserMultiFormatReader(hints, 0);
      reader.timeBetweenDecodingAttempts = ATTEMPT_PACING_MS;
      zxingReaderRef.current = reader;

      if (videoRef.current && activeStreamRef.current) {
        if (isLaptop && laptopEnhancement && filterCanvasRef.current) {
          laptopVisionActiveRef.current = true;
          let passCounter = 0;

          const runLaptopVisionLoop = () => {
            if (!laptopVisionActiveRef.current || sessionCancelTokenRef.current !== currentToken || !videoRef.current || !activeStreamRef.current) return;

            try {
              if (videoRef.current.readyState >= 2 && filterCanvasRef.current) {
                const currentPass = passCounter % 4;
                passCounter++;

                processMultiPassLaptopFrame(videoRef.current, filterCanvasRef.current, currentPass);
                
                try {
                  const zxingResult = reader.decode(filterCanvasRef.current);
                  if (zxingResult?.getText && zxingResult.getText().trim()) {
                    handleDecodedCode(zxingResult.getText().trim());
                  }
                } catch {
                  codeVisibleRef.current = false;
                }
              }
            } catch {}

            if (laptopVisionActiveRef.current && sessionCancelTokenRef.current === currentToken) {
              setTimeout(runLaptopVisionLoop, ATTEMPT_PACING_MS);
            }
          };

          runLaptopVisionLoop();
        } else {
          reader.decodeFromStream(
            activeStreamRef.current,
            videoRef.current,
            (zxingResult) => {
              if (!zxingResult?.getText || !zxingResult.getText().trim()) {
                codeVisibleRef.current = false;
                return;
              }
              handleDecodedCode(zxingResult.getText().trim());
            }
          );
        }
      }

      setStarting(false);
      setStatus({ type: 'info', text: 'Apunte la cámara a un código de barras…' });
    } catch (err) {
      if (sessionCancelTokenRef.current === currentToken) {
        setStarting(false);
        setStatus({ type: 'error', text: friendlyCameraError(err) });
      }
    }
  };

  useEffect(() => {
    if (!isOpen) return;

    startScanningSession(currentDeviceId);

    return () => {
      stopActiveStream();
      closeAudioContext();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, stopActiveStream]);

  const toggleTorch = async () => {
    try {
      const track = activeStreamRef.current?.getVideoTracks?.()[0];
      if (track) {
        const next = !torchActive;
        await track.applyConstraints({ advanced: [{ torch: next }] });
        setTorchActive(next);
      }
    } catch (err) {
      console.warn('[Scanner] Error al alternar linterna:', err);
    }
  };

  const applyZoom = async (zoomLevel) => {
    try {
      const track = activeStreamRef.current?.getVideoTracks?.()[0];
      if (track) {
        await track.applyConstraints({ advanced: [{ zoom: zoomLevel }] });
        setCurrentZoom(zoomLevel);
      }
    } catch (err) {
      console.warn('[Scanner] Error al aplicar zoom:', err);
    }
  };

  const switchCamera = async (newDeviceId) => {
    const prevDeviceId = currentDeviceId;
    try {
      await startScanningSession(newDeviceId);
    } catch (err) {
      console.error('[Scanner] Falló cambio de cámara, revirtiendo:', err);
      if (prevDeviceId) {
        await startScanningSession(prevDeviceId);
      }
    }
  };

  const handleToggleLaptopEnhancement = () => {
    setLaptopEnhancement((v) => !v);
    startScanningSession(currentDeviceId);
  };

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title="Escanear código de barras" maxWidth="540px">
      <div className="scanner-container">
        {/* Canvas de procesamiento oculto para filtros de laptop */}
        <canvas ref={filterCanvasRef} className="scanner-filter-canvas" />

        {/* Visor de Video + HUD Canvas Superpuesto */}
        <BarcodeScannerHud
          videoRef={videoRef}
          isOpen={isOpen}
          isLaptop={isLaptop}
          starting={starting}
          status={status}
          insecureContextMessage={INSECURE_CONTEXT_MESSAGE}
          boundingBoxRef={boundingBoxRef}
        />

        {/* Controles Flotantes Superiores y Barra de Zoom */}
        <BarcodeScannerControls
          isLaptop={isLaptop}
          laptopEnhancement={laptopEnhancement}
          onToggleLaptopEnhancement={handleToggleLaptopEnhancement}
          videoDevices={videoDevices}
          currentDeviceId={currentDeviceId}
          onSwitchCamera={switchCamera}
          hasTorch={hasTorch}
          torchActive={torchActive}
          onToggleTorch={toggleTorch}
          scanCount={scanCount}
          zoomCapabilities={zoomCapabilities}
          currentZoom={currentZoom}
          onApplyZoom={applyZoom}
        />

        {/* Barra de Cooldown de 2.0 Segundos */}
        {cooldownKey > 0 && (
          <div className="scanner-cooldown-bar" key={cooldownKey}>
            <div className="scanner-cooldown-fill" />
          </div>
        )}

        {/* Panel de Historial Visual: Últimos 3 Productos Distintos Agrupados */}
        {recentScannedItems.length > 0 && (
          <div className="scanner-recent-tray">
            <div className="scanner-recent-title">
              <Barcode size={14} />
              <span>Últimos escaneados ({recentScannedItems.length}/3)</span>
            </div>
            <div className="scanner-recent-cards">
              {recentScannedItems.map((item) => (
                <div key={item.id} className="scanner-recent-card">
                  <div className="scanner-recent-info">
                    <span className="scanner-recent-name">{item.productName}</span>
                    <span className="scanner-recent-price">
                      {Number(item.unitPriceBsS) > 0 ? formatBsS(Number(item.unitPriceBsS)) : formatUSD(Number(item.unitPrice))}
                    </span>
                  </div>
                  <div className="scanner-qty-stepper">
                    <button
                      type="button"
                      className="scanner-qty-btn"
                      disabled={item.quantity <= 1}
                      onClick={() => {
                        if (item.quantity > 1) {
                          onUpdateQuantity?.(item.id, item.quantity - 1);
                        }
                      }}
                      title={item.quantity <= 1 ? "Cantidad mínima" : "Disminuir cantidad"}
                      aria-label="Disminuir cantidad"
                    >
                      <Minus size={13} />
                    </button>
                    <span className="scanner-qty-value">x{item.quantity}</span>
                    <button
                      type="button"
                      className="scanner-qty-btn"
                      onClick={() => onUpdateQuantity?.(item.id, item.quantity + 1)}
                      title="Aumentar cantidad (+1)"
                      aria-label="Aumentar cantidad (+1)"
                    >
                      <Plus size={13} />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Tarjeta de Producto Individual (Status Card) si hay error o no encontrado */}
        {result && result.kind !== 'found' && (
          <div className={`scanner-result scanner-result--${result.kind}`} key={result.key}>
            {(result.kind === 'notfound' || result.kind === 'cashadvance') && <AlertCircle size={20} />}
            {(result.kind === 'inactive' || result.kind === 'error') && <XCircle size={20} />}
            <div className="scanner-result-body">
              <span className="scanner-result-title">{result.title}</span>
              {result.subtitle && <span className="scanner-result-subtitle">{result.subtitle}</span>}
            </div>
          </div>
        )}

        {/* Estado y Privacidad */}
        <div className={`scanner-status ${status.type}`}>
          {status.text}
          {engineType === 'native' && <span className="scanner-engine-badge">GPU Nativo</span>}
          {isLaptop && laptopEnhancement && <span className="scanner-engine-badge">Sauvola + Sharpen 1D Activo</span>}
        </div>

        <p className="scanner-privacy">
          Privacidad: la cámara procesa localmente en su dispositivo. No se transmiten imágenes.
        </p>
      </div>
    </Modal>
  );
}


function friendlyCameraError(err) {
  if (!err) return 'No se pudo iniciar la cámara.';
  if (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError') {
    return 'Permiso denegado: autorice el acceso a la cámara en los ajustes del navegador.';
  }
  if (err.name === 'NotFoundError' || err.name === 'DevicesNotFoundError') {
    return 'No se encontró ninguna cámara conectada en este dispositivo.';
  }
  if (err.name === 'NotReadableError' || err.name === 'TrackStartError') {
    return 'La cámara está ocupada por otra aplicación o pestaña del navegador.';
  }
  return `Error de cámara (${err.name || 'Desconocido'}): ${err.message || 'no disponible'}`;
}
