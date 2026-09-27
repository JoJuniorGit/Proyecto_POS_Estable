using System;
using System.IO;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SaleRecoveryStoreTests
{
    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        string path = CreateTempPath();
        try
        {
            var store = new SaleRecoveryStore(path);
            DateTime savedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            store.Save(new SaleRecoverySnapshot
            {
                SaleId = 42,
                CashierId = 7,
                CashierName = "Ana Perez",
                CustomerName = "Cliente de Prueba",
                ItemCount = 3,
                TotalUSD = 123.45m,
                Status = "Pending",
                SavedAtUtc = savedAt,
                Version = 1
            });

            var loaded = store.Load();

            Assert.NotNull(loaded);
            var snapshot = loaded!;
            Assert.Equal(42, snapshot.SaleId);
            Assert.Equal(7, snapshot.CashierId);
            Assert.Equal("Ana Perez", snapshot.CashierName);
            Assert.Equal("Cliente de Prueba", snapshot.CustomerName);
            Assert.Equal(3, snapshot.ItemCount);
            Assert.Equal(123.45m, snapshot.TotalUSD);
            Assert.Equal("Pending", snapshot.Status);
            Assert.Equal(savedAt, snapshot.SavedAtUtc);
            Assert.Equal(1, snapshot.Version);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsNull()
    {
        string path = CreateTempPath();
        try
        {
            var store = new SaleRecoveryStore(path);

            Assert.Null(store.Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_WhenFileContainsCorruptJson_ReturnsNull()
    {
        string path = CreateTempPath();
        try
        {
            File.WriteAllText(path, "no-es-json");
            var store = new SaleRecoveryStore(path);

            Assert.Null(store.Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Clear_RemovesSnapshotFile()
    {
        string path = CreateTempPath();
        try
        {
            var store = new SaleRecoveryStore(path);
            store.Save(new SaleRecoverySnapshot
            {
                SaleId = 9,
                ItemCount = 1,
                Status = "Pending",
                TotalUSD = 10m,
                SavedAtUtc = DateTime.UtcNow
            });

            store.Clear();

            Assert.Null(store.Load());
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Save_ThenLoad_OverwritesPreviousSnapshot()
    {
        string path = CreateTempPath();
        try
        {
            var store = new SaleRecoveryStore(path);
            store.Save(new SaleRecoverySnapshot { SaleId = 1, ItemCount = 1, Status = "Pending", SavedAtUtc = DateTime.UtcNow });
            store.Save(new SaleRecoverySnapshot { SaleId = 2, ItemCount = 2, Status = "Pending", SavedAtUtc = DateTime.UtcNow });

            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.SaleId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"sale_recovery_test_{Guid.NewGuid():N}.json");

    private static void Cleanup(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
