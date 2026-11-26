using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// WU1 static XAML contract: ExchangeRateView must render the canonical Spanish copy shared with
/// the Web client, keep the five history columns, expose the loading/empty states and the Single
/// regime notice, and declare a single "Sincronizar BCV" action.
/// </summary>
public sealed class ExchangeRateViewParityTests
{
    private static string ResolveViewsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Desktop.Client", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró Desktop.Client/Views desde el directorio de pruebas.");
    }

    private static string ReadExchangeRateView() =>
        File.ReadAllText(Path.Combine(ResolveViewsDirectory(), "ExchangeRateView.xaml"));

    [Fact]
    public void Header_DeclaresHybridAutomaticSyncCopy()
    {
        var source = ReadExchangeRateView();

        Assert.Contains("Text=\"Modo Híbrido\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Modo Manual", source, StringComparison.Ordinal);
        Assert.Contains(
            "Sincronización automática cada 2 horas. También puede sincronizar a demanda o ingresar la tasa del día manualmente.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Header_DeclaresCanonicalSectionAndActionCopy()
    {
        var source = ReadExchangeRateView();

        Assert.Contains("Text=\"Tasa de Cambio\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"TASA VIGENTE\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Nueva tasa del día\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Guardar\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Sincronizar BCV\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Reintentar\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Historial de Tasas\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OutdatedAlert_UsesCanonicalCopy()
    {
        var source = ReadExchangeRateView();

        Assert.Contains(
            "Tasa desactualizada: no se ha registrado una actualización en más de 24 horas. Sincronice con el BCV o ingrese la tasa oficial del día.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyBanner_UsesCanonicalCopy()
    {
        var source = ReadExchangeRateView();

        Assert.Contains(
            "Modo Solo Lectura: Su cuenta tiene permisos de consulta. Los cambios e importaciones están bloqueados.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void History_DeclaresCanonicalColumnsInOrder()
    {
        var source = ReadExchangeRateView();

        var fecha = source.IndexOf("Header=\"FECHA\"", StringComparison.Ordinal);
        var tasa = source.IndexOf("Header=\"TASA\"", StringComparison.Ordinal);
        var variacion = source.IndexOf("Header=\"VARIACIÓN\"", StringComparison.Ordinal);
        var actualizacion = source.IndexOf("Header=\"ACTUALIZACIÓN\"", StringComparison.Ordinal);
        var estado = source.IndexOf("Header=\"ESTADO\"", StringComparison.Ordinal);

        Assert.True(fecha >= 0, "Falta la columna FECHA.");
        Assert.True(tasa > fecha, "La columna TASA debe seguir a FECHA.");
        Assert.True(variacion > tasa, "La columna VARIACIÓN debe seguir a TASA.");
        Assert.True(actualizacion > variacion, "La columna ACTUALIZACIÓN debe seguir a VARIACIÓN.");
        Assert.True(estado > actualizacion, "La columna ESTADO debe seguir a ACTUALIZACIÓN.");
    }

    [Fact]
    public void History_DeclaresLoadingAndEmptyStates()
    {
        var source = ReadExchangeRateView();

        Assert.Contains("Text=\"Cargando historial...\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"No hay registros de cambios de tasa.\"", source, StringComparison.Ordinal);
        Assert.Contains("Binding IsHistoryLoading", source, StringComparison.Ordinal);
        Assert.Contains("Binding HasHistoryRecords", source, StringComparison.Ordinal);
        Assert.Contains("Binding HasNoHistoryRecords", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryGrid_IsPreservedWithItsColumns()
    {
        var source = ReadExchangeRateView();

        Assert.Contains("AutomationProperties.AutomationId=\"ExchangeRate_HistoryGrid\"", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding HistoryRows}\"", source, StringComparison.Ordinal);
        var canonicalColumns = Regex.Matches(source, "Header=\"(FECHA|TASA|VARIACIÓN|ACTUALIZACIÓN|ESTADO)\"");
        Assert.True(canonicalColumns.Count == 5, $"Se esperaban 5 columnas canónicas y se hallaron {canonicalColumns.Count}.");
    }

    [Fact]
    public void SyncAction_IsDeclaredExactlyOnce()
    {
        var source = ReadExchangeRateView();

        var syncLabels = Regex.Matches(source, "Sincronizar BCV");
        Assert.True(syncLabels.Count == 1, $"Se esperaba una sola acción Sincronizar BCV y se hallaron {syncLabels.Count}.");
    }

    [Fact]
    public void SingleMode_UsesPresenterVisibilityAndNoticeBinding()
    {
        var source = ReadExchangeRateView();

        Assert.Contains("SingleModeNotice", source, StringComparison.Ordinal);
        Assert.Contains("Path=IsSingle", source, StringComparison.Ordinal);
        Assert.Contains("DualOnlyVisibilityConverter", source, StringComparison.Ordinal);
        Assert.Contains("BooleanToVisibilityConverter", source, StringComparison.Ordinal);
    }
}
