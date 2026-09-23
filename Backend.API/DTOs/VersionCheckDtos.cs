namespace Backend.API.DTOs;

public class VersionCheckResponseDto
{
    public string ServerVersion { get; set; } = string.Empty;
    public string MinimumClientVersion { get; set; } = string.Empty;
    public string? UpdateServerUrl { get; set; }
    public string ClientVersionReceived { get; set; } = string.Empty;
    public bool IsClientCompatible { get; set; }
}
