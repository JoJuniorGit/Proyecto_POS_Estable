using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Guarda anti-drift modelo/migraciones. Cubre indices y columnas:
/// - Indices: todo indice declarado en el modelo debe existir como operacion CreateIndex (fluent)
///   o como CREATE INDEX dentro del SQL crudo de alguna migracion.
/// - Columnas (SEAM-03): toda columna escalar del modelo debe ser creada por una operacion
///   CreateTable/AddColumn o por SQL crudo (CREATE TABLE inline o ALTER TABLE ... ADD COLUMN).
/// Nace del hallazgo 8.142: indices vivian en el modelo y en el ModelSnapshot, la app arrancaba
/// sin PendingModelChangesWarning y los tests los tenian por EnsureCreated, pero ninguna migracion
/// los creaba -> la base real no los tenia. Detecta ademas migraciones huerfanas (sin el atributo
/// [Migration]): sus operaciones no entran al conjunto y sus indices aparecen como drift.
/// Los tokens de concurrencia `xmin` son shadow properties que PostgreSQL expone como pseudo-columna
/// de sistema: no requieren DDL, por eso se excluyen del chequeo de columnas.
/// </summary>
public class MigrationIndexDriftTests
{
    private static readonly Regex CreateIndexRegex = new(
        @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:""(?<name>[^""]+)""|(?<name>[A-Za-z0-9_]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // SEAM-03: el SQL crudo tambien crea columnas; se extraen del texto con el mismo criterio
    // que los indices, para que la guarda no dependa de una lista de excepciones.
    private static readonly Regex AddColumnRegex = new(
        @"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(""(?<table>[^""]+)""|(?<table>[A-Za-z0-9_]+))\s+ADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?(""(?<column>[^""]+)""|(?<column>[A-Za-z0-9_]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreateTableRegex = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(""(?<table>[^""]+)""|(?<table>[A-Za-z0-9_]+))\s*\((?<body>[\s\S]*?)\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RawColumnDefinitionRegex = new(
        @"^\s*""(?<column>[^""]+)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void SalesModel_EveryDeclaredIndexAndColumnIsCreatedByAMigration()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            AssertNoModelDrift(context);
        }
    }

    [Fact]
    public void InventoryModel_EveryDeclaredIndexAndColumnIsCreatedByAMigration()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            AssertNoModelDrift(context);
        }
    }

    private static void AssertNoModelDrift(DbContext context)
    {
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Values
            .Select(migrationType => (Migration)Activator.CreateInstance(migrationType.AsType())!)
            .ToList();

        var allOperations = migrations.SelectMany(migration => migration.UpOperations).ToList();

        AssertNoIndexDrift(context, allOperations);
        AssertNoColumnDrift(context, allOperations);
    }

    private static void AssertNoIndexDrift(DbContext context, List<MigrationOperation> allOperations)
    {
        var modelIndexes = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Select(index => index.GetDatabaseName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    private static void AssertNoColumnDrift(DbContext context, List<MigrationOperation> allOperations)
    {
        var modelColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in context.Model.GetEntityTypes())
        {
            var storeObject = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            if (storeObject is null) continue;

            foreach (var property in entity.GetProperties())
            {
                if (property.IsShadowProperty()) continue;

                var columnName = property.GetColumnName(storeObject.Value);
                if (!string.IsNullOrEmpty(columnName))
                {
                    modelColumns.Add(TableColumnKey(storeObject.Value.Name, columnName));
                }
            }
        }

        var createdSomewhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in allOperations.OfType<CreateTableOperation>())
        {
            if (operation.Name is null) continue;

            foreach (var column in operation.Columns)
            {
                createdSomewhere.Add(TableColumnKey(operation.Name, column.Name));
            }
        }

        foreach (var operation in allOperations.OfType<AddColumnOperation>())
        {
            createdSomewhere.Add(TableColumnKey(operation.Table, operation.Name));
        }

        // Una columna renombrada pasa a existir con su nombre nuevo sin un AddColumn adicional.
        foreach (var operation in allOperations.OfType<RenameColumnOperation>())
        {
            createdSomewhere.Add(TableColumnKey(operation.Table, operation.NewName));
        }

        foreach (var sql in allOperations.OfType<SqlOperation>().Select(operation => operation.Sql))
        {
            foreach (Match match in AddColumnRegex.Matches(sql))
            {
                createdSomewhere.Add(TableColumnKey(match.Groups["table"].Value, match.Groups["column"].Value));
            }

            foreach (Match tableMatch in CreateTableRegex.Matches(sql))
            {
                var table = tableMatch.Groups["table"].Value;
                foreach (Match columnMatch in RawColumnDefinitionRegex.Matches(tableMatch.Groups["body"].Value))
                {
                    createdSomewhere.Add(TableColumnKey(table, columnMatch.Groups["column"].Value));
                }
            }
        }

        var drift = modelColumns
            .Except(createdSomewhere)
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            drift.Count == 0,
            $"Columnas declaradas en el modelo que ninguna migracion crea (drift): {string.Join(", ", drift)}");
    }

    private static string TableColumnKey(string table, string column) => $"{table}.{column}";
}
