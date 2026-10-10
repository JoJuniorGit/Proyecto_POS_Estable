namespace Desktop.Client.Services;

public static class ApiErrorParser
{
    /// <summary>
    /// 8.150 (T10, design D5): detecta el contrato de accion protegida en un ProblemDetails
    /// (extensiones <c>authorizationRequired</c>/<c>authorizationAction</c>). Es aditivo y seguro
    /// ante bodies no JSON: nunca lanza, devuelve false y deja la accion en null.
    /// </summary>
    public static bool TryGetAuthorizationRequirement(string? body, out string? authorizationAction)
    {
        authorizationAction = null;
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetProperty(doc.RootElement, "authorizationRequired", out var required))
            {
                return false;
            }

            var isRequired = required.ValueKind == System.Text.Json.JsonValueKind.True
                || (required.ValueKind == System.Text.Json.JsonValueKind.String
                    && bool.TryParse(required.GetString(), out var parsed)
                    && parsed);
            if (!isRequired)
            {
                return false;
            }

            if (TryGetProperty(doc.RootElement, "authorizationAction", out var action)
                && action.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                authorizationAction = action.GetString();
            }

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(System.Text.Json.JsonElement element, string name, out System.Text.Json.JsonElement value)
    {
        // Las extensiones ProblemDetails se emiten tal cual se registran (camelCase); se acepta
        // tambien PascalCase por robustez ante configuraciones de serializacion distintas.
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

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