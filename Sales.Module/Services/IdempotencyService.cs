using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;

namespace Sales.Module.Services;

public class IdempotencyService : IIdempotencyService
{
    private static readonly Regex KeyRegex = new(@"^[a-zA-Z0-9_\-\.]{1,128}$", RegexOptions.Compiled);
    private readonly SalesDbContext _context;

    // 8.14-W4: TTL configurable (horas). Default 24 h = comportamiento histórico; las
    // instalaciones pueden alargarlo (retención forense de reintentos) vía appsettings
    // "Idempotency:TtlHours" sin tocar el código.
    private readonly TimeSpan _ttl;

    // 8.7-M5: contadores por-instancia (servicio Scoped), no estáticos — el estado mutable no
    // debe compartirse entre scopes de request.
    private long _hits;
    private long _misses;
    private long _conflicts;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Conflicts => Interlocked.Read(ref _conflicts);

    public IdempotencyService(SalesDbContext context, TimeSpan? ttl = null)
    {
        _context = context;
        // 8.14-W4: si TtlHours <= 0 se ignora y se usa el default de 24 h.
        var configured = ttl ?? TimeSpan.FromHours(24);
        _ttl = configured > TimeSpan.Zero ? configured : TimeSpan.FromHours(24);
    }

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key.Length > 128) return false;
        return KeyRegex.IsMatch(key);
    }

    public bool ValidateKeyFormat(string? key, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            errorMessage = "El encabezado Idempotency-Key no puede estar vacío.";
            return false;
        }

        if (key.Length > 128)
        {
            errorMessage = "El encabezado Idempotency-Key excede la longitud máxima permitida de 128 caracteres.";
            return false;
        }

        if (!KeyRegex.IsMatch(key))
        {
            errorMessage = "El encabezado Idempotency-Key contiene caracteres no permitidos. Solo se admiten caracteres alfanuméricos, guiones, puntos y guiones bajos.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static byte[] ComputePayloadHash(string method, string path, byte[] bodyBytes)
    {
        var prefix = Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}:{path.ToLowerInvariant()}:");
        var combined = new byte[prefix.Length + (bodyBytes?.Length ?? 0)];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        if (bodyBytes != null && bodyBytes.Length > 0)
        {
            Buffer.BlockCopy(bodyBytes, 0, combined, prefix.Length, bodyBytes.Length);
        }
        return SHA256.HashData(combined);
    }

    byte[] IIdempotencyService.ComputePayloadHash(string method, string path, byte[] bodyBytes)
    {
        return ComputePayloadHash(method, path, bodyBytes);
    }

    public async Task<IdempotencyCheckResult> CheckAsync(string key, string requestPath, byte[] payloadHash, CancellationToken cancellationToken = default)
    {
        var existing = await _context.IdempotentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key && r.RequestPath == requestPath, cancellationToken);

        if (existing == null)
        {
            Interlocked.Increment(ref _misses);
            return IdempotencyCheckResult.New();
        }

        if (existing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            Interlocked.Increment(ref _misses);
            return IdempotencyCheckResult.New();
        }

        bool match = CryptographicOperations.FixedTimeEquals(existing.PayloadHash, payloadHash);
        if (match)
        {
            Interlocked.Increment(ref _hits);
            return IdempotencyCheckResult.Replay(existing.StatusCode, existing.ResponseBody);
        }

        Interlocked.Increment(ref _conflicts);
        return IdempotencyCheckResult.Mismatch();
    }

    public async Task RegisterSuccessAsync(string key, string requestPath, byte[] payloadHash, int statusCode, string responseBody, CancellationToken cancellationToken = default)
    {
        var record = new IdempotentRequest
        {
            Key = key,
            RequestPath = requestPath,
            PayloadHash = payloadHash,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            CreatedAtUtc = DateTime.UtcNow,
            // 8.14-W4: TTL configurable en vez del 24 h fijo.
            ExpiresAtUtc = DateTime.UtcNow.Add(_ttl)
        };

        _context.IdempotentRequests.Add(record);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IdempotencyCheckResult> HandleConcurrentCollisionAsync(string key, string requestPath, byte[] payloadHash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _conflicts);

        // Intento de lectura de la fila confirmada por la transacción paralela
        var existing = await _context.IdempotentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key && r.RequestPath == requestPath, cancellationToken);

        if (existing != null)
        {
            if (CryptographicOperations.FixedTimeEquals(existing.PayloadHash, payloadHash))
            {
                Interlocked.Increment(ref _hits);
                return IdempotencyCheckResult.Replay(existing.StatusCode, existing.ResponseBody);
            }
            return IdempotencyCheckResult.Mismatch();
        }

        return IdempotencyCheckResult.Conflict();
    }
}
