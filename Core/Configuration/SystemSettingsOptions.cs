namespace Core.Configuration;

public class SystemSettingsOptions
{
    public const string SectionName = "SystemSettings";

    public string MinimumClientVersion { get; set; } = "1.0.0";
    public string ServerVersion { get; set; } = "1.0.0";
    public string UpdateServerUrl { get; set; } = "http://localhost:5000/updates/";
    public string AdminSeedUsername { get; set; } = "Admin";
    // 8.7-B9: sin contraseña por defecto — el seed exige configurarla explícitamente.
    public string AdminSeedPassword { get; set; } = string.Empty;
    public string BusinessName { get; set; } = "Mi Negocio POS";
}
