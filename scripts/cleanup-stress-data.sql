\set ON_ERROR_STOP on

\if :{?cashier_cedula}
\else
  \set cashier_cedula 'BOT_STRESS_TEST'
\endif

\echo '============================================================'
\echo 'Limpieza de datos sintéticos de la prueba de estrés (8.108)'
\echo '============================================================'
\echo 'Cajero de prueba    :' :cashier_cedula
\echo 'Elimina ventas de ese cajero y sus dependencias (SaleItems,'
\echo 'SalePayments, CashTransactions, OutboxMessages, IdempotentRequests,'
\echo 'StockMovements y StockMovements_Archive).'
\echo 'La facturación usa una secuencia: borrar ventas NO resetea el'
\echo 'próximo número de factura. Ejecute backup-postgres.ps1 antes.'
\echo '============================================================'

BEGIN;

CREATE TEMP TABLE stress_sale_ids AS
SELECT s."Id"
FROM "Sales" s
JOIN "Users" u ON u."Id" = s."CashierId"
WHERE u."Cedula" = :'cashier_cedula'
   OR u."Username" = :'cashier_cedula'
   OR u."Name" = :'cashier_cedula';

SELECT count(*) AS ventas_a_eliminar FROM stress_sale_ids;

DELETE FROM "SaleItems"     WHERE "SaleId" IN (SELECT "Id" FROM stress_sale_ids);
DELETE FROM "SalePayments"  WHERE "SaleId" IN (SELECT "Id" FROM stress_sale_ids);
DELETE FROM "CashTransactions" WHERE "SaleId" IN (SELECT "Id" FROM stress_sale_ids);

DELETE FROM "OutboxMessages" om
WHERE om."EventType" = 'SaleCompleted'
  AND EXISTS (
    SELECT 1 FROM stress_sale_ids s
    WHERE om."Payload"::text LIKE '%"SaleId":' || s."Id" || '[,}]%'
  );

DELETE FROM "IdempotentRequests" ir
WHERE ir."RequestPath" ~ '^/api/sales/[0-9]+/complete([?].*)?$'
  AND EXISTS (
    SELECT 1 FROM stress_sale_ids s
    WHERE s."Id" = substring(ir."RequestPath" FROM '/api/sales/([0-9]+)/complete')::bigint
  );

DELETE FROM "StockMovements" WHERE "SaleId" IN (SELECT "Id" FROM stress_sale_ids);
DELETE FROM "StockMovements_Archive" WHERE "SaleId" IN (SELECT "Id" FROM stress_sale_ids);

DELETE FROM "Sales" WHERE "Id" IN (SELECT "Id" FROM stress_sale_ids);

COMMIT;

SELECT count(*) AS ventas_restantes_del_cajero
FROM "Sales" s
JOIN "Users" u ON u."Id" = s."CashierId"
WHERE u."Cedula" = :'cashier_cedula'
   OR u."Username" = :'cashier_cedula'
   OR u."Name" = :'cashier_cedula';