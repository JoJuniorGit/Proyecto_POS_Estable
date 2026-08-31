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
    <div
      className="catalog-pagination-bar card p-3"
      style={{
        display: 'flex',
        justifyContent: 'center',
        alignItems: 'center',
        gap: '8px',
        flexWrap: 'wrap',
        marginTop: '16px',
        width: '100%'
      }}
    >
      {/* Resumen */}
      <span className="font-medium text-sm text-center" style={{ padding: '0 8px', whiteSpace: 'nowrap' }}>
        {summaryText}
      </span>

      {/* Botón Primera Página (<<) */}
      <button
        type="button"
        className="btn btn-outline btn-sm"
        onClick={() => handlePageClick(1)}
        disabled={!canGoFirst || loading}
        title="Primera Página"
        style={{ minWidth: '32px', height: '32px', padding: '0 6px', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}
      >
        <ChevronsLeft size={16} />
      </button>

      {/* Botón Página Anterior (<) */}
      <button
        type="button"
        className="btn btn-outline btn-sm"
        onClick={() => handlePageClick(currentPage - 1)}
        disabled={!canGoPrevious || loading}
        title="Página Anterior"
        style={{ minWidth: '32px', height: '32px', padding: '0 6px', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}
      >
        <ChevronLeft size={16} />
      </button>

      {/* Botones Numéricos Relativos */}
      <div style={{ display: 'inline-flex', gap: '4px', alignItems: 'center', flexWrap: 'wrap' }}>
        {pageNumbers.map((p) => {
          const isActive = p === currentPage;
          return (
            <button
              key={p}
              type="button"
              className={`btn btn-sm ${isActive ? 'btn-primary' : 'btn-outline'}`}
              onClick={() => handlePageClick(p)}
              disabled={loading}
              style={{
                minWidth: '34px',
                height: '32px',
                padding: '2px 8px',
                fontWeight: isActive ? 700 : 500,
                boxShadow: isActive ? '0 2px 4px rgba(99, 102, 241, 0.3)' : 'none'
              }}
            >
              {p}
            </button>
          );
        })}
      </div>

      {/* Botón Página Siguiente (>) */}
      <button
        type="button"
        className="btn btn-outline btn-sm"
        onClick={() => handlePageClick(currentPage + 1)}
        disabled={!canGoNext || loading}
        title="Página Siguiente"
        style={{ minWidth: '32px', height: '32px', padding: '0 6px', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}
      >
        <ChevronRight size={16} />
      </button>

      {/* Botón Última Página (>>) */}
      <button
        type="button"
        className="btn btn-outline btn-sm"
        onClick={() => handlePageClick(totalPages)}
        disabled={!canGoLast || loading}
        title="Última Página"
        style={{ minWidth: '32px', height: '32px', padding: '0 6px', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}
      >
        <ChevronsRight size={16} />
      </button>

      {/* Separador vertical */}
      <div style={{ width: '1px', height: '22px', backgroundColor: 'var(--border-color, #e2e8f0)', margin: '0 4px' }} />

      {/* Búsqueda Exacta: Ir a Página */}
      <form onSubmit={handleGoSubmit} style={{ display: 'inline-flex', alignItems: 'center', gap: '6px' }}>
        <span className="text-xs text-muted" style={{ whiteSpace: 'nowrap' }}>Ir a:</span>
        <input
          type="number"
          min="1"
          max={Math.max(1, totalPages)}
          value={goToPage}
          onChange={(e) => setGoToPage(e.target.value)}
          disabled={totalPages <= 0 || loading}
          className="form-control form-control-sm"
          style={{ width: '56px', height: '32px', textAlign: 'center', fontWeight: 600, padding: '2px 4px' }}
        />
        <button
          type="submit"
          className="btn btn-primary btn-sm"
          disabled={totalPages <= 0 || loading}
          title="Ir a página"
          style={{ minWidth: '32px', height: '32px', padding: '0 6px', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}
        >
          <ArrowRight size={14} />
        </button>
      </form>
    </div>
  );
}
