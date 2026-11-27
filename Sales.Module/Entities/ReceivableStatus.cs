namespace Sales.Module.Entities;

/// <summary>
/// Estado de la obligacion CxC (Paso 11, S3). Persistido como int; valores estables
/// y nuevos estados se anexan al final sin renumerar los ya almacenados.
/// </summary>
public enum ReceivableStatus
{
    Vigente = 0,
    Parcial = 1,
    Vencida = 2,
    Pagada = 3,
    Anulada = 4
}
