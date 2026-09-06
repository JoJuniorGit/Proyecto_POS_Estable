import { useEffect, useRef, memo } from 'react';
import { Loader2, CameraOff, ShieldAlert } from 'lucide-react';

function BarcodeScannerHud({
  videoRef,
  isOpen,
  isLaptop,
  starting,
  status,
  insecureContextMessage,
  boundingBoxRef,
}) {
  const hudCanvasRef = useRef(null);
  const hudAnimRef = useRef(null);

  // Dibujado del HUD (Láser y Bounding Box) en canvas superpuesto
  useEffect(() => {
    if (!isOpen) {
      if (hudAnimRef.current) cancelAnimationFrame(hudAnimRef.current);
      return;
    }

    const canvas = hudCanvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    let laserY = 0;
    let laserDir = 1;

    const renderHud = () => {
      if (!canvas || !videoRef.current) return;
      const width = (canvas.width = canvas.clientWidth || 400);
      const height = (canvas.height = canvas.clientHeight || 280);

      ctx.clearRect(0, 0, width, height);

      // 1. Recuadro guía de enfoque óptimo para Laptop
      if (isLaptop) {
        ctx.save();
        ctx.strokeStyle = 'rgba(16, 185, 129, 0.4)';
        ctx.lineWidth = 1.5;
        ctx.setLineDash([6, 6]);
        const rw = width * 0.7;
        const rh = height * 0.6;
        const rx = (width - rw) / 2;
        const ry = (height - rh) / 2;
        ctx.strokeRect(rx, ry, rw, rh);
        ctx.restore();
      }

      // 2. Línea láser animada
      laserY += laserDir * 2.5;
      if (laserY > height - 10) laserDir = -1;
      if (laserY < 10) laserDir = 1;

      ctx.save();
      ctx.strokeStyle = 'rgba(16, 185, 129, 0.9)';
      ctx.lineWidth = 2.5;
      ctx.shadowColor = '#10b981';
      ctx.shadowBlur = 12;
      ctx.beginPath();
      ctx.moveTo(width * 0.12, laserY);
      ctx.lineTo(width * 0.88, laserY);
      ctx.stroke();
      ctx.restore();

      // 3. Bounding Box sobre el código detectado
      if (boundingBoxRef?.current && boundingBoxRef.current.length >= 4) {
        const pts = boundingBoxRef.current;
        const vW = videoRef.current.videoWidth || width;
        const vH = videoRef.current.videoHeight || height;

        ctx.save();
        ctx.strokeStyle = '#10b981';
        ctx.lineWidth = 3;
        ctx.fillStyle = 'rgba(16, 185, 129, 0.25)';
        ctx.shadowColor = '#10b981';
        ctx.shadowBlur = 12;

        ctx.beginPath();
        ctx.moveTo(pts[0].x * (width / vW), pts[0].y * (height / vH));
        for (let i = 1; i < pts.length; i++) {
          ctx.lineTo(pts[i].x * (width / vW), pts[i].y * (height / vH));
        }
        ctx.closePath();
        ctx.stroke();
        ctx.fill();
        ctx.restore();
      }

      hudAnimRef.current = requestAnimationFrame(renderHud);
    };

    hudAnimRef.current = requestAnimationFrame(renderHud);

    return () => {
      if (hudAnimRef.current) cancelAnimationFrame(hudAnimRef.current);
    };
  }, [isOpen, isLaptop, videoRef, boundingBoxRef]);

  return (
    <div className="scanner-video-wrap">
      <video ref={videoRef} className="scanner-video" muted playsInline />
      <canvas ref={hudCanvasRef} className="scanner-hud-canvas" />

      {/* Guía de distancia focal para webcam de laptop */}
      {isLaptop && !starting && status?.type !== 'error' && (
        <div className="scanner-focus-hint">
          💡 Distancia recomendada: 30 a 40 cm de la pantalla
        </div>
      )}

      {starting && (
        <div className="scanner-overlay">
          <Loader2 className="animate-spin" size={28} />
          <span>Iniciando cámara…</span>
        </div>
      )}

      {!starting && status?.type === 'error' && (
        <div className="scanner-overlay error">
          {status.text === insecureContextMessage ? (
            <ShieldAlert size={28} />
          ) : (
            <CameraOff size={28} />
          )}
          <span>{status.text}</span>
        </div>
      )}
    </div>
  );
}

export default memo(BarcodeScannerHud);
