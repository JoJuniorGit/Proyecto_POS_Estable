using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T6-U19: static XAML contract audit. Every WPF profile binding must target the
/// observable MonetaryProfilePresenter; static CurrencyDisplay bindings would freeze at view
/// construction and defeat the refresh pipeline.
/// </summary>
public class XamlMonetaryBindingAuditTests
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
    [Fact]
    public void WpfViews_DoNotUseStaticCurrencyDisplayBindings()
    {
        var views = ResolveViewsDirectory();

        var offenders = Directory.EnumerateFiles(views, "*.xaml")
            .Where(file => File.ReadAllText(file).Contains("x:Static core:CurrencyDisplay", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Vistas con binding estático de moneda: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void WpfViews_UseTheObservablePresenterForProfileLabels()
    {
        var views = ResolveViewsDirectory();
        var count = Directory.EnumerateFiles(views, "*.xaml")
            .Count(file => File.ReadAllText(file).Contains("MonetaryProfilePresenter.Instance", StringComparison.Ordinal));

        Assert.True(count > 0, "Ninguna vista WPF usa MonetaryProfilePresenter.Instance.");
    }
}

