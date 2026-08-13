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

        if (_userSession.CurrentUser != null)
        {
            request.Headers.Remove("X-User-Role");
            request.Headers.Add("X-User-Role", _userSession.CurrentUser.Role.ToString());

            request.Headers.Remove("X-User-Id");
            request.Headers.Add("X-User-Id", _userSession.CurrentUser.Id.ToString());
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
