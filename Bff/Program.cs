using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication
    .CreateBuilder(args);

builder.AddServiceDefaults();

const string cookie = CookieAuthenticationDefaults.AuthenticationScheme;
const string oidc = OpenIdConnectDefaults.AuthenticationScheme;

string Setting(string key) => builder.Configuration[key]
    ?? throw new InvalidOperationException($"{key} is not set.");

var clientKey = ECDsa.Create();
clientKey.ImportPkcs8PrivateKey(
    Convert.FromBase64String(Setting("ClientKey")), out _);
var assertion = new ClientAssertion(
    "weather-bff", Setting("Authority"), clientKey);

builder.Services.AddSingleton(assertion);
builder.Services.AddSingleton<AccessTokens>();
builder.Services.AddHybridCache();
builder.Services.AddHttpForwarder();

builder.Services
    .AddAuthentication(cookie)
    .AddCookie(cookie, options =>
    {
        // HttpOnly and Path=/ are the defaults.
        options.Cookie.Name = "__Host-Http-bff";
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        // Keycloak's SSO Session Max: no refresh token lasts longer.
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.SlidingExpiration = false;
        // Answer the app's calls with 401, not a redirect.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(oidc, options =>
    {
        options.Authority = Setting("Authority");
        options.ClientId = "weather-bff";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType =
            "preferred_username";
        // Authenticate with a signed JWT, not a secret.
        options.Events.OnPushAuthorization = context =>
        {
            assertion.AddTo(context.ProtocolMessage);
            context.HandleClientAuthentication();
            return Task.CompletedTask;
        };
        options.Events.OnAuthorizationCodeReceived = context =>
        {
            assertion.AddTo(context.TokenEndpointRequest!);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// CSRF defense: the app's scripts send this header. A page on
// another origin cannot send it without a CORS preflight, and
// the BFF approves no preflight.
app.Use((context, next) =>
{
    var path = context.Request.Path;
    var fromScript = path.StartsWithSegments("/api")
        || path.StartsWithSegments("/bff/user");
    if (fromScript && context.Request.Headers["X-CSRF"] != "1")
    {
        context.Response.StatusCode = 400;
        return Task.CompletedTask;
    }
    return next(context);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();

app.MapGet("/bff/login", () => TypedResults.Challenge(
    new() { RedirectUri = "/" }, [oidc]));

// A form cannot send X-CSRF, so check where the post came from.
app.MapPost("/bff/logout", (HttpRequest request) =>
    request.Headers["Sec-Fetch-Site"] == "same-origin"
        ? Results.SignOut(new() { RedirectUri = "/" }, [cookie, oidc])
        : Results.StatusCode(403));

app.MapGet("/bff/user", (ClaimsPrincipal user) =>
    new { Name = user.Identity?.Name })
    .RequireAuthorization();

// The only API route: GET /api/forecast goes to GET /forecast.
var tokens = app.Services.GetRequiredService<AccessTokens>();
app.MapForwarder("/api/forecast", Setting("ApiUrl"), transforms =>
{
    transforms.AddPathSet("/forecast");
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
})
.RequireAuthorization()
.WithMetadata(new HttpMethodMetadata(["GET"]));

// In development, the Vite dev server serves the app.
if (app.Configuration["SpaDevServer"] is { } spaDevServer)
{
    app.MapForwarder("/{**path}", spaDevServer, transforms =>
        transforms.AddRequestHeaderRemove("Cookie"));
}

app.Run();
