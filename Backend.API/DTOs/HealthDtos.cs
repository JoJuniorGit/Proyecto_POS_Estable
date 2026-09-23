using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Backend.API.Metrics;

namespace Backend.API.DTOs;

public class HealthStatusDto
{
    public string Status { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    public string Timestamp { get; set; } = string.Empty;
}

public class HealthMetricsDto
{
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public double HitRatePercentage { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}

public class HealthRequestMetricsDto
{
    public IReadOnlyList<EndpointMetricSnapshot> Endpoints { get; set; } = Array.Empty<EndpointMetricSnapshot>();
    public string Timestamp { get; set; } = string.Empty;
}

public class HealthDetailsDto
{
    public string Status { get; set; } = string.Empty;
    public int MigrationsApplied { get; set; }
    public int PendingMigrations { get; set; }
    public decimal? BcvTodayRate { get; set; }
    public decimal? BcvLatestRate { get; set; }
    public bool BcvFreshToday { get; set; }
    public double DiskFreeMb { get; set; }
    public double DiskTotalMb { get; set; }
    public DateTime? CertExpiryUtc { get; set; }
    public DateTime? LastBackupUtc { get; set; }
    public double? LastBackupAgeMinutes { get; set; }
    public bool LastBackupFresh { get; set; }
    public string ConvergenceStatus { get; set; } = "not-run";
    public string? ConvergenceVersion { get; set; }
    public string? ConvergenceError { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}
