namespace Desktop.Client.Services;

public interface IJitterProvider
{
    int GetJitterDelayMs(int minMs, int maxMs);
}
