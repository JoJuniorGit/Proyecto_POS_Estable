using System;
using System.IO;
using Xunit;

namespace CommandCenter.Tests.Unit;

public sealed class WpfNavigationSpanishLabelsTests
{
    [Fact]
    public void MainShellAndNavigationTitles_UseSpanishSectionNames()
    {
        var root = ResolveRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "Desktop.Client", "MainWindow.xaml"));
        var mainViewModel = File.ReadAllText(Path.Combine(
            root,
            "Desktop.Client.Core",
            "ViewModels",
            "MainViewModel.cs"));

        Assert.Contains("Text=\"Punto de Venta\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Caja\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Historial de Ventas\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Cat\u00E1logo\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Importaci\u00F3n de Productos\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Configuraci\u00F3n\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Tasa de Cambio\"", mainWindow, StringComparison.Ordinal);

        Assert.DoesNotContain("Text=\"Point of Sale\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Register\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Sales History\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Catalog\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Import Products\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Settings\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Exchange Rate\"", mainWindow, StringComparison.Ordinal);

        Assert.Contains("_title = \"Punto de Venta\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"PUNTO DE VENTA\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"INVENTARIO\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"HISTORIAL DE VENTAS\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"CONFIGURACI\u00D3N DEL SISTEMA\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"TASA DE CAMBIO\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"CAJA / REGISTRO DE CAJA\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"IMPORTACI\u00D3N Y EXPORTACI\u00D3N DE PRODUCTOS\"", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Title = \"CIERRE DIARIO Y ARQUEO DE CAJA\"", mainViewModel, StringComparison.Ordinal);

        Assert.DoesNotContain("Title = \"POINT OF SALE\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"INVENTORY\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"SALES HISTORY\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"SYSTEM SETTINGS\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"EXCHANGE RATE\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"REGISTER / CASH DRAWER\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"IMPORT PRODUCTS\"", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"DAILY CLOSING\"", mainViewModel, StringComparison.Ordinal);
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Desktop.Client", "MainWindow.xaml")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del repositorio desde el directorio de pruebas.");
    }
}
