namespace Core.Configuration;

public class SystemSettingsOptions
{
    public const string SectionName = "SystemSettings";

    public string MinimumClientVersion { get; set; } = "1.0.0";
    public string ServerVersion { get; set; } = "1.0.0";
    public string UpdateServerUrl { get; set; } = "http://localhost:5000/updates/";
    public string AdminSeedUsername { get; set; } = "Admin";
    public string AdminSeedPassword { get; set; } = "Admin123!";
    public string BusinessName { get; set; } = "Mi Negocio POS";
}
