namespace Core.Constants;

public static class SettingKeys
{
    public const string AllowNegativeStock = "Inventory.AllowNegativeStock";
    public const string CashAdvanceTransferCommissionPct = "CashAdvance.TransferCommissionPct";
    public const string CashAdvanceCashCommissionPct = "CashAdvance.CashCommissionPct";

    // 8.184-T1 (S1): políticas globales de precios y redondeo. Storage: enum por NOMBRE
    // (Enum.TryParse + Enum.IsDefined) y bool ("True"/"False"); el lector resuelve defaults
    // ExactMath / ExactMath / true cuando la clave falta o es ilegible.
    public static class Pricing
    {
        public const string DefaultUsdRoundingRule = "Pricing.DefaultUsdRoundingRule";
        public const string DefaultCashBsRoundingRule = "Pricing.DefaultCashBsRoundingRule";
        public const string DefaultEnforceMinimumMarginFloor = "Pricing.DefaultEnforceMinimumMarginFloor";
    }
}
