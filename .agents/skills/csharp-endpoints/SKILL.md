---
name: csharp-endpoints
description: >-
  Strict Clean Code rules in C#, naming conventions, and standardization for creating RESTful
  endpoints. Activate this skill when creating or editing controllers, routes, HTTP verbs/status
  codes, or endpoint response contracts.
---

# C# Best Practices & API Endpoint Skills

## 1. C# (Modern .NET) Conventions

- Namespaces: ALWAYS use file-scoped namespaces (e.g., `namespace MyProject.Controllers;` without curly braces `{}`).
- Naming Conventions:
  - Classes, Records, Interfaces, and Methods: `PascalCase`.
  - Parameters and local variables: `camelCase`.
  - Private fields: `_camelCase` (with an underscore).
- Immutability and DTOs: For data transfer (DTOs), use `record` or `class` with `init` properties to ensure that data does not mutate unexpectedly after creation.
- Nullability: The project has `<Nullable>enable</Nullable>`. It always warns of and handles potential null values. Explicitly use `?` where a value can be null.

## 2. Endpoint Standardization (RESTful)

- Routing: Use lowercase kebab-case for new literal route segments; the repo base route is `[Route("api/[controller]")]` (case-insensitive, plural controller name).
- Always use plural nouns, NEVER verbs. (Correct: `api/products`. Incorrect: `api/getProducts`).
- HTTP Verbs:
  - `GET`: To retrieve data. Never alter the state.
  - `POST`: To create new resources.
  - `PUT`: To completely replace an existing resource.
  - `PATCH`: For partial modifications (e.g., changing only the price).
  - `DELETE`: To delete a resource.
- HTTP Status Codes:
  - `200 OK`: For successful responses from GET, PUT, PATCH, or DELETE.
  - `201 Created`: For successful POST requests (must include the path to the newly created resource).
  - `400 Bad Request`: Client validation error (missing or incorrect data).
  - `404 Not Found`: When the requested resource (ID) does not exist.
- Isolation (No Entity Leakage): NEVER return an EF Core entity (`Product`, `Sale`) from a controller. ALWAYS map to a DTO (`ProductResponseDto`) before returning `Ok()`.
- Error Contract (RFC 7807): Exceptions escalate to `GlobalExceptionHandlerMiddleware` and become `ProblemDetails`; never return anonymous objects with `ex.Message` or leaked internals.

## 3. Performance and Concurrency

- Every endpoint MUST be asynchronous (`async Task<ActionResult<T>>` or `async Task<IActionResult>`).
- Every endpoint MUST accept a `CancellationToken` as its last parameter and pass it to database queries or services.

## 4. Mandatory Code Pattern (Template)

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;

    public ProductsController(IProductService productService)
    {
        _productService = productService;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductResponseDto>> GetProductByIdAsync(int id, CancellationToken cancellationToken)
    {
        var productDto = await _productService.GetByIdAsync(id, cancellationToken);

        if (productDto is null)
        {
            return NotFound();
        }

        return Ok(productDto);
    }
}
```

- RBAC: protect endpoints with `[Authorize]` and enforce cashier/manager privileges as described in the `pos-security-hardening` skill.
