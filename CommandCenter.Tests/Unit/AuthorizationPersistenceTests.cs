using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.Integration;
using Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T1 (8.150): capa de datos del hub de autorizaciones remotas. Cubre el contrato D1:
/// persistencia de las entidades Core, indice unico parcial de solicitudes Pending,
/// paridad modelo/snapshot y trigger append-only de PostgreSQL (gated por env var).
/// </summary>
[Collection(PostgresRealCollection.Name)]
public class AuthorizationPersistenceTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static AuthorizationRequest CreatePendingRequest(
        int requestedByUserId = 7,
        int? saleId = 445,
        AuthorizationActionType actionType = AuthorizationActionType.ManualPriceOverride)
    {
        return new AuthorizationRequest
        {
            ActionType = actionType,
            SaleId = saleId,
            RequestedByUserId = requestedByUserId,
            RequestedByName = "Cajero 01",
            Terminal = "Caja-01",
            Status = AuthorizationStatus.Pending,
            ContextJson = """{"productId":10,"quantity":2}""",
            ContextHash = new string('a', 64),
            CreatedAt = FixedUtc,
            ExpiresAt = FixedUtc.AddSeconds(60)
        };
    }

    private static AuthorizationAudit CreateApprovedAudit(int requestId)
    {
        return new AuthorizationAudit
        {
            RequestId = requestId,
            ActionType = AuthorizationActionType.ManualPriceOverride,
            SaleId = 445,
            Terminal = "Caja-01",
            RequestedByUserId = 7,
            RequestedByName = "Cajero 01",
            Status = AuthorizationStatus.Approved,
            ResolutionMode = AuthorizationResolutionMode.Remote,
            ResolvedByUserId = 2,
            ResolvedByName = "Admin Uno",
            Reason = "Precio acordado",
            ContextJson = """{"productId":10,"quantity":2}""",
            RequestedAt = FixedUtc,
            ResolvedAt = FixedUtc.AddSeconds(10)
        };
    }

    [Fact]
    public async Task AuthorizationEntities_RoundTrip_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var request = CreatePendingRequest();
            context.Set<AuthorizationRequest>().Add(request);
            await context.SaveChangesAsync();

            var audit = CreateApprovedAudit(request.Id);
            context.Set<AuthorizationAudit>().Add(audit);
            await context.SaveChangesAsync();

            var persistedRequest = await context.Set<AuthorizationRequest>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == request.Id);

            Assert.Equal(AuthorizationActionType.ManualPriceOverride, persistedRequest.ActionType);
            Assert.Equal(445, persistedRequest.SaleId);
            Assert.Equal(7, persistedRequest.RequestedByUserId);
            Assert.Equal("Cajero 01", persistedRequest.RequestedByName);
            Assert.Equal("Caja-01", persistedRequest.Terminal);
            Assert.Equal(AuthorizationStatus.Pending, persistedRequest.Status);
            Assert.Null(persistedRequest.ResolutionMode);
            Assert.Null(persistedRequest.ResolvedByUserId);
            Assert.Null(persistedRequest.ResolvedByName);
            Assert.Null(persistedRequest.ResolutionReason);
            Assert.Equal(request.ContextJson, persistedRequest.ContextJson);
            Assert.Equal(request.ContextHash, persistedRequest.ContextHash);
            Assert.Equal(FixedUtc, persistedRequest.CreatedAt);
            Assert.Equal(FixedUtc.AddSeconds(60), persistedRequest.ExpiresAt);
            Assert.Null(persistedRequest.ResolvedAt);
            Assert.Null(persistedRequest.ConsumedAt);

            var persistedAudit = await context.Set<AuthorizationAudit>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audit.Id);

            Assert.Equal(request.Id, persistedAudit.RequestId);
            Assert.Equal(AuthorizationActionType.ManualPriceOverride, persistedAudit.ActionType);
            Assert.Equal(445, persistedAudit.SaleId);
            Assert.Equal("Caja-01", persistedAudit.Terminal);
            Assert.Equal(7, persistedAudit.RequestedByUserId);
            Assert.Equal("Cajero 01", persistedAudit.RequestedByName);
            Assert.Equal(AuthorizationStatus.Approved, persistedAudit.Status);
            Assert.Equal(AuthorizationResolutionMode.Remote, persistedAudit.ResolutionMode);
            Assert.Equal(2, persistedAudit.ResolvedByUserId);
            Assert.Equal("Admin Uno", persistedAudit.ResolvedByName);
            Assert.Equal("Precio acordado", persistedAudit.Reason);
            Assert.Equal(audit.ContextJson, persistedAudit.ContextJson);
            Assert.Equal(FixedUtc, persistedAudit.RequestedAt);
            Assert.Equal(FixedUtc.AddSeconds(10), persistedAudit.ResolvedAt);

            var tracked = await context.Set<AuthorizationRequest>()
                .SingleAsync(candidate => candidate.Id == request.Id);
            tracked.Status = AuthorizationStatus.Approved;
            tracked.ResolutionMode = AuthorizationResolutionMode.Remote;
            tracked.ResolvedByUserId = 2;
            tracked.ResolvedByName = "Admin Uno";
            tracked.ResolutionReason = "Precio acordado";
            tracked.ResolvedAt = FixedUtc.AddSeconds(10);
            tracked.ConsumedAt = FixedUtc.AddSeconds(12);
            await context.SaveChangesAsync();

            var resolved = await context.Set<AuthorizationRequest>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == request.Id);

            Assert.Equal(AuthorizationStatus.Approved, resolved.Status);
            Assert.Equal(AuthorizationResolutionMode.Remote, resolved.ResolutionMode);
            Assert.Equal(2, resolved.ResolvedByUserId);
            Assert.Equal("Admin Uno", resolved.ResolvedByName);
            Assert.Equal("Precio acordado", resolved.ResolutionReason);
            Assert.Equal(FixedUtc.AddSeconds(10), resolved.ResolvedAt);
            Assert.Equal(FixedUtc.AddSeconds(12), resolved.ConsumedAt);
        }
    }

    [Fact]
    public async Task PendingRequest_DuplicateTuple_IsRejectedByPartialUniqueIndex_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            context.Set<AuthorizationRequest>().Add(CreatePendingRequest());
            await context.SaveChangesAsync();

            context.Set<AuthorizationRequest>().Add(CreatePendingRequest());
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.IsType<SqliteException>(duplicate.InnerException);
            context.ChangeTracker.Clear();

            var pendingCount = await context.Set<AuthorizationRequest>().CountAsync(
                candidate => candidate.RequestedByUserId == 7
                    && candidate.SaleId == 445
                    && candidate.ActionType == AuthorizationActionType.ManualPriceOverride
                    && candidate.Status == AuthorizationStatus.Pending);
            Assert.Equal(1, pendingCount);

            // La condicion parcial permite una fila terminal del mismo tuple
            // y una Pending con otra accion.
            var rejected = CreatePendingRequest();
            rejected.Status = AuthorizationStatus.Rejected;
            context.Set<AuthorizationRequest>().Add(rejected);
            context.Set<AuthorizationRequest>().Add(CreatePendingRequest(actionType: AuthorizationActionType.SaleCancellation));
            await context.SaveChangesAsync();

            Assert.Equal(3, await context.Set<AuthorizationRequest>().CountAsync());
        }
    }

    [Fact]
    public void SalesModel_DefinesAuthorizationIndexes_AndNoForeignKeys()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var requestType = context.Model.FindEntityType(typeof(AuthorizationRequest));
            Assert.NotNull(requestType);
            Assert.Empty(requestType!.GetForeignKeys());

            var pendingDedupe = Assert.Single(
                requestType.GetIndexes(),
                index => index.GetDatabaseName() == "IX_AuthorizationRequests_PendingDedupe");
            Assert.True(pendingDedupe.IsUnique);
            Assert.Equal("\"Status\" = 0", pendingDedupe.GetFilter());

            Assert.Single(
                requestType.GetIndexes(),
                index => index.GetDatabaseName() == "IX_AuthorizationRequests_Status_ExpiresAt");

            var auditType = context.Model.FindEntityType(typeof(AuthorizationAudit));
            Assert.NotNull(auditType);
            Assert.Empty(auditType!.GetForeignKeys());
            Assert.Single(
                auditType.GetIndexes(),
                index => index.GetDatabaseName() == "IX_AuthorizationAudits_RequestId");
        }
    }

    [Fact]
    public void SalesModel_MatchesMigrationSnapshot()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=model_snapshot_parity;Username=postgres;Password=postgres")
            .Options;
        using var context = new SalesDbContext(options);

        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var initializedSnapshot = context.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot!.Model, designTime: true);

        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(
                initializedSnapshot.GetRelationalModel(),
                context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(
            differences.Count == 0,
            $"El ModelSnapshot de Sales.Module tiene drift contra el modelo EF: {differences.Count} operaciones pendientes.");
    }

    [Fact]
    public void MigrationScript_ForNpgsql_EmitsSchemaAndAppendOnlyTrigger()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_script;Username=postgres;Password=postgres")
            .Options;
        using var context = new SalesDbContext(options);

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261005120000_AddUniqueClosureDateIndex",
            toMigration: "20261007120000_AddRemoteAuthorizationHubDataLayer");

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"AuthorizationRequests\"", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"AuthorizationAudits\"", script);
        Assert.Contains("IX_AuthorizationRequests_PendingDedupe", script);
        Assert.Contains("IX_AuthorizationRequests_Status_ExpiresAt", script);
        Assert.Contains("IX_AuthorizationAudits_RequestId", script);
        Assert.Contains("trg_authorization_audits_append_only", script);
        Assert.Contains("BEFORE UPDATE OR DELETE ON \"AuthorizationAudits\"", script);
    }

    [Fact]
    [Trait("Category", "RequiresDocker")]
    public async Task PostgreSql_AppendOnlyTrigger_RejectsAuditMutation_AndKeepsRow()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var databaseName = $"pos_authorization_persistence_{Guid.NewGuid():N}";
        var testConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName
        }.ConnectionString;
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        }.ConnectionString;

        await CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var options = new DbContextOptionsBuilder<SalesDbContext>()
                .UseNpgsql(testConnectionString)
                .Options;
            await using var context = new SalesDbContext(options);
            await context.Database.MigrateAsync();

            var request = CreatePendingRequest();
            context.Set<AuthorizationRequest>().Add(request);
            await context.SaveChangesAsync();

            var audit = CreateApprovedAudit(request.Id);
            context.Set<AuthorizationAudit>().Add(audit);
            await context.SaveChangesAsync();

            // El indice unico parcial tambien lo crea el SQL crudo de la migracion.
            context.Set<AuthorizationRequest>().Add(CreatePendingRequest());
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var duplicateError = Assert.IsType<PostgresException>(duplicate.InnerException);
            Assert.Equal("23505", duplicateError.SqlState);
            context.ChangeTracker.Clear();

            var updateError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AuthorizationAudits\" SET \"Reason\" = 'mutated' WHERE \"Id\" = {0}",
                    audit.Id));
            Assert.Equal("P0001", updateError.SqlState);

            var deleteError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"AuthorizationAudits\" WHERE \"Id\" = {0}",
                    audit.Id));
            Assert.Equal("P0001", deleteError.SqlState);

            var persistedAudit = await context.Set<AuthorizationAudit>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audit.Id);
            Assert.Null(persistedAudit.Reason);
            Assert.Equal(AuthorizationStatus.Approved, persistedAudit.Status);
            Assert.Equal("Admin Uno", persistedAudit.ResolvedByName);
            Assert.Equal(FixedUtc.AddSeconds(10), persistedAudit.ResolvedAt);
        }
        finally
        {
            await DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
