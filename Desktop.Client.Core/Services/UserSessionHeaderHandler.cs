using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class UserSessionHeaderHandler : DelegatingHandler
{
    private readonly UserSession _userSession;

    public UserSessionHeaderHandler(UserSession userSession)
    {
        _userSession = userSession;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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
