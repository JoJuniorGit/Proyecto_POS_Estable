using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Desktop.Client.ViewModels;

public sealed partial class SupplierInvoiceColumnMappingItem : ObservableObject
{
    public string FieldKey { get; }
    public string Label { get; }
    public bool IsRequired { get; }
    public ObservableCollection<string> AvailableColumns { get; }

    [ObservableProperty]
    private string _selectedColumnName = SupplierInvoiceViewModel.UnassignedColumn;

    public SupplierInvoiceColumnMappingItem(
        string fieldKey,
        string label,
        bool isRequired,
        IEnumerable<string> headers)
    {
        FieldKey = fieldKey;
        Label = label;
        IsRequired = isRequired;
        AvailableColumns = new ObservableCollection<string> { SupplierInvoiceViewModel.UnassignedColumn };
        foreach (var header in headers)
        {
            AvailableColumns.Add(header);
        }
    }
}

public partial class SupplierInvoiceViewModel
{
    public const string UnassignedColumn = "(Sin asignar)";

    private List<string> _fileHeaders = [];

    public IReadOnlyList<string> FileHeaders => _fileHeaders;
    public bool HasFileHeaders => _fileHeaders.Count > 0;
    public bool HasMappings => ColumnMappings.Count > 0;

    public void SetFileHeaders(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _fileHeaders = headers
            .Where(header => !string.IsNullOrWhiteSpace(header))
            .Select(header => header.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var mapping in ColumnMappings)
        {
            mapping.PropertyChanged -= OnColumnMappingChanged;
        }

        ColumnMappings.Clear();
        ColumnMappings.Add(new SupplierInvoiceColumnMappingItem("Barcode", "Código de barras", false, _fileHeaders));
        ColumnMappings.Add(new SupplierInvoiceColumnMappingItem("SupplierCode", "Código del proveedor", false, _fileHeaders));
        ColumnMappings.Add(new SupplierInvoiceColumnMappingItem("Name", "Nombre del producto", true, _fileHeaders));
        ColumnMappings.Add(new SupplierInvoiceColumnMappingItem("Quantity", "Cantidad", true, _fileHeaders));
        ColumnMappings.Add(new SupplierInvoiceColumnMappingItem("UnitCost", "Costo unitario USD", true, _fileHeaders));

        foreach (var mapping in ColumnMappings)
        {
            mapping.PropertyChanged += OnColumnMappingChanged;
        }

        ApplyCurrentColumnMapping();
        OnPropertyChanged(nameof(FileHeaders));
        OnPropertyChanged(nameof(HasFileHeaders));
        OnPropertyChanged(nameof(HasMappings));
        RefreshCommandStates();
    }

    private void ApplyCurrentColumnMapping()
    {
        if (_fileHeaders.Count == 0 || ColumnMappings.Count == 0)
        {
            return;
        }

        var savedMapping = SelectedSupplier?.ColumnMapping;
        if (savedMapping is null)
        {
            ApplySuggestedMapping();
            return;
        }

        var missingColumns = new List<string>();
        foreach (var mapping in ColumnMappings)
        {
            var savedColumn = GetSavedColumn(savedMapping, mapping.FieldKey);
            if (string.IsNullOrWhiteSpace(savedColumn))
            {
                mapping.SelectedColumnName = UnassignedColumn;
                continue;
            }

            var availableColumn = _fileHeaders.FirstOrDefault(header =>
                string.Equals(header, savedColumn, StringComparison.OrdinalIgnoreCase));
            if (availableColumn is null)
            {
                mapping.SelectedColumnName = UnassignedColumn;
                missingColumns.Add(savedColumn);
                continue;
            }

            mapping.SelectedColumnName = availableColumn;
        }

        if (missingColumns.Count > 0)
        {
            StatusMessage = $"La plantilla guardada contiene columnas que no aparecen en el archivo: {string.Join(", ", missingColumns)}. Revise la asignación.";
        }
    }

    private void ApplySuggestedMapping()
    {
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in ColumnMappings)
        {
            var candidate = _fileHeaders.FirstOrDefault(header =>
                !assigned.Contains(header) && MatchesField(header, mapping.FieldKey));
            if (candidate is null)
            {
                mapping.SelectedColumnName = UnassignedColumn;
                continue;
            }

            mapping.SelectedColumnName = candidate;
            assigned.Add(candidate);
        }
    }

    private SupplierColumnMappingDto BuildColumnMapping()
    {
        var barcode = GetSelectedColumn("Barcode");
        var supplierCode = GetSelectedColumn("SupplierCode");
        var name = GetSelectedColumn("Name");
        var quantity = GetSelectedColumn("Quantity");
        var unitCost = GetSelectedColumn("UnitCost");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(quantity) || string.IsNullOrWhiteSpace(unitCost))
        {
            throw new InvalidOperationException("Asigne las columnas obligatorias: nombre, cantidad y costo unitario.");
        }

        return new SupplierColumnMappingDto(barcode, supplierCode, name, quantity, unitCost);
    }

    private bool HasValidColumnMapping()
    {
        var selectedMappings = ColumnMappings
            .Where(mapping => !string.Equals(mapping.SelectedColumnName, UnassignedColumn, StringComparison.Ordinal))
            .Select(mapping => mapping.SelectedColumnName)
            .ToList();
        var requiredMappingsPresent = ColumnMappings
            .Where(mapping => mapping.IsRequired)
            .All(mapping => !string.Equals(mapping.SelectedColumnName, UnassignedColumn, StringComparison.Ordinal));

        return requiredMappingsPresent &&
               selectedMappings.Count == selectedMappings.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private string? GetSelectedColumn(string fieldKey)
    {
        var selectedColumn = ColumnMappings.FirstOrDefault(mapping => mapping.FieldKey == fieldKey)?.SelectedColumnName;
        return string.IsNullOrWhiteSpace(selectedColumn) || selectedColumn == UnassignedColumn
            ? null
            : selectedColumn;
    }

    private static string? GetSavedColumn(SupplierColumnMappingDto mapping, string fieldKey) => fieldKey switch
    {
        "Barcode" => mapping.BarcodeColumnName,
        "SupplierCode" => mapping.SupplierCodeColumnName,
        "Name" => mapping.NameColumnName,
        "Quantity" => mapping.QuantityColumnName,
        "UnitCost" => mapping.UnitCostColumnName,
        _ => null
    };

    private static bool MatchesField(string header, string fieldKey)
    {
        var normalized = new string(header
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        return fieldKey switch
        {
            "Barcode" => normalized.Contains("barcode", StringComparison.Ordinal) ||
                         normalized.Contains("ean", StringComparison.Ordinal) ||
                         normalized.Contains("upc", StringComparison.Ordinal) ||
                         normalized.Contains("skucode", StringComparison.Ordinal) ||
                         normalized is "sku" or "codigo" or "codigobarras",
            "SupplierCode" => normalized.Contains("suppliercode", StringComparison.Ordinal) ||
                               normalized.Contains("vendorcode", StringComparison.Ordinal) ||
                               normalized.Contains("codigoproveedor", StringComparison.Ordinal) ||
                               normalized.Contains("productcode", StringComparison.Ordinal),
            "Name" => normalized.Contains("name", StringComparison.Ordinal) ||
                      normalized.Contains("nombre", StringComparison.Ordinal) ||
                      normalized.Contains("producto", StringComparison.Ordinal) ||
                      normalized.Contains("articulo", StringComparison.Ordinal) ||
                      normalized is "description" or "descripcion" or "item",
            "Quantity" => normalized.Contains("quantity", StringComparison.Ordinal) ||
                          normalized.Contains("cantidad", StringComparison.Ordinal) ||
                          normalized is "qty" or "units" or "unidades",
            "UnitCost" => normalized.Contains("unitcost", StringComparison.Ordinal) ||
                          normalized.Contains("costprice", StringComparison.Ordinal) ||
                          normalized.Contains("costo", StringComparison.Ordinal) ||
                          normalized.Contains("cost", StringComparison.Ordinal) ||
                          normalized.Contains("price", StringComparison.Ordinal) ||
                          normalized.Contains("precio", StringComparison.Ordinal),
            _ => false
        };
    }

    private void OnColumnMappingChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SupplierInvoiceColumnMappingItem.SelectedColumnName))
        {
            OnPropertyChanged(nameof(CanStageInvoice));
            StageInvoiceCommand.NotifyCanExecuteChanged();
        }
    }
}
