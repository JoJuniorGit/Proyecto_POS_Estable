using System;
using System.Collections.Generic;
using System.Linq;

namespace Backend.API.Metrics;

public sealed class RequestMetricsRegistry
{
    private const int MaxSamples = 1000;
    private readonly object _lock = new();
    private readonly Dictionary<string, EndpointMetric> _endpoints = new(StringComparer.Ordinal);

    public void Record(string method, string path, double milliseconds, int statusCode)
    {
        var key = $"{method} {path}";
        lock (_lock)
        {
            if (!_endpoints.TryGetValue(key, out var metric))
            {
                metric = new EndpointMetric();
                _endpoints[key] = metric;
            }
            metric.Record(milliseconds, statusCode);
        }
    }

    public IReadOnlyList<EndpointMetricSnapshot> GetSnapshot()
    {
        lock (_lock)
        {
            return _endpoints
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value.ToSnapshot(kv.Key))
                .ToList();
        }
    }

    private sealed class EndpointMetric
    {
        private readonly List<double> _samples = new();
        private long _total;
        private long _errors;
        private double _sumMs;
        private double _maxMs;

        public void Record(double milliseconds, int statusCode)
        {
            _total++;
            if (statusCode >= 500)
            {
                _errors++;
            }
            _sumMs += milliseconds;
            if (milliseconds > _maxMs)
            {
                _maxMs = milliseconds;
            }
            _samples.Add(milliseconds);
            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveAt(0);
            }
        }

        public EndpointMetricSnapshot ToSnapshot(string key)
        {
            var sorted = _samples.OrderBy(v => v).ToList();
            double p95 = sorted.Count == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
            double avg = _total == 0 ? 0 : _sumMs / _total;
            return new EndpointMetricSnapshot(key, _total, _errors, avg, _maxMs, p95);
        }
    }
}

public sealed record EndpointMetricSnapshot(
    string Endpoint,
    long TotalCount,
    long ErrorCount,
    double AverageMilliseconds,
    double MaxMilliseconds,
    double P95Milliseconds);
