using Core.Entities;
using Core.Security;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace Sales.Module.Services;

/// <summary>
/// 8.150 (T2, design D2): maquina de estados de autorizaciones remotas. Toda transicion
/// terminal (Approved/Rejected/Expired) se reclama con un UPDATE condicional sobre
/// Status = Pending y agrega exactamente una fila AuthorizationAudit en la misma transaccion;
/// la primera respuesta gana y el consumo del token es single-use.
/// </summary>
public class AuthorizationService : IAuthorizationService
{
    private const int MaxContextJsonBytes = 4096;

    // W1: el indice unico parcial de dedupe y las dos formas de nombrarlo que exponen los
    // proveedores (Npgsql: constraint/indice; SQLite: columnas, nunca el nombre del indice).
    private const string PendingDedupeIndexName = "IX_AuthorizationRequests_PendingDedupe";
    private const string SqlitePendingDedupeViolation =
        "UNIQUE constraint failed: AuthorizationRequests.RequestedByUserId, AuthorizationRequests.SaleId, AuthorizationRequests.ActionType";

    private static readonly string _dummyPasswordHash = PasswordHasher.HashPassword("dummy-8.150-local");

    private readonly SalesDbContext _db;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _tokenTtl;

    public AuthorizationService(SalesDbContext db, TimeSpan? requestTimeout = null, TimeSpan? tokenTtl = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        _tokenTtl = tokenTtl ?? TimeSpan.FromSeconds(60);

        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "El timeout de la solicitud debe ser positivo.");
        }

        if (_tokenTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenTtl), "El TTL del token debe ser positivo.");
        }

        _db = db;
    }

    public static bool IsElevatedRole(UserRole role) => role is UserRole.Admin or UserRole.Manager;

    public async Task<CreateAuthorizationResult> CreateAsync(CreateAuthorizationRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsElevatedRole(request.RequesterRole))
        {
            return new CreateAuthorizationResult(CreateAuthorizationOutcome.ElevationNotRequired, Message: AuthorizationMessages.ElevationNotRequired);
        }

        if (request.RequesterRole == UserRole.Driver)
        {
            return new CreateAuthorizationResult(CreateAuthorizationOutcome.DriverBlocked, Message: AuthorizationMessages.DriverBlocked);
        }

        if (!Enum.IsDefined(request.ActionType))
        {
            return new CreateAuthorizationResult(CreateAuthorizationOutcome.UnknownAction, Message: AuthorizationMessages.UnknownAction);
        }

        if (Encoding.UTF8.GetByteCount(request.ContextJson) > MaxContextJsonBytes)
        {
            return new CreateAuthorizationResult(CreateAuthorizationOutcome.ContextTooLarge, Message: AuthorizationMessages.ContextTooLarge);
        }

        if (!await CanRequestForSaleAsync(request, cancellationToken))
        {
            return new CreateAuthorizationResult(CreateAuthorizationOutcome.SaleAccessDenied, Message: AuthorizationMessages.SaleAccessDenied);
        }

        var now = UtcNowSeconds();
        CreateAuthorizationResult result = null!;
        DbUpdateException? pendingDedupeConflict = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // La estrategia de reintento vuelve a ejecutar el lambda: se descarta el tracker
                    // para no duplicar inserciones de intentos previos.
                    _db.ChangeTracker.Clear();

                    await using var transaction = _db.Database.IsRelational()
                        ? await _db.Database.BeginTransactionAsync(cancellationToken)
                        : null;

                    var existing = await FindPendingRequestAsync(request, cancellationToken);
                    if (existing != null && existing.ExpiresAt > now)
                    {
                        result = new CreateAuthorizationResult(CreateAuthorizationOutcome.Deduplicated, ToDto(existing, now));
                        if (transaction != null)
                        {
                            await transaction.CommitAsync(cancellationToken);
                        }

                        return;
                    }

                    if (existing != null)
                    {
                        // Dedupe contra una Pending ya vencida: se cierra como Expired (con auditoria)
                        // y se emite una solicitud nueva; devolverla con lifetime 0 bloquearia la terminal.
                        if (await ClaimExpiryAsync(existing.Id, now, cancellationToken) == 1)
                        {
                            _db.AuthorizationAudits.Add(BuildAudit(existing, AuthorizationStatus.Expired, null, null, null, null, now));
                        }
                    }

                    var entity = new AuthorizationRequest
                    {
                        ActionType = request.ActionType,
                        SaleId = request.SaleId,
                        RequestedByUserId = request.RequestedByUserId,
                        RequestedByName = request.RequestedByName,
                        Terminal = request.Terminal,
                        Status = AuthorizationStatus.Pending,
                        ContextJson = request.ContextJson,
                        ContextHash = request.ContextHash,
                        CreatedAt = now,
                        ExpiresAt = now.Add(_requestTimeout)
                    };

                    _db.AuthorizationRequests.Add(entity);
                    await _db.SaveChangesAsync(cancellationToken);

                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    result = new CreateAuthorizationResult(CreateAuthorizationOutcome.Created, ToDto(entity, now));
                });

                return result;
            }
            catch (DbUpdateException ex) when (IsPendingDedupeUniqueViolation(ex))
            {
                // W1: otra terminal gano la carrera de dedupe entre el pre-chequeo y el INSERT.
                // La transaccion se revirtio al desenrollar; se relee la Pending ganadora y se
                // devuelve como dedupe. Si desaparecio (resuelta o expirada en el interin), un
                // unico reintento deja que el pre-chequeo vuelva a decidir. Cualquier otro
                // DbUpdateException no coincide con el filtro y se propaga.
                pendingDedupeConflict = ex;
                _db.ChangeTracker.Clear();

                var winner = await FindPendingRequestAsync(request, cancellationToken);
                if (winner is not null)
                {
                    return new CreateAuthorizationResult(CreateAuthorizationOutcome.Deduplicated, ToDto(winner, UtcNowSeconds()));
                }
            }
        }

        throw (Exception?)pendingDedupeConflict ?? new InvalidOperationException("No se pudo resolver la creación concurrente de la solicitud de autorización.");
    }

    public async Task<ResolveAuthorizationResult> ResolveAsync(
        int requestId,
        int resolverUserId,
        string resolverName,
        bool approved,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolverName);

        ResolveAuthorizationResult result = null!;

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _db.ChangeTracker.Clear();

            var now = UtcNowSeconds();
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var request = await LoadRequestAsync(requestId, cancellationToken);
            if (request == null)
            {
                result = new ResolveAuthorizationResult(ResolveAuthorizationOutcome.NotFound, Message: AuthorizationMessages.RequestNotFound);
                return;
            }

            if (request.Status is AuthorizationStatus.Approved or AuthorizationStatus.Rejected)
            {
                result = AlreadyResolvedResult(request, now);
                return;
            }

            if (request.Status == AuthorizationStatus.Expired)
            {
                result = new ResolveAuthorizationResult(
                    ResolveAuthorizationOutcome.Expired,
                    ToDto(request, now),
                    AuthorizationMessages.RequestExpired);
                return;
            }

            if (now >= request.ExpiresAt)
            {
                if (await ClaimExpiryAsync(request.Id, now, cancellationToken) == 1)
                {
                    _db.AuthorizationAudits.Add(BuildAudit(request, AuthorizationStatus.Expired, null, null, null, null, now));
                    await _db.SaveChangesAsync(cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                }

                result = new ResolveAuthorizationResult(
                    ResolveAuthorizationOutcome.Expired,
                    ToDto(request, now) with { Status = AuthorizationStatus.Expired, ResolvedAt = now },
                    AuthorizationMessages.RequestExpired);
                return;
            }

            var targetStatus = approved ? AuthorizationStatus.Approved : AuthorizationStatus.Rejected;
            var claimed = await ClaimResolutionAsync(request.Id, targetStatus, AuthorizationResolutionMode.Remote, resolverUserId, resolverName, reason, now, cancellationToken);

            if (claimed == 1)
            {
                _db.AuthorizationAudits.Add(BuildAudit(request, targetStatus, AuthorizationResolutionMode.Remote, resolverUserId, resolverName, reason, now));
                await _db.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                result = new ResolveAuthorizationResult(
                    ResolveAuthorizationOutcome.Resolved,
                    ToDto(request, now) with
                    {
                        Status = targetStatus,
                        ResolutionMode = AuthorizationResolutionMode.Remote,
                        ResolvedByUserId = resolverUserId,
                        ResolvedByName = resolverName,
                        ResolutionReason = reason,
                        ResolvedAt = now
                    });
                return;
            }

            result = await MapCurrentResolveAsync(requestId, now, cancellationToken);
        });

        return result;
    }

    public async Task<LocalResolveResult> ResolveLocalAsync(
        int requestId,
        string username,
        string password,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        LocalResolveResult result = null!;

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _db.ChangeTracker.Clear();

            var now = UtcNowSeconds();
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var request = await LoadRequestAsync(requestId, cancellationToken);
            if (request == null)
            {
                result = new LocalResolveResult(LocalResolveOutcome.NotFound, Message: AuthorizationMessages.RequestNotFound);
                return;
            }

            if (request.Status is AuthorizationStatus.Approved or AuthorizationStatus.Rejected)
            {
                result = new LocalResolveResult(
                    LocalResolveOutcome.AlreadyResolved,
                    ToDto(request, now),
                    Message: AuthorizationMessages.AlreadyResolvedBy(request.ResolvedByName ?? "otro usuario"));
                return;
            }

            if (request.Status == AuthorizationStatus.Expired)
            {
                result = new LocalResolveResult(
                    LocalResolveOutcome.Expired,
                    ToDto(request, now),
                    Message: AuthorizationMessages.RequestExpired);
                return;
            }

            if (now >= request.ExpiresAt)
            {
                if (await ClaimExpiryAsync(request.Id, now, cancellationToken) == 1)
                {
                    _db.AuthorizationAudits.Add(BuildAudit(request, AuthorizationStatus.Expired, null, null, null, null, now));
                    await _db.SaveChangesAsync(cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                }

                result = new LocalResolveResult(
                    LocalResolveOutcome.Expired,
                    ToDto(request, now) with { Status = AuthorizationStatus.Expired, ResolvedAt = now },
                    Message: AuthorizationMessages.RequestExpired);
                return;
            }

            var user = await FindUserAsync(username, cancellationToken);
            if (user == null)
            {
                PasswordHasher.VerifyPassword(password, _dummyPasswordHash);
                result = new LocalResolveResult(LocalResolveOutcome.InvalidCredentials, ToDto(request, now), Message: AuthorizationMessages.InvalidCredentials);
                return;
            }

            var utcNow = DateTime.UtcNow;
            if (LoginLockoutPolicy.IsLockedOut(user, utcNow))
            {
                result = new LocalResolveResult(LocalResolveOutcome.LockedOut, ToDto(request, now), Message: AuthorizationMessages.InvalidCredentials);
                return;
            }

            LoginLockoutPolicy.ClearExpiredLockout(user, utcNow);

            if (!user.IsActive || string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                result = new LocalResolveResult(LocalResolveOutcome.InvalidCredentials, ToDto(request, now), Message: AuthorizationMessages.InvalidCredentials);
                return;
            }

            if (string.IsNullOrWhiteSpace(password) || !PasswordHasher.VerifyPassword(password, user.PasswordHash))
            {
                LoginLockoutPolicy.RegisterFailedAttempt(user, utcNow);
                await _db.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                result = new LocalResolveResult(LocalResolveOutcome.InvalidCredentials, ToDto(request, now), Message: AuthorizationMessages.InvalidCredentials);
                return;
            }

            if (!IsElevatedRole(user.Role))
            {
                result = new LocalResolveResult(LocalResolveOutcome.InvalidCredentials, ToDto(request, now), Message: AuthorizationMessages.InvalidCredentials);
                return;
            }

            var resolutionNow = UtcNowSeconds();
            var claimed = await ClaimResolutionAsync(request.Id, AuthorizationStatus.Approved, AuthorizationResolutionMode.Local, user.Id, user.Name, reason, resolutionNow, cancellationToken);

            if (claimed == 1)
            {
                LoginLockoutPolicy.ResetOnSuccess(user);
                _db.AuthorizationAudits.Add(BuildAudit(request, AuthorizationStatus.Approved, AuthorizationResolutionMode.Local, user.Id, user.Name, reason, resolutionNow));
                await _db.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                result = new LocalResolveResult(
                    LocalResolveOutcome.Resolved,
                    ToDto(request, resolutionNow) with
                    {
                        Status = AuthorizationStatus.Approved,
                        ResolutionMode = AuthorizationResolutionMode.Local,
                        ResolvedByUserId = user.Id,
                        ResolvedByName = user.Name,
                        ResolutionReason = reason,
                        ResolvedAt = resolutionNow
                    },
                    user.Id,
                    user.Name);
                return;
            }

            // Perdio la carrera: se descarta cualquier mutacion en memoria del usuario y la
            // transaccion se revierte al disponerse (nada se persistio).
            _db.ChangeTracker.Clear();
            result = await MapCurrentLocalAsync(requestId, resolutionNow, cancellationToken);
        });

        return result;
    }

    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNowSeconds();
        var affected = 0;

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _db.ChangeTracker.Clear();

            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var stale = await _db.AuthorizationRequests
                .AsNoTracking()
                .Where(candidate => candidate.Status == AuthorizationStatus.Pending && candidate.ExpiresAt < now)
                .ToListAsync(cancellationToken);

            var attemptAffected = 0;
            foreach (var request in stale)
            {
                // UPDATE condicional por fila: solo la transicion efectiva agrega auditoria.
                if (await ClaimExpiryAsync(request.Id, now, cancellationToken) == 1)
                {
                    _db.AuthorizationAudits.Add(BuildAudit(request, AuthorizationStatus.Expired, null, null, null, null, now));
                    attemptAffected++;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            affected = attemptAffected;
        });

        return affected;
    }

    public async Task<ConsumeAuthorizationResult> TryConsumeAsync(
        int requestId,
        int userId,
        AuthorizationActionType actionType,
        int? saleId,
        string contextHash,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNowSeconds();
        var request = await LoadRequestAsync(requestId, cancellationToken);
        if (request == null)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.NotFound);
        }

        if (request.Status != AuthorizationStatus.Approved)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.NotApproved);
        }

        if (request.ConsumedAt.HasValue)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.AlreadyConsumed);
        }

        if (request.RequestedByUserId != userId)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.WrongUser);
        }

        if (request.ActionType != actionType)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.WrongAction);
        }

        if (request.SaleId != saleId)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.WrongSale);
        }

        if (!string.Equals(request.ContextHash, contextHash, StringComparison.Ordinal))
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.ContextMismatch);
        }

        var tokenFloor = now.Subtract(_tokenTtl);
        if (!request.ResolvedAt.HasValue || request.ResolvedAt.Value < tokenFloor)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.TokenExpired);
        }

        var claimed = await _db.AuthorizationRequests
            .Where(candidate => candidate.Id == requestId
                && candidate.Status == AuthorizationStatus.Approved
                && candidate.ConsumedAt == null
                && candidate.RequestedByUserId == userId
                && candidate.ResolvedAt >= tokenFloor)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.ConsumedAt, now), cancellationToken);

        if (claimed == 1)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.Consumed);
        }

        var current = await LoadRequestAsync(requestId, cancellationToken);
        if (current == null)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.NotFound);
        }

        if (current.ConsumedAt.HasValue)
        {
            return new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.AlreadyConsumed);
        }

        return current.Status != AuthorizationStatus.Approved
            ? new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.NotApproved)
            : new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.TokenExpired);
    }

    public async Task<AuthorizationRequestDto?> GetAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default)
    {
        var request = await LoadRequestAsync(requestId, cancellationToken);
        if (request == null || (!viewerIsElevated && request.RequestedByUserId != viewerUserId))
        {
            return null;
        }

        return ToDto(request, UtcNowSeconds());
    }

    private async Task<bool> CanRequestForSaleAsync(CreateAuthorizationRequestDto request, CancellationToken cancellationToken)
    {
        if (!request.SaleId.HasValue)
        {
            return true;
        }

        var sale = await _db.Sales
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.SaleId.Value)
            .Select(candidate => new { candidate.Status, candidate.CashierId })
            .FirstOrDefaultAsync(cancellationToken);

        if (sale == null)
        {
            // Espeja IsAuthorizedForSaleAsync: venta inexistente (KeyNotFound) se tolera.
            return true;
        }

        if (sale.Status == SaleStatus.OnHold)
        {
            return true;
        }

        return sale.CashierId == request.RequestedByUserId;
    }

    private async Task<AuthorizationRequest?> FindPendingRequestAsync(CreateAuthorizationRequestDto request, CancellationToken cancellationToken)
    {
        var query = _db.AuthorizationRequests
            .AsNoTracking()
            .Where(candidate => candidate.RequestedByUserId == request.RequestedByUserId
                && candidate.ActionType == request.ActionType
                && candidate.Status == AuthorizationStatus.Pending);

        // Comparacion explicita contra NULL: el indice unico parcial no dedupe (user, NULL, action)
        // porque NULL nunca es igual a NULL; el servicio es dueno de ese caso (residual T1).
        query = request.SaleId.HasValue
            ? query.Where(candidate => candidate.SaleId == request.SaleId.Value)
            : query.Where(candidate => candidate.SaleId == null);

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// W1: identifica la violacion del indice unico parcial de dedupe sin tragarse ninguna otra
    /// violacion de unicidad (el mensaje/constraint de otros indices no coincide).
    /// </summary>
    private static bool IsPendingDedupeUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pg)
        {
            return pg.SqlState == "23505"
                && (string.Equals(pg.ConstraintName, PendingDedupeIndexName, StringComparison.Ordinal)
                    || NamesPendingDedupe(pg.Message));
        }

        // SQLite (tests deterministas) nombra la violacion por columnas, no por el indice parcial.
        return NamesPendingDedupe(ex.InnerException?.Message) || NamesPendingDedupe(ex.Message);
    }

    private static bool NamesPendingDedupe(string? message)
        => message?.Contains(PendingDedupeIndexName, StringComparison.Ordinal) == true
            || message?.Contains(SqlitePendingDedupeViolation, StringComparison.Ordinal) == true;

    private async Task<User?> FindUserAsync(string searchInput, CancellationToken cancellationToken)
    {
        var searchLower = searchInput.ToLower();
        var withV = searchInput.StartsWith("V-", StringComparison.OrdinalIgnoreCase) ? searchLower : "v-" + searchLower;
        var digitsOnly = Regex.Replace(searchInput, @"[^\d]", "");

        return await _db.Users.FirstOrDefaultAsync(user => user.Username.ToLower() == searchLower ||
                                                           user.Cedula.ToLower() == searchLower ||
                                                           user.Cedula.ToLower() == withV ||
                                                           (digitsOnly.Length > 0 && (user.Cedula.ToLower() == "v-" + digitsOnly || user.Cedula == digitsOnly)), cancellationToken);
    }

    private Task<AuthorizationRequest?> LoadRequestAsync(int requestId, CancellationToken cancellationToken)
        => _db.AuthorizationRequests.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);

    private Task<int> ClaimResolutionAsync(
        int requestId,
        AuthorizationStatus targetStatus,
        AuthorizationResolutionMode mode,
        int resolverUserId,
        string resolverName,
        string? reason,
        DateTime resolvedAt,
        CancellationToken cancellationToken)
        => _db.AuthorizationRequests
            .Where(candidate => candidate.Id == requestId && candidate.Status == AuthorizationStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, targetStatus)
                .SetProperty(candidate => candidate.ResolutionMode, mode)
                .SetProperty(candidate => candidate.ResolvedByUserId, resolverUserId)
                .SetProperty(candidate => candidate.ResolvedByName, resolverName)
                .SetProperty(candidate => candidate.ResolutionReason, reason)
                .SetProperty(candidate => candidate.ResolvedAt, resolvedAt), cancellationToken);

    private Task<int> ClaimExpiryAsync(int requestId, DateTime now, CancellationToken cancellationToken)
        => _db.AuthorizationRequests
            .Where(candidate => candidate.Id == requestId
                && candidate.Status == AuthorizationStatus.Pending
                && candidate.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, AuthorizationStatus.Expired)
                .SetProperty(candidate => candidate.ResolvedAt, now), cancellationToken);

    private static AuthorizationAudit BuildAudit(
        AuthorizationRequest request,
        AuthorizationStatus status,
        AuthorizationResolutionMode? mode,
        int? resolverUserId,
        string? resolverName,
        string? reason,
        DateTime resolvedAt)
        => new()
        {
            RequestId = request.Id,
            ActionType = request.ActionType,
            SaleId = request.SaleId,
            Terminal = request.Terminal,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByName = request.RequestedByName,
            Status = status,
            ResolutionMode = mode,
            ResolvedByUserId = resolverUserId,
            ResolvedByName = resolverName,
            Reason = reason,
            ContextJson = request.ContextJson,
            RequestedAt = request.CreatedAt,
            ResolvedAt = resolvedAt
        };

    private static ResolveAuthorizationResult AlreadyResolvedResult(AuthorizationRequest request, DateTime now)
        => new(
            ResolveAuthorizationOutcome.AlreadyResolved,
            ToDto(request, now),
            AuthorizationMessages.AlreadyResolvedBy(request.ResolvedByName ?? "otro usuario"));

    private async Task<ResolveAuthorizationResult> MapCurrentResolveAsync(int requestId, DateTime now, CancellationToken cancellationToken)
    {
        var current = await LoadRequestAsync(requestId, cancellationToken);
        if (current == null)
        {
            return new ResolveAuthorizationResult(ResolveAuthorizationOutcome.NotFound, Message: AuthorizationMessages.RequestNotFound);
        }

        if (current.Status is AuthorizationStatus.Approved or AuthorizationStatus.Rejected)
        {
            return AlreadyResolvedResult(current, now);
        }

        if (current.Status == AuthorizationStatus.Expired || now >= current.ExpiresAt)
        {
            return new ResolveAuthorizationResult(
                ResolveAuthorizationOutcome.Expired,
                ToDto(current, now) with { Status = AuthorizationStatus.Expired },
                AuthorizationMessages.RequestExpired);
        }

        return AlreadyResolvedResult(current, now);
    }

    private async Task<LocalResolveResult> MapCurrentLocalAsync(int requestId, DateTime now, CancellationToken cancellationToken)
    {
        var current = await LoadRequestAsync(requestId, cancellationToken);
        if (current == null)
        {
            return new LocalResolveResult(LocalResolveOutcome.NotFound, Message: AuthorizationMessages.RequestNotFound);
        }

        if (current.Status is AuthorizationStatus.Approved or AuthorizationStatus.Rejected)
        {
            return new LocalResolveResult(
                LocalResolveOutcome.AlreadyResolved,
                ToDto(current, now),
                Message: AuthorizationMessages.AlreadyResolvedBy(current.ResolvedByName ?? "otro usuario"));
        }

        if (current.Status == AuthorizationStatus.Expired || now >= current.ExpiresAt)
        {
            return new LocalResolveResult(
                LocalResolveOutcome.Expired,
                ToDto(current, now) with { Status = AuthorizationStatus.Expired },
                Message: AuthorizationMessages.RequestExpired);
        }

        return new LocalResolveResult(
            LocalResolveOutcome.AlreadyResolved,
            ToDto(current, now),
            Message: AuthorizationMessages.AlreadyResolvedBy(current.ResolvedByName ?? "otro usuario"));
    }

    private static AuthorizationRequestDto ToDto(AuthorizationRequest request, DateTime now)
    {
        var remainingLifetimeSeconds = request.Status == AuthorizationStatus.Pending
            ? (int)Math.Max(0, Math.Ceiling((request.ExpiresAt - now).TotalSeconds))
            : 0;

        return new AuthorizationRequestDto
        {
            Id = request.Id,
            ActionType = request.ActionType,
            SaleId = request.SaleId,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByName = request.RequestedByName,
            Terminal = request.Terminal,
            Status = request.Status,
            RemainingLifetimeSeconds = remainingLifetimeSeconds,
            ResolutionMode = request.ResolutionMode,
            ResolvedByUserId = request.ResolvedByUserId,
            ResolvedByName = request.ResolvedByName,
            ResolutionReason = request.ResolutionReason,
            ContextJson = request.ContextJson,
            ContextHash = request.ContextHash,
            CreatedAt = request.CreatedAt,
            ExpiresAt = request.ExpiresAt,
            ResolvedAt = request.ResolvedAt,
            ConsumedAt = request.ConsumedAt
        };
    }

    private static DateTime UtcNowSeconds()
    {
        var utcNow = DateTime.UtcNow;
        return utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerSecond));
    }
}
