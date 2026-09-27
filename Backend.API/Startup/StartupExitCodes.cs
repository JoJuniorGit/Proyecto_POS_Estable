namespace Backend.API.Startup;

/// <summary>
/// Códigos de salida del proceso backend. El instalador mapea el 2 a "no reintentar"
/// (NSSM AppExit 2 Exit): un error de configuración no se arregla reiniciando.
/// </summary>
public static class StartupExitCodes
{
    public const int OperationalFailure = 1;  // transitorio/operativo: reintentable
    public const int ConfigurationError = 2;  // configuración: no reintentable
}
