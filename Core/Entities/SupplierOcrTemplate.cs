using Core.DTOs;

namespace Core.Entities;

/// <summary>
/// Plantilla de configuración del escáner OCR persistida por proveedor (preprocesamiento de imagen,
/// región de interés ROI, palabras ancla, orden de columnas, reglas matemáticas y limpieza de texto).
/// </summary>
public class SupplierOcrTemplate : BaseEntity
{
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public double RotationDegrees { get; set; }
    public double ContrastFactor { get; set; } = 1.0d;
    public double BrightnessDelta { get; set; }
    public int BinarizationThreshold { get; set; }
    public double? RoiX { get; set; }
    public double? RoiY { get; set; }
    public double? RoiWidth { get; set; }
    public double? RoiHeight { get; set; }
    public string? HeaderAnchorKeywords { get; set; }
    public string? FooterAnchorKeywords { get; set; }
    public int? ProductColumnPosition { get; set; }
    public int? QuantityColumnPosition { get; set; }
    public int? UnitCostColumnPosition { get; set; }
    public int? SupplierCodeColumnPosition { get; set; }
    public OcrCostAmountRule CostAmountRule { get; set; } = OcrCostAmountRule.UnitCost;
    public OcrDecimalSeparatorRule DecimalSeparator { get; set; } = OcrDecimalSeparatorRule.Auto;
    public string? CharacterReplacementsJson { get; set; }
    public string? FactorExtractionPattern { get; set; }
}
