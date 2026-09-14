# Coding Guidelines — QA / Tests / CI/CD

> Sección de aseguramiento de calidad. Aplica a cambios en pruebas, cobertura y pipelines.

---

## 6.1. Estándar de Pruebas Automatizadas

* **Nomenclatura:** `Metodo_Escenario_ResultadoEsperado`
  *Ejemplo:* `RegisterDeliveryOrderAsync_WhenOrderIdZero_ThrowsArgumentException`
* **Cobertura Mínima:**
  * `Core ≥ 0.70`
  * `Sales.Module ≥ 0.80`
  * `Inventory.Module ≥ 0.72`
  * Excluye `*.Migrations.*` (verificación por smoke `MigrateAsync`).
* **Pruebas de Integración:**
  * CI: sobre `postgres:16` con `TEST_POSTGRES_CONNECTION`.
  * Local: PostgreSQL local o Testcontainers.

## 6.2. Pipeline CI/CD (`ci.yml`)

1. **Frontend:** `npm run lint` (`oxlint`) + `npm test`.
2. **Backend:** Compilación Release con `TreatWarningsAsErrors=true`.
3. **Suite Completa:** `dotnet test` + `npm test` con cobertura XML.
