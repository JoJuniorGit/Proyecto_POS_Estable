import { memo } from 'react';
import {
  Wand2,
  SwitchCamera,
  Flashlight,
  FlashlightOff,
  Sparkles,
  ZoomIn,
} from 'lucide-react';

function BarcodeScannerControls({
  isLaptop,
  laptopEnhancement,
  onToggleLaptopEnhancement,
  videoDevices = [],
  currentDeviceId,
  onSwitchCamera,
  hasTorch,
  torchActive,
  onToggleTorch,
  scanCount = 0,
  zoomCapabilities,
  currentZoom = 1,
  onApplyZoom,
}) {
  return (
    <>
      {/* Controles Flotantes Superiores */}
      <div className="scanner-controls-top">
        {/* Botón de Realce Óptico exclusivo para Laptops */}
        {isLaptop && (
          <button
            type="button"
            className={`scanner-icon-btn ${laptopEnhancement ? 'active' : ''}`}
            onClick={onToggleLaptopEnhancement}
            title={
              laptopEnhancement
                ? 'Desactivar Realce Óptico Laptop'
                : 'Activar Realce Óptico Laptop (Sauvola + Sharpen + Zoom)'
            }
            aria-label="Realce Óptico Laptop"
            aria-pressed={laptopEnhancement}
          >
            <Wand2 size={18} />
          </button>
        )}

        {videoDevices.length > 1 && (
          <button
            type="button"
            className="scanner-icon-btn"
            onClick={() => {
              const nextIndex =
                (videoDevices.findIndex((d) => d.deviceId === currentDeviceId) + 1) %
                videoDevices.length;
              onSwitchCamera?.(videoDevices[nextIndex].deviceId);
            }}
            title="Cambiar lente/cámara"
            aria-label="Cambiar lente de la cámara"
          >
            <SwitchCamera size={18} />
          </button>
        )}

        {hasTorch && (
          <button
            type="button"
            className={`scanner-icon-btn ${torchActive ? 'active' : ''}`}
            onClick={onToggleTorch}
            title={torchActive ? 'Apagar linterna' : 'Encender linterna'}
            aria-label="Linterna"
            aria-pressed={torchActive}
          >
            {torchActive ? <FlashlightOff size={18} /> : <Flashlight size={18} />}
          </button>
        )}

        {scanCount > 0 && (
          <div
            className="scanner-session-badge"
            title="Artículos escaneados en esta sesión"
          >
            <Sparkles size={14} />
            <span>{scanCount}</span>
          </div>
        )}
      </div>

      {/* Selector de Zoom Rápido si el hardware lo soporta */}
      {zoomCapabilities && zoomCapabilities.max > 1 && (
        <div className="scanner-zoom-bar">
          <ZoomIn size={14} className="scanner-zoom-icon" />
          {[1, 2, 3]
            .filter((z) => z <= zoomCapabilities.max)
            .map((z) => (
              <button
                key={z}
                type="button"
                className={`scanner-zoom-pill ${currentZoom === z ? 'active' : ''}`}
                onClick={() => onApplyZoom?.(z)}
                aria-label={`Zoom ${z}x`}
                aria-pressed={currentZoom === z}
              >
                {z}×
              </button>
            ))}
        </div>
      )}
    </>
  );
}

export default memo(BarcodeScannerControls);
