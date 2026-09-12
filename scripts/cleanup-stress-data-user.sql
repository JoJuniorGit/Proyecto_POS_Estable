\set ON_ERROR_STOP on

\if :{?cashier_cedula}
\else
  \set cashier_cedula 'BOT_STRESS_TEST'
\endif

\echo '============================================================'
\echo 'Borrado opcional del usuario cajero de prueba (8.108)'
\echo '============================================================'
\echo 'Cajero de prueba  :' :cashier_cedula
\echo 'Elimina el usuario por Cedula o Username.'
\echo 'Solo ejecutar con -DeleteUser -Confirm YES y DESPUÉS de la'
\echo 'limpieza de ventas principal (FK Sales.CashierId).'
\echo '============================================================'

BEGIN;

DELETE FROM "Users"
WHERE "Cedula" = :'cashier_cedula'
   OR "Username" = :'cashier_cedula';

COMMIT;

SELECT count(*) AS usuarios_restantes
FROM "Users"
WHERE "Cedula" = :'cashier_cedula'
   OR "Username" = :'cashier_cedula';