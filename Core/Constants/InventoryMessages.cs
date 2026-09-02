namespace Core.Constants;

/// <summary>
/// Mensajes centralizados de validación, error y tooltips para operaciones de inventario.
/// </summary>
public static class InventoryMessages
{
    public const string GroupIndividualStockAdjustmentBlocked =
        "No se pueden realizar ajustes manuales en un producto agrupador con inventario individual. Su stock es la suma consolidada de sus variantes; realice el ajuste en cada presentación individual.";

    public const string VariantSharedStockAdjustmentBlocked =
        "No se pueden realizar ajustes manuales en una variante con inventario compartido. Realice el ajuste directamente en el producto padre.";

    public const string CashAdvanceStockAdjustmentBlocked =
        "El producto es un servicio de adelanto de efectivo y no maneja inventario físico.";

    public const string DeletedProductStockAdjustmentBlocked =
        "El producto está eliminado o archivado y no permite ajustes de inventario.";

    public const string ConversionFactorOutOfRange =
        "El factor de conversión debe ser un valor positivo entre 0.0001 y 1,000,000.";

    public const string UnauthorizedAdjustment =
        "No tiene permisos para realizar ajustes manuales de inventario.";

    public const string TooltipGroupIndividualBlocked =
        "No editable: El stock es la suma consolidada de sus presentaciones. Realice el ajuste en cada variante.";

    public const string TooltipVariantSharedBlocked =
        "No editable: Esta variante comparte el inventario centralizado del padre. Realice el ajuste en el producto padre.";

    public const string TooltipCashAdvance =
        "No aplica: Este producto es un servicio y no maneja inventario físico.";

    public const string TooltipDeleted =
        "No disponible: El producto está eliminado / archivado.";

    public const string TooltipAdjustStockAllowed =
        "Ajustar Inventario";
}
