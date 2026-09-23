---
name: clean-architecture
description: >-
  Standards for creating new services, DTOs, and controllers with strict layer boundaries.
  Activate this skill when adding services, controllers, or DTOs, or when enforcing dependency
  direction between the domain layer, the backend, and the client applications.
---

# Clean Architecture & API Skill

- Dependencies: The Domain layer CANNOT have references to EF Core or WPF. Only pure C#.
- Dependency Injection: All services must be injected through the constructor.
- Full Asynchronicity: All methods that perform I/O (network or disk) must return `Task` or `Task<T>`. DO NOT use `.Result`, `Task.Wait()`, or `async void` (except in unavoidable event handlers).
- CancellationToken: Every asynchronous method must accept a `CancellationToken` as its last parameter to allow safe interruptions.
- Error Handling: Do not use `try/catch` to control the normal logical flow of the application.
