using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Comisiones del adelanto de efectivo por canal. El backend exige Admin para escribir; Manager
/// puede consultar los valores pero la UI y el comando quedan bloqueados.
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty] private string _transferCommissionText = "0";
    [ObservableProperty] private string _cashCommissionText = "0";
    [ObservableProperty] private bool _isCashAdvanceCommissionSaving;
    [ObservableProperty] private string _cashAdvanceCommissionStatus = string.Empty;

    public bool CanEditCashAdvanceCommission => UserSession == null || UserSession.CanMutateSettings;

    public bool CanSaveCashAdvanceCommission => CanEditCashAdvanceCommission && !IsCashAdvanceCommissionSaving;

    public bool HasCashAdvanceCommissionStatus => !string.IsNullOrWhiteSpace(CashAdvanceCommissionStatus);

    partial void OnIsCashAdvanceCommissionSavingChanged(bool value)
        => OnPropertyChanged(nameof(CanSaveCashAdvanceCommission));

    partial void OnCashAdvanceCommissionStatusChanged(string value)
        => OnPropertyChanged(nameof(HasCashAdvanceCommissionStatus));

    public async Task LoadCashAdvanceCommissionAsync()
    {
        var settings = await _settingsService.GetCashAdvanceCommissionAsync()
            ?? new CashAdvanceCommissionSettingsDto();
        TransferCommissionText = FormatCommission(settings.TransferCommissionPct);
        CashCommissionText = FormatCommission(settings.CashCommissionPct);

        CashAdvanceCommissionStatus = settings.TransferCommissionPct <= 0m || settings.CashCommissionPct <= 0m
            ? "Comisiones no configuradas. Guarde ambos valores para habilitar el adelanto de efectivo."
            : string.Empty;
    }

    private static string FormatCommission(decimal value)
    {
        // Respeta el formato numérico activo (Venezolano/Internacional) y quita los ceros
        // decimales sobrantes para que el campo muestre exactamente lo que el usuario ve.
        var formatted = CurrencyDisplay.Number(value, 4);
        return formatted.TrimEnd('0').TrimEnd('.', ',');
    }

    [RelayCommand]
    private async Task SaveCashAdvanceCommissionAsync()
    {
        if (!CanEditCashAdvanceCommission || IsCashAdvanceCommissionSaving)
        {
            return;
        }

        if (!FlexibleDecimalParser.TryParse(TransferCommissionText, out var transfer)
            || transfer <= 0m
            || transfer > 100m)
        {
            CashAdvanceCommissionStatus = "La comisión de transferencia debe ser mayor a cero y menor o igual a 100.";
            return;
        }

        if (!FlexibleDecimalParser.TryParse(CashCommissionText, out var cash)
            || cash <= 0m
            || cash > 100m)
        {
            CashAdvanceCommissionStatus = "La comisión de efectivo debe ser mayor a cero y menor o igual a 100.";
            return;
        }

        IsCashAdvanceCommissionSaving = true;
        CashAdvanceCommissionStatus = string.Empty;
        try
        {
            await _settingsService.SetCashAdvanceCommissionAsync(new CashAdvanceCommissionSettingsDto
            {
                TransferCommissionPct = transfer,
                CashCommissionPct = cash
            });

            TransferCommissionText = FormatCommission(transfer);
            CashCommissionText = FormatCommission(cash);
            CashAdvanceCommissionStatus = "Comisiones guardadas correctamente.";
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al guardar las comisiones de adelanto: {ex.Message}",
                nameof(SettingsViewModel));
            CashAdvanceCommissionStatus = $"No se pudieron guardar las comisiones: {ex.Message}";
            _dialogService?.ShowError(
                "Comisiones de adelanto",
                "No se pudieron guardar las comisiones. Intente nuevamente.");
        }
        finally
        {
            IsCashAdvanceCommissionSaving = false;
        }
    }
}
