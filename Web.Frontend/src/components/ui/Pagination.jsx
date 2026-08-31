import { useState, useEffect } from 'react';
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, ArrowRight } from 'lucide-react';

export default function Pagination({
  currentPage = 1,
  totalPages = 1,
  totalCount = 0,
  onPageChange,
  loading = false,
  itemLabel = 'productos'
}) {
  const [goToPage, setGoToPage] = useState(currentPage.toString());

  useEffect(() => {
    setGoToPage(currentPage > 0 ? currentPage.toString() : '1');
  }, [currentPage]);

  const canGoFirst = currentPage > 1 && totalPages > 1;
  const canGoPrevious = currentPage > 1 && totalPages > 1;
  const canGoNext = currentPage < totalPages && totalPages > 1;
  const canGoLast = currentPage < totalPages && totalPages > 1;

  const startPage = Math.max(1, currentPage - 2);
  const endPage = Math.min(totalPages, currentPage + 2);
  const pageNumbers = totalPages > 0 && endPage >= startPage
    ? Array.from({ length: endPage - startPage + 1 }, (_, i) => startPage + i)
    : [];

  const handlePageClick = (page) => {
    if (page >= 1 && page <= totalPages && page !== currentPage && !loading) {
      onPageChange(page);
    }
  };

  const handleGoSubmit = (e) => {
    e.preventDefault();
    const pageNum = parseInt(goToPage, 10);
    if (!isNaN(pageNum) && totalPages > 0) {
      const clamped = Math.min(totalPages, Math.max(1, pageNum));
      if (clamped !== currentPage) {
        onPageChange(clamped);
      } else {
        setGoToPage(clamped.toString());
      }
    } else {
      setGoToPage(currentPage > 0 ? currentPage.toString() : '1');
    }
  };

  const summaryText = totalPages > 0
    ? (
      <>
        Página <strong>{currentPage}</strong> de <strong>{totalPages}</strong>{' '}
        <span className="text-muted">({totalCount} {itemLabel})</span>
      </>
    )
    : (
      <>
        Página <strong>0</strong> de <strong>0</strong>{' '}
        <span className="text-muted">(0 {itemLabel})</span>
      </>
    );

  return (
    <div className="pagination-container card">
      {/* Fila 1 (Información): Resumen de Página y Total de Elementos */}
      <div className="pagination-info">
        {summaryText}
      </div>

      {/* Controles: Botones de Navegación y Salto Rápido en la misma fila horizontal */}
      <div className="pagination-controls-row">
        {/* Grupo de Navegación (<<, <, Números, >, >>) */}
        <div className="pagination-nav-group">
          {/* Botón Primera Página (<<) */}
          <button
            type="button"
            className="btn btn-outline pagination-btn"
            onClick={() => handlePageClick(1)}
            disabled={!canGoFirst || loading}
            title="Primera Página"
            aria-label="Primera Página"
          >
            <ChevronsLeft size={16} />
          </button>

          {/* Botón Página Anterior (<) */}
          <button
            type="button"
            className="btn btn-outline pagination-btn"
            onClick={() => handlePageClick(currentPage - 1)}
            disabled={!canGoPrevious || loading}
            title="Página Anterior"
            aria-label="Página Anterior"
          >
            <ChevronLeft size={16} />
          </button>

          {/* Botones Numéricos de Página (El número activo queda en el medio de < y >) */}
          <div className="pagination-numbers">
            {pageNumbers.map((p) => {
              const isActive = p === currentPage;
              return (
                <button
                  key={p}
                  type="button"
                  className={`btn pagination-btn pagination-number-btn ${isActive ? 'btn-primary active font-bold' : 'btn-outline'}`}
                  onClick={() => handlePageClick(p)}
                  disabled={loading}
                  aria-current={isActive ? 'page' : undefined}
                >
                  {p}
                </button>
              );
            })}
          </div>

          {/* Botón Página Siguiente (>) */}
          <button
            type="button"
            className="btn btn-outline pagination-btn"
            onClick={() => handlePageClick(currentPage + 1)}
            disabled={!canGoNext || loading}
            title="Página Siguiente"
            aria-label="Página Siguiente"
          >
            <ChevronRight size={16} />
          </button>

          {/* Botón Última Página (>>) */}
          <button
            type="button"
            className="btn btn-outline pagination-btn"
            onClick={() => handlePageClick(totalPages)}
            disabled={!canGoLast || loading}
            title="Última Página"
            aria-label="Última Página"
          >
            <ChevronsRight size={16} />
          </button>
        </div>

        {/* Separador vertical para escritorio */}
        <div className="pagination-divider desktop-only" />

        {/* Salto Rápido: Bloque 'Ir a' */}
        <form onSubmit={handleGoSubmit} className="pagination-goto-form">
          <span className="pagination-goto-label">Ir a:</span>
          <input
            type="number"
            min="1"
            max={Math.max(1, totalPages)}
            value={goToPage}
            onFocus={(e) => e.target.select()}
            onClick={(e) => e.target.select()}
            onChange={(e) => setGoToPage(e.target.value)}
            disabled={totalPages <= 0 || loading}
            className="form-control form-control-sm pagination-goto-input"
          />
          <button
            type="submit"
            className="btn btn-primary pagination-btn pagination-goto-btn"
            disabled={totalPages <= 0 || loading}
            title="Ir a página"
            aria-label="Ir a página"
          >
            <ArrowRight size={14} />
          </button>
        </form>
      </div>
    </div>
  );
}
