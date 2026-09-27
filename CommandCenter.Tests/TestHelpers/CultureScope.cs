using System;
using System.Globalization;

namespace CommandCenter.Tests.TestHelpers;

/// <summary>
/// 8.143: fuerza la cultura del hilo durante el scope para que los tests de formato
/// validen la convención es-VE también en hosts en-US (el runner de CI usa en-US).
/// </summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _original;

    public CultureScope(string cultureName)
    {
        _original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
    }

    public void Dispose() => CultureInfo.CurrentCulture = _original;
}
