using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpPost("{id}/deliveries")]
    [ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DeliveryReceiptDto>> DeliverPartialAsync(
        int id,
        [FromBody] PartialDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (User.IsInRole("Driver"))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para confirmar esta entrega.");
        }

        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para confirmar esta entrega.");
        }

        string requestPath = $"/api/sales/{id}/deliveries";
        string bodyJson = GetActorUserId() + "|" + JsonSerializer.Serialize(request);
        var resolved = await ResolveIdempotencyAsync(
            requestPath,
            bodyJson,
            missingKeyMessage: "El encabezado Idempotency-Key es obligatorio para registrar una entrega parcial.",
            parseNumericBodyAsInvoice: false);

        if (resolved.ShouldStop)
        {
            return resolved.BlockingResult!;
        }

        try
        {
            var receipt = await _salesService.DeliverPartialAsync(
                id,
                request.Items.Select(item => (item.SaleItemId, item.Quantity)).ToList(),
                request.Notes,
                GetActorUserId(),
                cancellationToken,
                idempotencyKey: resolved.Key,
                idempotencyPayloadHash: resolved.PayloadHash,
                requestPath: requestPath);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(receipt);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.ApiConflict("Otro usuario modificó el retiro simultáneamente. Actualice la lista e intente de nuevo.");
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Venta con ID {id} no encontrada.");
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (DbUpdateException ex) when (IsIdempotencyUniqueViolation(ex))
        {
            return await HandleIdempotencyCollisionAsync(ex, requestPath, resolved.Key, resolved.PayloadHash);
        }
    }
}
