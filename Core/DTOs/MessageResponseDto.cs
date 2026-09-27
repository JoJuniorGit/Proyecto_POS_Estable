namespace Core.DTOs;

/// <summary>
/// Typed contract for simple JSON message responses (AUD-05.1). Preserves the existing
/// {"message": "..."} wire shape used by the logout and change-password endpoints.
/// </summary>
public class MessageResponseDto
{
    public string Message { get; set; } = string.Empty;

    public MessageResponseDto()
    {
    }

    public MessageResponseDto(string message)
    {
        Message = message;
    }
}
