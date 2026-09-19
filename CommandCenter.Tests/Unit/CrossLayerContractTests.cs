using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Guardas de contrato entre capas: valores que viajan por la base de datos, por el wire o por el
/// espejo JavaScript, y que ningun compilador puede verificar. Nacen del hallazgo 8.141-S2 (el
/// filtro web de movimientos apuntaba a 4 cuando el valor real era 2) y de la auditoria de
/// costuras de 8.142: renombrar un enum o cambiar un literal rompe en silencio sin que build,
/// tests ni lint lo detecten.
/// </summary>
public class CrossLayerContractTests
{
    [Fact]
    public void SaleStatus_WireStrings_AreStableBecauseClientsCompareLiterals()
    {
        // SaleDto.Status expone el nombre del enum (ToString). Lo comparan por literal:
        // SalesController.IsAuthorizedForSaleAsync ("OnHold") y CartViewModel/PosViewModel (WPF)
        // con "Pending" y "OnHold". Renombrar un miembro rompe esos flujos en silencio.
        Assert.Equal("Pending", Sales.Module.Entities.SaleStatus.Pending.ToString());
        Assert.Equal("Completed", Sales.Module.Entities.SaleStatus.Completed.ToString());
        Assert.Equal("Cancelled", Sales.Module.Entities.SaleStatus.Cancelled.ToString());
        Assert.Equal("OnHold", Sales.Module.Entities.SaleStatus.OnHold.ToString());
    }

    [Fact]
    public void CashDrawerStatus_Ordinals_AreStableBecauseThePartialIndexHardcodesZero()
    {
        // IX_CashDrawerSessions_SingleOpen se declara con HasFilter("\"Status\" = 0"). Si Open deja
        // de ser 0, la invariante "una sola sesion de caja abierta" desaparece en silencio y la
        // base permite dos sesiones concurrentes.
        Assert.Equal(0, (int)Sales.Module.Entities.CashDrawerStatus.Open);
        Assert.Equal(1, (int)Sales.Module.Entities.CashDrawerStatus.Closed);
    }

    [Fact]
    public void OutboxPendingLiteral_MatchesThePartialIndexFilter()
    {
        // El indice parcial IX_OutboxMessages_Status_NextRetryUtc filtra por el literal 'Pending'.
        // Si el valor por defecto o el que escribe el job dejan de coincidir con ese literal, los
        // mensajes quedan sin procesar o el planner pierde el indice.
        Assert.Equal("Pending", new Core.Entities.OutboxMessage().Status);

        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var filter = context.Model.GetEntityTypes()
                .Where(entity => entity.ClrType == typeof(Core.Entities.OutboxMessage))
                .SelectMany(entity => entity.GetIndexes())
                .Select(index => index.GetFilter())
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));

            Assert.NotNull(filter);
            Assert.Contains("Pending", filter!);
        }
    }

    [Fact]
    public void CashTransactionSource_ServerClientAndWebMirror_Agree()
    {
        // Espejo 1: enum del servidor (Sales.Module.Entities) contra el del cliente WPF.
        foreach (var name in Enum.GetNames<Sales.Module.Entities.CashTransactionSource>())
        {
            var serverValue = (int)Enum.Parse<Sales.Module.Entities.CashTransactionSource>(name);
            var clientValue = (int)Enum.Parse<Desktop.Client.Services.CashTransactionSource>(name);
            Assert.Equal(serverValue, clientValue);
        }

        // Espejo 2: el archivo JS del frontend, que duplica los ids como numeros literales.
        var jsPath = FindRepositoryFile(Path.Combine("Web.Frontend", "src", "constants", "cashTransactionSource.js"));
        var js = File.ReadAllText(jsPath);

        var comparados = 0;
        foreach (Match match in Regex.Matches(js, @"^\s*(\w+):\s*(\d+),", RegexOptions.Multiline))
        {
            var name = match.Groups[1].Value;
            if (!Enum.TryParse<Sales.Module.Entities.CashTransactionSource>(name, out var parsed)) continue;

            Assert.Equal((int)parsed, int.Parse(match.Groups[2].Value));
            comparados++;
        }

        Assert.Equal(Enum.GetNames<Sales.Module.Entities.CashTransactionSource>().Length, comparados);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"No se encontro '{relativePath}' subiendo desde {AppContext.BaseDirectory}");
    }
}
