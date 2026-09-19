using System;
using System.Linq;
using System.Text.RegularExpressions;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Guarda anti-drift modelo/migraciones: todo indice declarado en el modelo debe existir como
/// operacion CreateIndex (fluent) o como CREATE INDEX dentro del SQL crudo de alguna migracion.
/// Nace del hallazgo 8.142: indices vivian en el modelo y en el ModelSnapshot, la app arrancaba
/// sin PendingModelChangesWarning y los tests los tenian por EnsureCreated, pero ninguna migracion
/// los creaba -> la base real no los tenia. Detecta ademas migraciones huerfanas (sin el atributo
/// [Migration]): sus operaciones no entran al conjunto y sus indices aparecen como drift.
/// </summary>
public class MigrationIndexDriftTests
{
    private static readonly Regex CreateIndexRegex = new(
        @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:""(?<name>[^""]+)""|(?<name>[A-Za-z0-9_]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void SalesModel_EveryDeclaredIndexIsCreatedByAMigration()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            AssertNoIndexDrift(context);
        }
    }

    [Fact]
    public void InventoryModel_EveryDeclaredIndexIsCreatedByAMigration()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            AssertNoIndexDrift(context);
        }
    }

    private static void AssertNoIndexDrift(DbContext context)
    {
        var modelIndexes = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Select(index => index.GetDatabaseName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Values
            .Select(migrationType => (Migration)Activator.CreateInstance(migrationType.AsType())!)
            .ToList();

        var allOperations = migrations.SelectMany(migration => migration.UpOperations).ToList();

        var createdByFluentApi = allOperations
            .OfType<CreateIndexOperation>()
            .Select(operation => operation.Name);

        // Las migraciones con SQL crudo (pg_trgm, reparaciones, indices funcionales) tambien crean
        // indices; se extraen del texto para que el test no dependa de una lista de excepciones.
        var createdByRawSql = allOperations
            .OfType<SqlOperation>()
            .SelectMany(operation => CreateIndexRegex.Matches(operation.Sql).Cast<Match>())
            .Select(match => match.Groups["name"].Value);

        var createdSomewhere = createdByFluentApi
            .Concat(createdByRawSql)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var drift = modelIndexes
            .Except(createdSomewhere)
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            drift.Count == 0,
            $"Indices declarados en el modelo que ninguna migracion crea (drift): {string.Join(", ", drift)}");
    }
}
