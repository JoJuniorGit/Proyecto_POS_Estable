namespace Backend.API.Services;

public sealed record HttpsRuntimeInfo(bool Enabled, int HttpPort, int HttpsPort);
