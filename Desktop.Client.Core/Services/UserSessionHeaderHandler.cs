using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Logging;

namespace Desktop.Client.Services;

public class UserSessionHeaderHandler : DelegatingHandler
{
    private readonly UserSession _userSession;
    private readonly IConnectionManager? _connectionManager;
    private readonly IDispatcherInvoker _dispatcherInvoker;

    public UserSessionHeaderHandler(UserSession userSession, IConnectionManager? connectionManager = null, IDispatcherInvoker? dispatcherInvoker = null)
    {
        _userSession = userSession;
        _connectionManager = connectionManager;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
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
        request.Headers.Add("X-Client-Version", Core.Common.AppVersionHelper.CurrentVersion);

        // Cabecera meramente informativa y de telemetría/diagnóstico.
        // ADVERTENCIA DE SEGURIDAD: La API NO debe usar esta cabecera para autorización.
        // La autorización y el aislamiento de plataformas se basan 100% en los claims criptográficos (scope: pos:desktop).
        request.Headers.Remove("X-Client-Platform");
        request.Headers.Add("X-Client-Platform", "Desktop");

        if (!string.IsNullOrWhiteSpace(_userSession.Token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _userSession.Token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // If the token expired or user is unauthorized (and not during login attempt), reset session cleanly
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && 
            request.RequestUri?.AbsolutePath.Contains("api/auth/login") != true)
        {
            ClientStateLogger.LogWarning("[AUTH] Sesión expirada o token inválido (HTTP 401). Forzando cierre de sesión.", "UserSessionHeaderHandler");
            if (!_dispatcherInvoker.CheckAccess())
            {
                _dispatcherInvoker.Invoke(_userSession.Logout);
            }
            else
            {
                _userSession.Logout();
            }
        }

        return response;
    }
}
