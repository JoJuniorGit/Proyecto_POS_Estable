using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs;
using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// 8.204 (extracción del God object de caja): persistencia y replay de la respuesta idempotente
/// de un movimiento manual. Vive separado del ciclo de sesión para que la transacción de caja no
/// mezcle transporte, serialización y reglas de estado.
/// </summary>
internal sealed class CashDrawerIdempotencyStore
{
    private readonly SalesDbContext _context;

    public CashDrawerIdempotencyStore(SalesDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CashTransactionResponseDto?> TryReadAsync(
        CashTransactionIdempotency idempotency,
        CancellationToken cancellationToken)
    {
        var existing = await _context.IdempotentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.Key == idempotency.Key && record.RequestPath == idempotency.RequestPath,
                cancellationToken);

        if (existing is null || existing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        if (existing.UserId != idempotency.UserId
            || existing.PayloadHash.Length != idempotency.PayloadHash.Length
            || !CryptographicOperations.FixedTimeEquals(existing.PayloadHash, idempotency.PayloadHash)
            || string.IsNullOrWhiteSpace(existing.ResponseBody))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CashTransactionResponseDto>(existing.ResponseBody);
    }

    public async Task PersistAsync(
        CashTransactionIdempotency idempotency,
        CashTransactionResponseDto dto,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _context.IdempotentRequests.Add(new IdempotentRequest
        {
            Key = idempotency.Key,
            RequestPath = idempotency.RequestPath,
            PayloadHash = idempotency.PayloadHash,
            StatusCode = 200,
            ResponseBody = idempotency.SerializeResponse(dto),
            UserId = idempotency.UserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(24)
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        CashTransactionIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        if (idempotency is null)
        {
            return false;
        }

        return await _context.IdempotentRequests
            .AsNoTracking()
            .AnyAsync(
                record => record.Key == idempotency.Key && record.RequestPath == idempotency.RequestPath,
                cancellationToken);
    }

    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505"
                && (string.IsNullOrEmpty(postgresException.ConstraintName)
                    || postgresException.ConstraintName.Contains("IdempotentRequests", StringComparison.Ordinal));
        }

        return ex.Message.Contains("IdempotentRequests", StringComparison.Ordinal)
            || ex.InnerException?.Message.Contains("IdempotentRequests", StringComparison.Ordinal) == true;
    }
}
