import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildProductsQuery, resolveCategorySelection } from './CatalogPage.jsx';

/**
 * 8.179-T3 (S4 Web): filtro de categorías del catálogo. El contrato congelado en T1 expone
 * `categoryId` en GET /api/products (subárbol completo) y el select se alimenta de
 * GET /api/categories. La carga de categorías es no bloqueante: si falla, sólo queda "Todas".
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'CatalogPage.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('CatalogPage — filtro por categoría (8.179-T3, S4)', () => {
  it('1. Con categoría seleccionada la URL paginada lleva categoryId', () => {
    const url = buildProductsQuery({
      page: 1,
      pageSize: 25,
      filter: '',
      sortBy: 'name',
      isDescending: false,
      categoryId: '5',
    });

    assert.ok(url.startsWith('/api/products?page=1&pageSize=25'), url);
    assert.ok(url.includes('&categoryId=5'), `la URL debe filtrar por subárbol: ${url}`);
  });

  it('2. Sin categoría ("Todas") la URL no incluye el parámetro', () => {
    const url = buildProductsQuery({
      page: 2,
      pageSize: 25,
      filter: 'leche',
      sortBy: 'price',
      isDescending: true,
      categoryId: '',
    });

    assert.ok(!url.includes('categoryId'), `no debe viajar categoryId vacío: ${url}`);
    assert.ok(url.includes('&filter=leche'), url);
    assert.ok(url.includes('&sortBy=price&isDescending=true'), url);
  });

  it('3. Cambiar la categoría siempre vuelve a la página 1', () => {
    assert.deepStrictEqual(resolveCategorySelection('7'), { categoryId: '7', page: 1 });
    assert.deepStrictEqual(resolveCategorySelection(''), { categoryId: '', page: 1 });
  });

  it('4. El fetch paginado construye la URL con la categoría activa y la declara en dependencias', () => {
    assert.ok(
      normalizedSource.includes(
        'buildProductsQuery({ page: currentPage, pageSize, filter: debouncedSearch, sortBy, isDescending: sortDescending, categoryId, })'
      ),
      'el efecto debe construir la URL con la categoría activa'
    );
    assert.ok(
      normalizedSource.includes('reloadToken, categoryId]);'),
      'categoryId debe estar en las dependencias del efecto para refetch al cambiar'
    );
  });

  it('5. La carga de categorías es no bloqueante (catch con log, sin propagar)', () => {
    assert.ok(
      normalizedSource.includes('getCategories(controller.signal)'),
      'debe cargar las categorías una sola vez al montar'
    );
    assert.ok(
      normalizedSource.includes('Error cargando categorías'),
      'si la carga falla debe registrarse y dejar el select en "Todas las categorías"'
    );
  });
});
