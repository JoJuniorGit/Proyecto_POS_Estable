using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Logging;

namespace Desktop.Client.Services;

public class UserSessionHeaderHandler : DelegatingHandler
{
    private readonly UserSession _userSession;
    private readonly IConnectionManager? _connectionManager;

    public UserSessionHeaderHandler(UserSession userSession, IConnectionManager? connectionManager = null)
    {
        _userSession = userSession;
        _connectionManager = connectionManager;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_connectionManager != null && request.RequestUri != null && 
            Uri.TryCreate(_connectionManager.CurrentServerAddress, UriKind.Absolute, out var currentServerUri))
        {
            if (request.RequestUri.Host != currentServerUri.Host || 
                request.RequestUri.Port != currentServerUri.Port || 
                request.RequestUri.Scheme != currentServerUri.Scheme)
            {
                var uriBuilder = new System.UriBuilder(request.RequestUri)
                {
                    Scheme = currentServerUri.Scheme,
                    Host = currentServerUri.Host,
                    Port = currentServerUri.Port
                };
                request.RequestUri = uriBuilder.Uri;
            }
        }

        request.Headers.Remove("X-Client-Version");
        request.Headers.Add("X-Client-Version", "1.0.0");

        if (!string.IsNullOrWhiteSpace(_userSession.Token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _userSession.Token);
        }

        if (_userSession.CurrentUser != null)
        {
            request.Headers.Remove("X-User-Role");
            request.Headers.Add("X-User-Role", _userSession.CurrentUser.Role.ToString());

            request.Headers.Remove("X-User-Id");
            request.Headers.Add("X-User-Id", _userSession.CurrentUser.Id.ToString());
        }

        var response = await base.SendAsync(request, cancellationToken);

        // If the token expired or user is unauthorized (and not during login attempt), reset session cleanly
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && 
            request.RequestUri?.AbsolutePath.Contains("api/auth/login") != true)
        {
            ClientStateLogger.LogWarning("[AUTH] Sesión expirada o token inválido (HTTP 401). Forzando cierre de sesión.", "UserSessionHeaderHandler");
            var app = System.Windows.Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => _userSession.Logout());
            }
            else
            {
                _userSession.Logout();
            }
        }

        return response;
    }
}
