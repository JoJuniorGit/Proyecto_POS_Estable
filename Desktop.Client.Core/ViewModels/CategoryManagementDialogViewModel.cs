using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.179-T2 (S3): fila aplanada del árbol de categorías (Id, profundidad y ruta legible)
/// reutilizada por la tarjeta "Categorías" del producto y por el diálogo de gestión.
/// <see cref="IsSelected"/> es la marca de multi-selección del producto; <see cref="SelectionChanged"/>
/// notifica al contenedor para recalcular la lista completa de ids (semántica de reemplazo).
/// </summary>
public partial class CategoryOption : ObservableObject
{
    /// <summary>Sentinel "Todas" del filtro de inventario (Id 0 = sin filtro).</summary>
    public static CategoryOption AllFilter { get; } = new(0, "Todas", null, 0, "Todas");

    public int Id { get; }
    public string Name { get; }
    public int? ParentCategoryId { get; }
    public int Depth { get; }
    public string Path { get; }

    /// <summary>Ancho de sangría en píxeles (~16px por nivel) para el aplanado visual del árbol.</summary>
    public double IndentWidth => Depth * 16d;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Callback de cambio de selección; lo asigna el contenedor dueño de la lista.</summary>
    public Action<CategoryOption>? SelectionChanged { get; set; }

    public CategoryOption(int id, string name, int? parentCategoryId, int depth, string path)
    {
        Id = id;
        Name = name;
        ParentCategoryId = parentCategoryId;
        Depth = depth;
        Path = path;
    }

    /// <summary>Aplana el listado del servidor en orden de ruta (padres antes que hijas).</summary>
    public static List<CategoryOption> BuildFlat(IEnumerable<CategoryDto> categories) =>
        categories
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .Select(c => new CategoryOption(c.Id, c.Name, c.ParentCategoryId, c.Depth, c.Path))
            .ToList();

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);
}

/// <summary>
/// 8.179-T2 (S2/S3): gestión completa del árbol de categorías (crear con padre opcional, renombrar,
/// mover y borrar con confirmación). Cada mutación se persiste al instante y los errores del
/// servidor (409 duplicado/ciclo/borrado bloqueado) se muestran sin romper el diálogo. El cierre
/// devuelve si hubo cambios para que el diálogo de producto refresque su tarjeta.
/// </summary>
public partial class CategoryManagementDialogViewModel : ObservableObject, IDisposable
{
    private readonly ICategoryService _categoryService;
    private readonly IDialogService? _dialogService;
    private readonly CategoryOption _noParentOption = new(0, "Sin categoría padre", null, 0, "Sin categoría padre");
    private List<CategoryOption> _allOptions = new();

    public Action<bool>? RequestClose;

    /// <summary>true cuando al menos una mutación se persistió durante la sesión del diálogo.</summary>
    public bool HasChanges { get; private set; }

    /// <summary>Carga inicial del árbol (awaitable en pruebas).</summary>
    public Task LoadTask { get; }

    public ObservableCollection<CategoryOption> Categories { get; } = new();

    /// <summary>Opciones del selector de padre: "Sin categoría padre" + árbol sin el subárbol propio.</summary>
    public ObservableCollection<CategoryOption> ParentOptions { get; } = new();

    [ObservableProperty]
    private CategoryOption? _selectedCategory;

    [ObservableProperty]
    private CategoryOption? _selectedParent;

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool CanDeleteSelected => SelectedCategory is { Id: > 0 };

    public string SaveButtonText => IsEditing ? "Guardar cambios" : "Crear categoría";

    public CategoryManagementDialogViewModel(ICategoryService categoryService, IDialogService? dialogService = null)
    {
        ArgumentNullException.ThrowIfNull(categoryService);
        _categoryService = categoryService;
        _dialogService = dialogService;
        LoadTask = LoadAsync();
    }

    /// <summary>Recarga el árbol completo; opcionalmente reselecciona por Id (post crear/renombrar).</summary>
    public async Task LoadAsync(int? selectId = null)
    {
        IsBusy = true;
        try
        {
            var categories = await _categoryService.GetCategoriesAsync();
            _allOptions = CategoryOption.BuildFlat(categories);

            Categories.Clear();
            foreach (var option in _allOptions)
            {
                Categories.Add(option);
            }

            if (selectId.HasValue)
            {
                SelectedCategory = Categories.FirstOrDefault(c => c.Id == selectId.Value);
            }
            else if (SelectedCategory is { Id: > 0 } current)
            {
                SelectedCategory = Categories.FirstOrDefault(c => c.Id == current.Id);
            }

            RebuildParentOptions();
            if (Categories.Count == 0)
            {
                StatusMessage = "No hay categorías registradas; cree la primera.";
            }
            else
            {
                StatusMessage = $"{Categories.Count} categoría(s) en el catálogo.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudieron cargar las categorías: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginCreate()
    {
        IsEditing = false;
        CategoryName = string.Empty;
        SelectedCategory = null;
        SelectedParent = _noParentOption;
        ErrorMessage = string.Empty;
        StatusMessage = "Nueva categoría: indique nombre y padre opcional.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = CategoryName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "El nombre de la categoría es obligatorio.";
            return;
        }

        if (name.Length > 60)
        {
            ErrorMessage = "El nombre de la categoría no puede exceder los 60 caracteres.";
            return;
        }

        var parentId = SelectedParent is { Id: > 0 } parent ? parent.Id : (int?)null;

        if (IsEditing && SelectedCategory is { Id: > 0 } target &&
            parentId is int newParentId && DescendantIds(target.Id).Contains(newParentId))
        {
            ErrorMessage = "No se puede mover una categoría dentro de sí misma o de una subcategoría.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            if (IsEditing && SelectedCategory is { Id: > 0 } existing)
            {
                await _categoryService.UpdateAsync(existing.Id, new UpdateCategoryRequestDto
                {
                    Name = name,
                    ParentCategoryId = parentId
                });
                HasChanges = true;
                StatusMessage = $"Categoría '{name}' actualizada.";
                await LoadAsync(existing.Id);
            }
            else
            {
                var created = await _categoryService.CreateAsync(new CreateCategoryRequestDto
                {
                    Name = name,
                    ParentCategoryId = parentId
                });
                HasChanges = true;
                StatusMessage = $"Categoría '{name}' creada.";
                await LoadAsync(created.Id);
            }
        }
        catch (Exception ex)
        {
            // 409 duplicado/ciclo y 400 del servidor llegan legibles vía ApiErrorParser.
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedCategory is not { Id: > 0 } target)
        {
            return;
        }

        if (_dialogService is not null &&
            !_dialogService.ShowConfirm(
                "Eliminar categoría",
                $"¿Eliminar la categoría '{target.Path}'?\n\nSolo es posible si no tiene subcategorías ni productos asignados."))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _categoryService.DeleteAsync(target.Id);
            HasChanges = true;
            StatusMessage = $"Categoría '{target.Name}' eliminada.";
            SelectedCategory = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // Borrado bloqueado (409 legible): el diálogo sigue operativo con el árbol cargado.
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(HasChanges);

    public void Dispose() => RequestClose = null;

    partial void OnSelectedCategoryChanged(CategoryOption? value)
    {
        if (value is { Id: > 0 })
        {
            IsEditing = true;
            CategoryName = value.Name;
            ErrorMessage = string.Empty;

            // 8.179-T2 (fix verificación B1): el selector de padre se re-ancla SIEMPRE al padre real
            // de la categoría seleccionada. Antes, el combo conservaba la selección previa y
            // renombrar una hija podía moverla en silencio (a raíz o bajo otro padre).
            SelectedParent = _allOptions.FirstOrDefault(o => o.Id == value.ParentCategoryId) ?? _noParentOption;
        }

        OnPropertyChanged(nameof(CanDeleteSelected));
        RebuildParentOptions();
    }

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(SaveButtonText));

    private void RebuildParentOptions()
    {
        var previousParentId = SelectedParent?.Id ?? SelectedCategory?.ParentCategoryId ?? 0;
        var forbidden = SelectedCategory is { Id: > 0 } selected
            ? DescendantIds(selected.Id)
            : new HashSet<int>();

        ParentOptions.Clear();
        ParentOptions.Add(_noParentOption);
        foreach (var option in _allOptions.Where(o => !forbidden.Contains(o.Id)))
        {
            ParentOptions.Add(option);
        }

        SelectedParent = ParentOptions.FirstOrDefault(o => o.Id == previousParentId) ?? _noParentOption;
    }

    private HashSet<int> DescendantIds(int rootId)
    {
        var result = new HashSet<int> { rootId };
        var queue = new Queue<int>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in _allOptions.Where(o => o.ParentCategoryId == current))
            {
                if (result.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return result;
    }
}
