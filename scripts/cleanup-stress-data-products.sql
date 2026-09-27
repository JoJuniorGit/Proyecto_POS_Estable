\set ON_ERROR_STOP on

\if :{?sku_prefix}
\else
  \set sku_prefix 'SKU-TEST-'
\endif

\echo '============================================================'
\echo 'Borrado opcional de productos sintéticos (8.108)'
\echo '============================================================'
\echo 'Prefijo de SKU     :' :sku_prefix
\echo 'Elimina reservas de stock y productos con SKU = prefijo || %'
\echo 'Solo ejecutar con -DeleteProducts -Confirm YES.'
\echo '============================================================'

BEGIN;

DELETE FROM "StockReservations" r
WHERE EXISTS (
  SELECT 1 FROM "Products" p
  WHERE p."Id" = r."ProductId"
    AND p."SKU" LIKE :'sku_prefix' || '%'
);

DELETE FROM "Products" WHERE "SKU" LIKE :'sku_prefix' || '%';

COMMIT;