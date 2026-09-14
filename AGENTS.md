# AGENTS.md — CommandCenter POS (V0.1)

Contrato operativo para agentes. Este archivo se inyecta automáticamente.

## Flujo de Trabajo

1.  **Lee las Guías:**
    *   **Core** (siempre): `docs/coding-guidelines-core.md` — reglas universales de código.
    *   **Backend** (si el cambio es .NET): `docs/coding-guidelines-backend.md` — EF Core, persistencia, errores.
    *   **Web** (si el cambio es React): `docs/coding-guidelines-web.md` — componentes, DTOs, estilos.
    *   **WPF** (si el cambio es Desktop): `docs/coding-guidelines-wpf.md` — MVVM, CommunityToolkit, memoria.
    *   **QA** (si el cambio es tests): `docs/coding-guidelines-qa.md` — nomenclatura, cobertura, CI.
    *   **Arquitectura** (si el cambio es estructural): `ARCHITECTURE.md` — arquitectura general del sistema.
2.  **Carga Skills:** Usa skills de disciplina según el área de cambio.
3.  **Implementa (SDD):** Sigue el flujo `sdd-apply` → `sdd-verify`.
4.  **Verifica:** Ejecuta `dotnet build -c Release` y `dotnet test` (0 errores/0 warnings).
5.  **Commitea:** Estilo `feat(8.xx)/fix/refactor(8.xx): ...` referenciando el ANEXO en `docs/reporte.txt`.

## Reglas Críticas (Resumen)

Para el detalle completo, consulta siempre el archivo core `docs/coding-guidelines-core.md`.

-   **Integridad:** Solo `decimal` para dinero. Nunca recalcules historial con tasa actual.
-   **Datos:** Entidades EF nunca al cliente (usa DTOs). Usa `AsNoTracking` y `AsSplitQuery`.
-   **Código:** Sin `async void` en servicios. Sin comentarios explicativos (usa nombres expresivos).
-   **Seguridad:** RBAC estricto (`Driver` bloqueado en ventas/caja). Errores vía `ProblemDetails`.

## Done (Checklist)

-   [ ] `dotnet build CommandCenter.slnx -c Release` (0 errors, 0 warnings).
-   [ ] `dotnet test` y `npm test` (si aplica) al 100%.
-   [ ] Cobertura: Core ≥0.70, Sales ≥0.80, Inventory ≥0.72.
-   [ ] ANEXO registrado en `docs/reporte.txt`.
