using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

// Gives the forwarder the session's access token, refreshed
// when it has less than a minute left.
public sealed class AccessTokens(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    ClientAssertion assertion,
    HybridCache cache,
    TimeProvider time)
{
    private const string Cookie =
        CookieAuthenticationDefaults.AuthenticationScheme;

    // One refresh per refresh token: requests that arrive during
    // it wait for it, and for a minute after it, requests that
    // still send the old cookie get the same tokens.
    private static readonly HybridCacheEntryOptions oneMinute = new()
    {
        Expiration = TimeSpan.FromMinutes(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
    };

    public sealed record Tokens(
        string Access, string Refresh, DateTimeOffset ExpiresAt);

    public async Task<string?> GetAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(Cookie);
        if (result is not { Succeeded: true, Properties: { } props })
        {
            return null;
        }

        var expiresAt = DateTimeOffset.Parse(
            props.GetTokenValue("expires_at")!,
            CultureInfo.InvariantCulture);
        if (expiresAt - time.GetUtcNow() > TimeSpan.FromMinutes(1))
        {
            return props.GetTokenValue("access_token");
        }

        // The cache key is a hash, not the refresh token itself.
        var refreshToken = props.GetTokenValue("refresh_token")!;
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        Tokens? tokens;
        try
        {
            tokens = await cache.GetOrCreateAsync(
                key, ct => RefreshAsync(refreshToken, ct), oneMinute);
        }
        catch (Exception e) when (
            e is HttpRequestException or OperationCanceledException
            && expiresAt > time.GetUtcNow())
        {
            // Keycloak did not answer, but the token is still valid.
            return props.GetTokenValue("access_token");
        }

        if (tokens is null)
        {
            // Keycloak refused the refresh token: the session is over.
            await context.SignOutAsync(Cookie);
            return null;
        }

        // Keep the new tokens in the cookie.
        props.UpdateTokenValue("access_token", tokens.Access);
        props.UpdateTokenValue("refresh_token", tokens.Refresh);
        props.UpdateTokenValue("expires_at", tokens.ExpiresAt
            .ToString("o", CultureInfo.InvariantCulture));
        await context.SignInAsync(Cookie, result.Principal, props);
        return tokens.Access;
    }

    // Null when Keycloak refuses the refresh token.
    private async ValueTask<Tokens?> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        var options = oidcOptions.Get(
            OpenIdConnectDefaults.AuthenticationScheme);
        var config = await options.ConfigurationManager!
            .GetConfigurationAsync(cancellationToken);

        var request = new OpenIdConnectMessage
        {
            GrantType = OpenIdConnectGrantTypes.RefreshToken,
            RefreshToken = refreshToken,
            ClientId = options.ClientId,
        };
        assertion.AddTo(request);

        using var response = await options.Backchannel.PostAsync(
            config.TokenEndpoint,
            new FormUrlEncodedContent(request.Parameters),
            cancellationToken);
        var message = new OpenIdConnectMessage(
            await response.Content.ReadAsStringAsync(cancellationToken));

        if (response.IsSuccessStatusCode)
        {
            var expiresIn = int.Parse(
                message.ExpiresIn, CultureInfo.InvariantCulture);
            return new(message.AccessToken, message.RefreshToken,
                time.GetUtcNow().AddSeconds(expiresIn));
        }
        if (message.Error == "invalid_grant") return null;
        throw new HttpRequestException(
            $"Refresh failed: {(int)response.StatusCode} {message.Error}");
    }
}
