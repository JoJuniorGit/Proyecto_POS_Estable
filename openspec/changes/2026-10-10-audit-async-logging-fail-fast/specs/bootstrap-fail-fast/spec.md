# Spec (delta): Fail-Fast y logging estructurado en bootstrap — CLEAN-03 (8.158)

Fuente: auditoría re-emitida, CLEAN-03: "Swallowing de excepciones en el bootstrap del backend" (Backend.API/Program.cs L48-51, 126-130). Reestructuración exigida textual:

    catch (Exception ex)
    {
        AppLogger.LogError($"[STARTUP_ERROR] Fallo crítico al cargar configuración: {ex}");
        throw; // Fail-Fast si la configuración obligatoria no está disponible
    }

Notas de implementación (D2, ver proposal): `AppLogger` no expone `LogError`; se usa `AppLogger.LogCrash(ex, contexto)` que vuelca la traza completa (lo exigido por el diagnóstico: "sin volcar la traza de error en `AppLogger.LogCrash`"). El `throw` se aplica SOLO al catch de `secrets.json` (configuración obligatoria, precedencia máxima 8U-M2, `optional: false` cuando el archivo existe); el catch del recorrido de `.env` (camino OPCIONAL de Desarrollo, appsettings ya cargado) registra `LogWarn` SIN throw — desviación documentada.

## ADDED Requirements

### Requirement: REQ-BF-01 — secrets.json: fail-fast con traza

El catch de carga de `secrets.json` MUST registrar la excepción COMPLETA vía `AppLogger.LogCrash(ex, "Backend.API.Program.SecretsBootstrap")` y re-lanzar (`throw;`). El servidor MUST NOT arrancar desconfigurado si el archivo de secretos existe pero no puede cargarse (ACL/JSON inválido).

#### Scenario: secrets.json ilegible

- GIVEN secrets.json presente pero ilegible/corrupto
- THEN crash.log recibe la traza completa y el arranque aborta.

### Requirement: REQ-BF-02 — .env: observabilidad sin fail-fast

El catch del recorrido de directorios para `.env` (solo Desarrollo) MUST registrar `AppLogger.LogWarn($"...{ex.Message}", "Backend.API.Program.DotEnvBootstrap")` sin relanzar (el `.env` es opcional; no cargarlo no impide el arranque de desarrollo).

#### Scenario: fallo de recorrido

- GIVEN un fallo de IO en el recorrido de directorios
- THEN warn.log recibe el aviso y el arranque continúa.

### Requirement: REQ-BF-03 — Cero Console.WriteLine en el bootstrap

`Backend.API/Program.cs` MUST NOT contener `Console.WriteLine` (un servicio Windows o contenedor sin consola interactiva oculta esos mensajes).

#### Scenario: grep

- GIVEN el archivo final
- THEN `grep "Console.WriteLine" Backend.API/Program.cs` = 0.
