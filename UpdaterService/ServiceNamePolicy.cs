using System;
using System.Collections.Generic;

namespace UpdaterService;

public static class ServiceNamePolicy
{
    private static readonly HashSet<string> AllowedServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PosBackendService"
    };

    public static bool IsAllowed(string? serviceName) =>
        !string.IsNullOrWhiteSpace(serviceName) && AllowedServiceNames.Contains(serviceName.Trim());
}
