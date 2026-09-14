using System;
using System.IO;
using System.Text.Json;

namespace Desktop.Client.Services;

public class SaleRecoverySnapshot
{
    public int SaleId { get; set; }
    public int? CashierId { get; set; }
    public string? CashierName { get; set; }
    public string? CustomerName { get; set; }
    public int ItemCount { get; set; }
    public decimal TotalUSD { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime SavedAtUtc { get; set; }
    public int Version { get; set; } = 1;
}

public interface ISaleRecoveryStore
{
    void Save(SaleRecoverySnapshot snapshot);
    SaleRecoverySnapshot? Load();
    void Clear();
}

public class SaleRecoveryStore : ISaleRecoveryStore
{
    private readonly string _recoveryFilePath;
    private readonly object _lock = new object();

    public SaleRecoveryStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _recoveryFilePath = customPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "ProyectoPOS");
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
            }

            _recoveryFilePath = Path.Combine(folder, "active_sale_recovery.json");
        }
    }

    public void Save(SaleRecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_recoveryFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_recoveryFilePath, json);
            }
            catch
            {
            }
        }
    }

    public SaleRecoverySnapshot? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_recoveryFilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(_recoveryFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<SaleRecoverySnapshot>(json);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_recoveryFilePath))
                {
                    File.Delete(_recoveryFilePath);
                }
            }
            catch
            {
            }
        }
    }
}
