using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public class SystemSettingsService : ISystemSettingsService
{
    private readonly InventoryDbContext _context;

    public SystemSettingsService(InventoryDbContext context)
    {
        _context = context;
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = key,
                Value = value,
                LastUpdated = DateTime.UtcNow
            };
            _context.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
            setting.LastUpdated = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
    }
}
