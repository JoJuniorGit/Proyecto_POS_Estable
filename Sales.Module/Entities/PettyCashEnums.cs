namespace Sales.Module.Entities;

/// <summary>
/// 8.188-T1 (Paso 13, S1/S6): tipificacion del egreso de caja chica. Enum persistido
/// append-only: los ids ya guardados en la base no se renumeran; las categorias nuevas
/// se anexan al final.
/// </summary>
public enum PettyCashCategory
{
    InsumosLimpieza = 0,
    FletesAcarreo = 1,
    MantenimientoReparaciones = 2,
    ServiciosPublicos = 3,
    Papeleria = 4,
    Varios = 5
}

/// <summary>
/// Moneda fisica que sale de la gaveta (S1): efectivo en moneda local (Bs.S) o en
/// moneda dura (USD). Determina de que gaveta se deduce el gasto (S2).
/// </summary>
public enum PettyCashCurrency
{
    BsS = 0,
    Usd = 1
}

/// <summary>
/// Ciclo de vida del gasto (S5): la anulacion es logica (nunca se borra fisicamente) y
/// genera un movimiento compensatorio. Solo los gastos <see cref="Active"/> totalizan el
/// renglon OPEX informativo del cierre Z.
/// </summary>
public enum PettyCashStatus
{
    Active = 0,
    Cancelled = 1
}
