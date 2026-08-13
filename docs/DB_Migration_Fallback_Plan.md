# Plan de Respaldo Alternativo para Migraciones de Base de Datos en Producción

## Contexto y Objetivo
En entornos de producción con bases de datos PostgreSQL de gran volumen o esquemas críticos en uso 24/7, la ejecución automática de migraciones de Entity Framework Core (`Database.MigrateAsync()`) puede verse bloqueada por bloqueos de tablas (*table locks*), desconexiones de red o transformaciones de datos complejas.

Este documento establece el procedimiento de respaldo manual mediante scripts SQL independientes para ejecutar migraciones críticas de forma segura sin alterar la estabilidad operativa.

---

## Procedimiento de Ejecución Alternativa

### 1. Respaldo Obligatorio de Base de Datos
Antes de aplicar cualquier cambio estructural o script SQL masivo en producción, genere un respaldo completo ejecutable:
```bash
pg_dump -U postgres -h localhost -d CommandCenterDb -F c -b -v -f "C:\Backups\CommandCenterDb_pre_migration_%date:~-4,4%%date:~-7,2%%date:~-10,2%.backup"
```

### 2. Generación del Script SQL desde EF Core
Si requiere exportar las sentencias SQL exactas correspondientes a las migraciones pendientes sin aplicarlas directamente desde el backend:
```powershell
dotnet ef migrations script --project Backend.API\Backend.API.csproj --output scripts\fallback_migration.sql --idempotent
```

### 3. Ejecución del Script de Respaldo (`fallback_migration.sql`)
Ejecute el script en PostgreSQL utilizando `psql` dentro de una transacción explícita:
```bash
psql -U postgres -h localhost -d CommandCenterDb -f scripts/fallback_migration.sql
```

---

## Script SQL de Ejemplo (`scripts/fallback_migration.sql`)
El archivo `scripts/fallback_migration.sql` incluye las verificaciones idempotentes para asegurar que la tabla `__EFMigrationsHistory` registre el parche aplicado de forma consistente con EF Core.
