# Spec (delta): Serialización segura de la entidad User — SEC-05 (8.153)

Fuente: auditoría integral re-emitida, hallazgo SEC-05: "Agregar `[System.Text.Json.Serialization.JsonIgnore]` explícito a ambas propiedades en `User.cs`" (PasswordHash, SecurityStamp).

## ADDED Requirements

### Requirement: REQ-USJ-01 — Los campos sensibles de User nunca se serializan

`Core/Entities/User.cs` MUST decorar `PasswordHash` y `SecurityStamp` con `[System.Text.Json.Serialization.JsonIgnore]`. La serialización de un `User` vía `System.Text.Json` (el serializador del pipeline, sin Newtonsoft en el repo) MUST omitir ambas propiedades (valor y nombre); el resto de las propiedades MUST seguir serializándose igual. La persistencia EF y los flujos de autenticación (que leen las propiedades en código, no por JSON) quedan intactos.

#### Scenario: Serialización directa de User

- GIVEN un `User` con `PasswordHash = "hash-X"` y `SecurityStamp = "stamp-X"`
- WHEN se serializa con `JsonSerializer.Serialize`
- THEN el JSON no contiene `hash-X` ni `stamp-X`, ni los nombres `passwordHash`/`securityStamp` (case-insensitive), y sí contiene cedula/name/username/role.

#### Scenario: Servicio y suite sin regresiones

- GIVEN el cambio aplicado
- WHEN corre la suite completa
- THEN 0 fallas; ningún flujo existente serializa `User` esperando esos campos (verificado por la suite).

### Requirement: REQ-USJ-02 — Cobertura de pruebas

- Un test dedicado de serialización STJ (nuevo archivo autorizado) con el escenario anterior, más la verificación de que otras propiedades siguen presentes.
