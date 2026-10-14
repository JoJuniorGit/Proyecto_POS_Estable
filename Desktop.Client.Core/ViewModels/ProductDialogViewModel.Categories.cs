using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.179-T2 (S3): tarjeta "Categorías" del diálogo de producto. El árbol aplanado (con profundidad y
/// ruta) se carga al inicializar el diálogo y la multi-selección viaja SIEMPRE como lista completa
/// (marcar/desmarcar = reemplazo) dentro de <see cref="ProductDialogViewModel.ResultProduct"/>.
/// El cliente de categorías lo adjunta WpfDialogService antes de mostrar el diálogo, porque el
/// ViewModel se construye fuera del contenedor DI (InventoryViewModel.Operations).
/// </summary>
public partial class ProductDialogViewModel
{
    private ICategoryService? _categoryClient;
    private bool _categoriesLoadStarted;
    private bool _categoriesLoaded;
    private bool _isLoadingCategories;
    private string _categoriesErrorMessage = string.Empty;

    /// <summary>
    /// Cliente del árbol de categorías. Asignarlo dispara la carga inicial; al no cargarse, el
    /// guardado deja <c>CategoryIds</c> en null (update = "no tocar") en lugar de pisar el servidor.
    /// </summary>
    public ICategoryService? CategoryClient
    {
        get => _categoryClient;
        set
        {
            if (ReferenceEquals(_categoryClient, value))
            {
                return;
            }

            _categoryClient = value;
            OnPropertyChanged();
            TryStartCategoriesLoad();
        }
    }

    public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();

    /// <summary>Tarea de la carga inicial del árbol (awaitable en pruebas).</summary>
    public Task CategoriesLoadTask { get; private set; } = Task.CompletedTask;

    public bool IsLoadingCategories
    {
        get => _isLoadingCategories;
        private set
        {
            if (_isLoadingCategories != value)
            {
                _isLoadingCategories = value;
                OnPropertyChanged();
            }
        }
    }

    public string CategoriesErrorMessage
    {
        get => _categoriesErrorMessage;
        private set
        {
            if (_categoriesErrorMessage != value)
            {
                _categoriesErrorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCategoriesError));
            }
        }
    }

    public bool HasCategoriesError => !string.IsNullOrEmpty(_categoriesErrorMessage);

    public int SelectedCategoriesCount => CategoryOptions.Count(o => o.IsSelected);

    /// <summary>RBAC: los roles de consulta no pueden alterar la asignación de categorías.</summary>
    public bool CanEditCategories => UserSession?.CanMutateCatalog ?? true;

    // El ctor principal dispara LoadMetadataAsync al final; ese cambio marca el arranque de la carga
    // del árbol sin acoplarse al orden del cuerpo del constructor.
    partial void OnIsLoadingMetadataChanged(bool value) => TryStartCategoriesLoad();

    private void TryStartCategoriesLoad()
    {
        if (_categoriesLoadStarted || _categoryClient is null)
        {
            return;
        }

        _categoriesLoadStarted = true;
        CategoriesLoadTask = LoadCategoriesAsync(preserveSelection: true);
    }

    /// <summary>
    /// Carga el árbol aplanado. Con <paramref name="preserveSelection"/> conserva los Ids marcados
    /// (preselección inicial en edición incluida); la lista completa resultante se sincroniza en
    /// <c>ResultProduct.CategoryIds</c> (vacía = quitar todas).
    /// </summary>
    public async Task LoadCategoriesAsync(bool preserveSelection = true)
    {
        var client = _categoryClient;
        if (client is null)
        {
            return;
        }

        IsLoadingCategories = true;
        CategoriesErrorMessage = string.Empty;
        try
        {
            var selectedIds = preserveSelection ? CurrentSelectedIds() : new HashSet<int>();
            var categories = await client.GetCategoriesAsync();
            var options = CategoryOption.BuildFlat(categories);

            void Apply()
            {
                CategoryOptions.Clear();
                foreach (var option in options)
                {
                    option.SelectionChanged = OnCategoryOptionSelectionChanged;
                    CategoryOptions.Add(option);
                    option.IsSelected = selectedIds.Contains(option.Id);
                }

                _categoriesLoaded = true;
                SyncResultCategoryIds();
                NotifySelectedCategoriesCount();
                IsLoadingCategories = false;
            }

            if (_dispatcherInvoker.CheckAccess())
            {
                Apply();
            }
            else
            {
                _dispatcherInvoker.Invoke(Apply);
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError(
                $"Fallo al cargar las categorías del producto: {ex.Message}",
                nameof(ProductDialogViewModel));

            void Fail()
            {
                CategoriesErrorMessage = "No se pudieron cargar las categorías. Use 'Gestionar categorías' para reintentar.";
                IsLoadingCategories = false;
            }

            if (_dispatcherInvoker.CheckAccess())
            {
                Fail();
            }
            else
            {
                _dispatcherInvoker.Invoke(Fail);
            }
        }
    }

    [RelayCommand]
    private async Task ManageCategoriesAsync()
    {
        var client = _categoryClient;
        if (client is null || _dialogService is null)
        {
            return;
        }

        var managementVm = new CategoryManagementDialogViewModel(client, _dialogService);
        if (_dialogService.ShowCategoryManagementDialog(managementVm))
        {
            // El árbol pudo cambiar (crear/renombrar/mover/borrar): refrescar preservando la
            // selección viva por Id; los Ids borrados simplemente desaparecen.
            await LoadCategoriesAsync(preserveSelection: true);
        }
    }

    private HashSet<int> CurrentSelectedIds()
    {
        var ids = CategoryOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();

        // Primera carga en edición: pre-seleccionar las categorías ya asignadas al producto.
        if (!_categoriesLoaded && _initialProduct?.Categories is { Count: > 0 } assigned)
        {
            ids.UnionWith(assigned.Select(c => c.Id));
        }

        return ids;
    }

    private void OnCategoryOptionSelectionChanged(CategoryOption option)
    {
        NotifySelectedCategoriesCount();
        SyncResultCategoryIds();
    }

    private void NotifySelectedCategoriesCount() => OnPropertyChanged(nameof(SelectedCategoriesCount));

    private void SyncResultCategoryIds()
    {
        // Ante una carga fallida no se toca: null preserva la asignación existente en el update.
        if (!_categoriesLoaded)
        {
            return;
        }

        ResultProduct.CategoryIds = CategoryOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList();
    }
}
