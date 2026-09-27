namespace Desktop.Client.Services;

public static class ApiErrorParser
{
    public static string FromBody(string? body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return body;
            }

            foreach (var property in new[] { "message", "detail", "title", "Message", "Detail", "Title", "errors" })
            {
                if (doc.RootElement.TryGetProperty(property, out var element) &&
                    element.ValueKind == System.Text.Json.JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(element.GetString()))
                {
                    return element.GetString()!;
                }
            }

            return body;
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }
}