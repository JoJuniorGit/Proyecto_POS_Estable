using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;
using Core.Constants;
using Core.DTOs;
using Core.Interfaces;

namespace Inventory.Module.Services;

/// <summary>
/// 8.211-T1 (S1/S2): perfil monetario configurable sobre las cinco claves de settings. El servicio
/// solo almacena y valida configuración: no convierte montos, no consulta tasas y no modifica
/// snapshots financieros. Un perfil almacenado inválido se degrada a los defaults con
/// <c>IsConfigured = false</c> en lugar de romper la lectura.
/// </summary>
public class MonetaryProfileService : IMonetaryProfileService
{
    private const string UndefinedModeMessage = "El modo monetario debe ser Single o Dual.";
    private const string MalformedPrimaryCodeMessage = "El código de la moneda principal debe ser un código ISO 4217 de tres letras.";
    private const string PrimarySymbolMessage = "El símbolo de la moneda principal es obligatorio y no puede superar los 8 caracteres.";
    private const string ControlCharacterMessage = "El símbolo de la moneda no puede contener caracteres de control.";
    private const string SingleWithSecondaryMessage = "En modo Single no se debe configurar una moneda secundaria.";
    private const string DualWithoutSecondaryMessage = "En modo Dual se requiere el código de la moneda secundaria.";
    private const string MalformedSecondaryCodeMessage = "El código de la moneda secundaria debe ser un código ISO 4217 de tres letras.";
    private const string SameCurrencyMessage = "La moneda secundaria no puede ser igual a la principal.";
    private const string SecondarySymbolMessage = "El símbolo de la moneda secundaria es obligatorio y no puede superar los 8 caracteres.";
    private const int MaxSymbolLength = 8;

    private readonly ISystemSettingsService _settingsService;

    public MonetaryProfileService(ISystemSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task<MonetaryProfileDto> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modeValue = await _settingsService.GetSettingAsync(SettingKeys.Monetary.Mode);
        if (!Enum.TryParse<MonetaryMode>(modeValue?.Trim(), ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            return CreateDefaultProfile();
        }

        var primaryCode = await _settingsService.GetSettingAsync(SettingKeys.Monetary.PrimaryCurrencyCode);
        var primarySymbol = await _settingsService.GetSettingAsync(SettingKeys.Monetary.PrimaryCurrencySymbol);
        var secondaryCode = await _settingsService.GetSettingAsync(SettingKeys.Monetary.SecondaryCurrencyCode);
        var secondarySymbol = await _settingsService.GetSettingAsync(SettingKeys.Monetary.SecondaryCurrencySymbol);

        var candidate = new MonetaryProfileDto
        {
            Mode = mode,
            PrimaryCurrencyCode = primaryCode ?? string.Empty,
            PrimaryCurrencySymbol = primarySymbol ?? string.Empty,
            // En Single los valores secundarios almacenados son residuos de un modo anterior: se
            // ignoran en la lectura y se preservan en disco para un eventual regreso a Dual.
            SecondaryCurrencyCode = mode == MonetaryMode.Dual ? secondaryCode : null,
            SecondaryCurrencySymbol = mode == MonetaryMode.Dual ? secondarySymbol : null
        };

        return TryValidateAndNormalize(candidate, out var normalized, out _)
            ? normalized
            : CreateDefaultProfile();
    }

    public async Task<MonetaryProfileDto> SaveAsync(MonetaryProfileDto profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryValidateAndNormalize(profile, out var normalized, out var errorMessage))
        {
            throw new ArgumentException(errorMessage);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SettingKeys.Monetary.Mode] = normalized.Mode.ToString(),
            [SettingKeys.Monetary.PrimaryCurrencyCode] = normalized.PrimaryCurrencyCode,
            [SettingKeys.Monetary.PrimaryCurrencySymbol] = normalized.PrimaryCurrencySymbol
        };

        // En Single no se escriben las claves secundarias: si existían de un Dual anterior se
        // conservan para permitir un cambio de vuelta sin perder la configuración.
        if (normalized.Mode == MonetaryMode.Dual)
        {
            values[SettingKeys.Monetary.SecondaryCurrencyCode] = normalized.SecondaryCurrencyCode!;
            values[SettingKeys.Monetary.SecondaryCurrencySymbol] = normalized.SecondaryCurrencySymbol!;
        }

        await _settingsService.SetSettingsAsync(values);

        return normalized;
    }

    private static bool TryValidateAndNormalize(
        MonetaryProfileDto profile,
        out MonetaryProfileDto normalized,
        out string? errorMessage)
    {
        normalized = CreateDefaultProfile();
        errorMessage = null;

        if (!Enum.IsDefined(profile.Mode))
        {
            errorMessage = UndefinedModeMessage;
            return false;
        }

        var primaryCode = Iso4217.Normalize(profile.PrimaryCurrencyCode);
        if (IsMalformedCode(primaryCode))
        {
            errorMessage = MalformedPrimaryCodeMessage;
            return false;
        }

        if (!Iso4217.IsSupported(primaryCode))
        {
            errorMessage = $"El código '{primaryCode}' no es un código ISO 4217 reconocido.";
            return false;
        }

        var primarySymbol = profile.PrimaryCurrencySymbol?.Trim();
        if (string.IsNullOrEmpty(primarySymbol) || primarySymbol.Length > MaxSymbolLength)
        {
            errorMessage = PrimarySymbolMessage;
            return false;
        }

        var secondaryCode = Iso4217.Normalize(profile.SecondaryCurrencyCode);
        var secondarySymbol = profile.SecondaryCurrencySymbol?.Trim();

        if (ContainsControlCharacter(primarySymbol) || ContainsControlCharacter(secondarySymbol))
        {
            errorMessage = ControlCharacterMessage;
            return false;
        }

        if (profile.Mode == MonetaryMode.Single)
        {
            if (!string.IsNullOrEmpty(secondaryCode) || !string.IsNullOrEmpty(secondarySymbol))
            {
                errorMessage = SingleWithSecondaryMessage;
                return false;
            }

            normalized = new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = primaryCode!,
                PrimaryCurrencySymbol = primarySymbol,
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            };
            return true;
        }

        if (string.IsNullOrEmpty(secondaryCode))
        {
            errorMessage = DualWithoutSecondaryMessage;
            return false;
        }

        if (IsMalformedCode(secondaryCode))
        {
            errorMessage = MalformedSecondaryCodeMessage;
            return false;
        }

        if (!Iso4217.IsSupported(secondaryCode))
        {
            errorMessage = $"El código '{secondaryCode}' no es un código ISO 4217 reconocido.";
            return false;
        }

        if (string.Equals(secondaryCode, primaryCode, StringComparison.Ordinal))
        {
            errorMessage = SameCurrencyMessage;
            return false;
        }

        if (string.IsNullOrEmpty(secondarySymbol) || secondarySymbol.Length > MaxSymbolLength)
        {
            errorMessage = SecondarySymbolMessage;
            return false;
        }

        normalized = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = primaryCode!,
            PrimaryCurrencySymbol = primarySymbol,
            SecondaryCurrencyCode = secondaryCode,
            SecondaryCurrencySymbol = secondarySymbol,
            IsConfigured = true
        };
        return true;
    }

    private static bool IsMalformedCode(string? normalizedCode)
    {
        if (normalizedCode is not { Length: 3 })
        {
            return true;
        }

        foreach (var character in normalizedCode)
        {
            if (character is < 'A' or > 'Z')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsControlCharacter(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || character is '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return false;
    }

    private static MonetaryProfileDto CreateDefaultProfile() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S",
        IsConfigured = false
    };
}

