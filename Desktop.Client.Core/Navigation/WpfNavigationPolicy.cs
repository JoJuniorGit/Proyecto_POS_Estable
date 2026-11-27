using System;
using System.Collections.Generic;
using System.Linq;
using Core.Entities;

namespace Desktop.Client.Navigation;

/// <summary>
/// Single source of truth for the WPF sidebar and navigation guards. The matrix mirrors the
/// web role views plus the WPF-only inventory surfaces (import, supplier invoices and Kardex).
/// Keeping it pure makes the role policy testable without constructing the shell view model.
/// </summary>
public enum WpfNavigationCategory
{
    Principal,
    Operations,
    Inventory,
    Administration
}

public static class WpfNavigationPolicy
{
    public const string Pos = "pos";
    public const string Pending = "pending";
    public const string Pickups = "pickups";
    public const string Credit = "credit";
    public const string Catalog = "catalog";
    public const string History = "history";
    public const string Profitability = "profitability";
    public const string Register = "register";
    public const string Closing = "closing";
    public const string StockCount = "stock-count";
    public const string Kardex = "kardex";
    public const string Import = "import";
    public const string SupplierInvoices = "supplier-invoices";
    public const string Settings = "settings";
    public const string Exchange = "exchange";
    public const string Users = "users";

    private static readonly string[] AllViews =
    {
        Pos, Pending, Pickups, Credit, Catalog, History, Profitability, Register, Closing,
        StockCount, Kardex, Import, SupplierInvoices, Settings, Exchange, Users
    };

    private static readonly IReadOnlyDictionary<UserRole, HashSet<string>> ViewsByRole =
        new Dictionary<UserRole, HashSet<string>>
        {
            [UserRole.Admin] = CreateSet(AllViews),
            [UserRole.Manager] = CreateSet(AllViews.Where(view => view != Users)),
            [UserRole.Cashier] = CreateSet(new[]
            {
                Pos, Pending, Pickups, Credit, Catalog, History, Register, Closing, StockCount
            }),
            [UserRole.Driver] = CreateSet(new[] { Pickups, History })
        };

    private static readonly IReadOnlyDictionary<WpfNavigationCategory, string[]> ViewsByCategory =
        new Dictionary<WpfNavigationCategory, string[]>
        {
            [WpfNavigationCategory.Principal] = new[] { Pos, Register, History },
            [WpfNavigationCategory.Operations] = new[] { Pending, Pickups, Credit, Closing },
            [WpfNavigationCategory.Inventory] = new[]
            {
                Catalog, StockCount, Kardex, Import, SupplierInvoices
            },
            [WpfNavigationCategory.Administration] = new[] { Users, Settings, Exchange, Profitability }
        };

    public static bool CanAccess(UserRole? role, string? view)
    {
        if (role is null || string.IsNullOrWhiteSpace(view))
        {
            return false;
        }

        return ViewsByRole.TryGetValue(role.Value, out var allowed) && allowed.Contains(view);
    }

    public static bool CanAccessCategory(UserRole? role, WpfNavigationCategory category)
    {
        if (role is null || !ViewsByCategory.TryGetValue(category, out var views))
        {
            return false;
        }

        return views.Any(view => CanAccess(role, view));
    }

    /// <summary>
    /// First view a role may open after login. Cashiers and supervisors land on POS; Drivers land
    /// on pending pickups because every sales/cash view is intentionally blocked for them.
    /// </summary>
    public static string? GetFallbackView(UserRole? role)
    {
        if (role is null)
        {
            return null;
        }

        return role == UserRole.Driver ? Pickups : Pos;
    }

    private static HashSet<string> CreateSet(IEnumerable<string> views)
        => new(views, StringComparer.OrdinalIgnoreCase);
}
