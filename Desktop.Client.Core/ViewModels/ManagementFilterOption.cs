using Core.Entities;

namespace Desktop.Client.ViewModels;

/// <summary>Opción de rol con etiqueta de presentación; conserva el enum para el contrato existente.</summary>
public sealed record UserRoleOption(UserRole Role, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Filtro rápido de la pestaña de usuarios. <see cref="Role"/> null representa "Todos".</summary>
public sealed record UserRoleFilterOption(string Label, UserRole? Role)
{
    public override string ToString() => Label;
}

/// <summary>Filtro rápido de estado de clientes. <see cref="IsActive"/> null representa "Todos".</summary>
public sealed record CustomerStatusFilterOption(string Label, bool? IsActive)
{
    public override string ToString() => Label;
}
