using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class CustomerPickerViewModel : ObservableObject
{
    private readonly ISalesService _salesService;
    private CancellationTokenSource? _searchCts;

    private static readonly HashSet<string> ValidRifPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "V", "E", "J", "G", "P"
    };

    private static readonly HashSet<string> ValidPhonePrefixes = new()
    {
        "0412", "0414", "0424", "0416", "0426", "0212", "0241", "0242", "0243", "0244", "0245", "0251", "0276", "0261"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchTab))]
    [NotifyPropertyChangedFor(nameof(IsCreateTab))]
    private string _activeTab = "search"; // "search" | "create"

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private CustomerDto? _selectedCustomer;

    public bool IsSearchTab => ActiveTab == "search";
    public bool IsCreateTab => ActiveTab == "create";
    public bool CanConfirm => SelectedCustomer != null;
    public bool HasNoResults => !IsSearching && Customers.Count == 0;

    public ObservableCollection<CustomerDto> Customers { get; } = new();

    // Create form fields
    [ObservableProperty] private string _newCedula = string.Empty;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newPhone = string.Empty;
    [ObservableProperty] private string _newCreditLimitText = "0.00";

    // Dynamic Validation Indicators & Length Tracking
    [ObservableProperty] private bool _isNewCedulaValid;
    [ObservableProperty] private bool _isNewPhoneValid;

    public int NewNameLength => NewName?.Length ?? 0;
    public bool IsNewNameNearLimit => NewNameLength >= 42;

    // ── Controlled Input: Cédula / RIF (V or E + 7-8 digits) ──
    partial void OnNewCedulaChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            IsNewCedulaValid = false;
            return;
        }

        string formatted = FormatCedulaRifInput(value);
        if (formatted != value)
        {
            NewCedula = formatted;
            return;
        }

        // Valid format: Letter V/E/J/G/P + hyphen + 7 to 8 digits
        IsNewCedulaValid = Regex.IsMatch(formatted, @"^[VJEGPvjegp]-\d{7,8}$", RegexOptions.IgnoreCase);
    }

    private static string FormatCedulaRifInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        input = input.Trim().ToUpperInvariant();

        // Auto-typing assistance: if starts with a digit, prepend "V-"
        if (char.IsDigit(input[0]))
        {
            input = "V-" + input;
        }

        // Force prefix V, E, J, G, or P
        string firstChar = input.Substring(0, 1);
        if (!ValidRifPrefixes.Contains(firstChar))
        {
            return string.Empty;
        }

        // Extract digits after prefix
        string digits = Regex.Replace(input.Substring(1), @"\D", "");
        if (digits.Length > 8)
        {
            digits = digits.Substring(0, 8);
        }

        return $"{firstChar}-{digits}";
    }

    // ── Controlled Input: Phone (11 digits with operator hyphen separator) ──
    partial void OnNewPhoneChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            IsNewPhoneValid = false;
            return;
        }

        string digits = Regex.Replace(value, @"\D", "");
        if (digits.Length > 11)
        {
            digits = digits.Substring(0, 11);
        }

        // Immediate Operator Prefix Check at 4th digit:
        if (digits.Length >= 4)
        {
            string prefix4 = digits.Substring(0, 4);
            if (!ValidPhonePrefixes.Contains(prefix4))
            {
                // Revert 4th digit to stop visual advance and force operator correction
                digits = digits.Substring(0, 3);
            }
        }

        // Format with hyphen after operator prefix (e.g. 0412-1234567)
        string formatted = digits;
        if (digits.Length > 4)
        {
            formatted = $"{digits.Substring(0, 4)}-{digits.Substring(4)}";
        }

        if (formatted != value)
        {
            NewPhone = formatted;
            return;
        }

        IsNewPhoneValid = digits.Length == 11 && ValidPhonePrefixes.Contains(digits.Substring(0, 4));
    }

    // ── Controlled Input: Credit Limit (ATM / Cashier Shift Effect 0.0x) ──
    partial void OnNewCreditLimitTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0" || value == "0.00")
        {
            if (value != "0.00") NewCreditLimitText = "0.00";
            return;
        }

        // Extract digits and trim leading zeroes so typing '1' becomes '0.01'
        string digits = Regex.Replace(value, @"\D", "").TrimStart('0');

        if (string.IsNullOrEmpty(digits))
        {
            if (value != "0.00") NewCreditLimitText = "0.00";
            return;
        }

        if (long.TryParse(digits, out long cents))
        {
            decimal dollars = cents / 100.00m;
            string formatted = dollars.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            if (value != formatted)
            {
                NewCreditLimitText = formatted;
            }
        }
    }

    partial void OnNewNameChanged(string value)
    {
        OnPropertyChanged(nameof(NewNameLength));
        OnPropertyChanged(nameof(IsNewNameNearLimit));
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                var newCts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _searchCts, newCts);
                try
                {
                    oldCts?.Cancel();
                    oldCts?.Dispose();
                }
                catch (ObjectDisposedException) { }

                StartDebouncedSearch(value, newCts.Token);
            }
        }
    }

    public CustomerPickerViewModel(ISalesService salesService)
    {
        _salesService = salesService;
        Customers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoResults));
    }

    public async Task InitializeAsync()
    {
        await SearchAsync(string.Empty);
    }

    private async void StartDebouncedSearch(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
                await SearchAsync(query);
        }
        catch (OperationCanceledException) { }
    }

    public async Task SearchAsync(string query)
    {
        IsSearching = true;
        try
        {
            var (results, _) = await _salesService.GetCustomersAsync(query, page: 1, pageSize: 20, recentOnly: string.IsNullOrWhiteSpace(query));
            Customers.Clear();
            foreach (var c in results)
                Customers.Add(c);

        }
        catch { /* silently ignore search errors */ }
        finally
        {
            IsSearching = false;
        }
    }

    public void SwitchToSearch()
    {
        ErrorMessage = null;
        ActiveTab = "search";
    }

    public void SwitchToCreate()
    {
        ErrorMessage = null;
        ActiveTab = "create";
    }

    [RelayCommand]
    private async Task CreateCustomerAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(NewCedula))
        {
            ErrorMessage = "La Cédula o RIF es obligatoria.";
            return;
        }

        if (!IsNewCedulaValid)
        {
            ErrorMessage = "La Cédula o RIF debe tener el formato oficial (ej. V-12345678 con 7 u 8 dígitos).";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewName))
        {
            ErrorMessage = "El Nombre o Razón Social es obligatorio.";
            return;
        }

        if (NewName.Trim().Length > 50)
        {
            ErrorMessage = "El Nombre o Razón Social no puede exceder los 50 caracteres.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(NewPhone) && !IsNewPhoneValid)
        {
            ErrorMessage = "El teléfono debe tener 11 dígitos y una operadora válida (ej. 0412-1234567).";
            return;
        }

        decimal.TryParse(NewCreditLimitText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out decimal creditLimit);

        IsCreating = true;
        try
        {
            var created = await _salesService.CreateCustomerAsync(new CreateCustomerDto
            {
                CedulaOrRif = NewCedula.Trim(),
                Name = NewName.Trim(),
                Phone = NewPhone.Trim(),
                CreditLimitUSD = creditLimit
            });

            // 1. Refresh list with the new customer's cedula so it appears highlighted
            await SearchAsync(created.CedulaOrRif);

            // 2. Auto-select the newly created customer
            SelectedCustomer = created;

            // 3. Switch back to search tab
            ActiveTab = "search";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al crear cliente: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }
}
