using System.Text.RegularExpressions;

namespace ReVueRemote;

public static partial class TransferViewerIdentity
{
    private const string CookieName = "ReVueRemoteTransferViewer";

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ViewerTokenPattern();

    public static string? QueryToken(HttpContext context)
    {
        var token = context.Request.Query["viewer"].ToString();
        return ViewerTokenPattern().IsMatch(token) ? token : null;
    }

    public static string RateLimitKey(HttpContext context)
    {
        if (QueryToken(context) is { } token) return "viewer:" + token;
        if (context.Request.Cookies.TryGetValue(CookieName, out var existing) &&
            Guid.TryParseExact(existing, "N", out _))
            return "browser:" + existing;

        var generated = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(CookieName, generated, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/api/sessions"
        });
        return "browser:" + generated;
    }

    public static string AddViewerToPlaylist(string playlist, string? token)
    {
        if (token is null) return playlist;
        var encoded = Uri.EscapeDataString(token);
        playlist = Regex.Replace(playlist, "URI=\"([^\"]+)\"", match =>
            "URI=\"" + Append(match.Groups[1].Value, encoded) + "\"");
        return Regex.Replace(playlist, @"(?m)^(?!#)([^\r\n]+)", match =>
            Append(match.Value, encoded));
    }

    private static string Append(string uri, string encodedToken)
    {
        var fragmentAt = uri.IndexOf('#');
        var baseUri = fragmentAt < 0 ? uri : uri[..fragmentAt];
        var fragment = fragmentAt < 0 ? "" : uri[fragmentAt..];
        var queryAt = baseUri.IndexOf('?');
        if (queryAt < 0) return baseUri + "?viewer=" + encodedToken + fragment;
        var query = baseUri[(queryAt + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("viewer=", StringComparison.OrdinalIgnoreCase));
        return baseUri[..queryAt] + "?" + string.Join('&', query.Append("viewer=" + encodedToken)) + fragment;
    }
}
