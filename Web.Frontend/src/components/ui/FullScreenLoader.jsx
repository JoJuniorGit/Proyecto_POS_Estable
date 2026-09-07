export default function FullScreenLoader() {
  return (
    <div className="pos-loading" aria-busy="true" role="status">
      <span className="pos-loading-text">Cargando…</span>
    </div>
  );
}