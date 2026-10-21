using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Yarp.ReverseProxy.Transforms;

public static class BffEndpoints
{
    private const string Cookie =
        CookieAuthenticationDefaults.AuthenticationScheme;
    private const string Oidc =
        OpenIdConnectDefaults.AuthenticationScheme;

    public static void MapBffEndpoints(this WebApplication app)
    {
        var tokens = app.Services.GetRequiredService<AccessTokens>();
        var assertion = app.Services.GetRequiredService<ClientAssertion>();

        app.MapGet("/bff/login", () => TypedResults.Challenge(
            new() { RedirectUri = "/" }, [Oidc]));

        // A form cannot send X-CSRF, so check where the post came from.
        app.MapPost("/bff/logout", (HttpRequest request) =>
            request.Headers["Sec-Fetch-Site"] == "same-origin"
                ? Results.SignOut(new() { RedirectUri = "/" }, [Cookie, Oidc])
                : Results.StatusCode(403));

        // A session is active while the BFF can still get a token.
        app.MapGet("/bff/user", async (HttpContext context) =>
            await tokens.GetAsync(context) is null
                ? Results.Unauthorized()
                : Results.Ok(new { Name = context.User.Identity?.Name }))
            .RequireAuthorization()
            .RequireRateLimiting("session");

        // Keycloak reads the BFF's public key here.
        app.MapGet("/bff/jwks", () => Results.Json(assertion.Jwks));
    }

    // Forwards one method and path to one API URL, with the
    // session's access token instead of the browser's cookie.
    public static void MapApiRoute(this WebApplication app,
        string method, string path, string apiUrl, string apiPath)
    {
        var tokens = app.Services.GetRequiredService<AccessTokens>();

        app.MapForwarder(path, apiUrl, transforms =>
        {
            transforms.AddPathSet(apiPath);
            transforms.AddRequestHeaderRemove("Cookie");
            transforms.AddRequestTransform(async context =>
            {
                var token = await tokens.GetAsync(context.HttpContext);
                if (token is null)
                {
                    // A status set here ends the request unforwarded.
                    context.HttpContext.Response.StatusCode = 401;
                    return;
                }
                context.ProxyRequest.Headers.Authorization =
                    new("Bearer", token);
            });
            // If the API refuses the token, the session is over too.
            transforms.AddResponseTransform(async context =>
            {
                if (context.ProxyResponse?.StatusCode
                    == HttpStatusCode.Unauthorized)
                {
                    await context.HttpContext.SignOutAsync(Cookie);
                }
            });
        })
        .RequireAuthorization()
        .RequireRateLimiting("session")
        .WithMetadata(new HttpMethodMetadata([method]));
    }

    // Everything else goes to the Vite dev server, cookie removed.
    public static void MapDevServer(this WebApplication app, string url)
    {
        app.MapForwarder("/{**path}", url, transforms =>
            transforms.AddRequestHeaderRemove("Cookie"));
    }
}
