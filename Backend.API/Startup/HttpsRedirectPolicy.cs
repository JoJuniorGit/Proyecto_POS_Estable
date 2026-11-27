using System;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Backend.API.Startup;

public static class HttpsRedirectPolicy
{
    public static bool ShouldRedirect(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.IsHttps)
        {
            return false;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null || IPAddress.IsLoopback(remoteIp))
        {
            return false;
        }

        return !remoteIp.IsIPv4MappedToIPv6 || !IPAddress.IsLoopback(remoteIp.MapToIPv4());
    }

    public static Uri BuildTarget(HttpContext context, int httpsPort)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (httpsPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(httpsPort));
        }

        return new UriBuilder(Uri.UriSchemeHttps, context.Request.Host.Host, httpsPort)
        {
            Path = context.Request.Path.Value ?? "/",
            Query = context.Request.QueryString.Value?.TrimStart('?') ?? string.Empty
        }.Uri;
    }
}
