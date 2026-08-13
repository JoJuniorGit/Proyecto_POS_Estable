using System;

namespace Desktop.Client.Services;

public class ProductionJitterProvider : IJitterProvider
{
    public int GetJitterDelayMs(int minMs, int maxMs)
    {
        if (minMs >= maxMs) return minMs;
        return Random.Shared.Next(minMs, maxMs);
    }
}
