using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication
    .CreateBuilder(args);

builder.AddServiceDefaults();

const string cookie = CookieAuthenticationDefaults.AuthenticationScheme;
const string oidc = OpenIdConnectDefaults.AuthenticationScheme;

string Setting(string key) => builder.Configuration[key]
    ?? throw new InvalidOperationException($"{key} is not set.");

// The client's private key, in PEM. In production it comes from a
// secret store; in development, the AppHost creates one per run.
var clientKey = ECDsa.Create();
clientKey.ImportFromPem(Setting("ClientKey"));
var assertion = new ClientAssertion(
    "weather-bff", Setting("Authority"), clientKey);

builder.Services.AddSingleton(assertion);
builder.Services.AddSingleton<AccessTokens>();
builder.Services.AddHybridCache();
builder.Services.AddHttpForwarder();

// The session is only as durable as the keys that encrypt it, so
// every instance needs the same key ring and application name.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("weather-bff");
if (builder.Configuration["DataProtection:KeysPath"] is { } keysPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}
else if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "DataProtection:KeysPath is not set.");
}

// Each session gets its own budget, so a script that has taken
// over one session cannot flood the API through the BFF.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("session", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sid")?.Value ?? "",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
            }));
});

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
        // Give up on Keycloak after 10 seconds, not the default 60.
        options.BackchannelTimeout = TimeSpan.FromSeconds(10);
        // A failed sign-in returns to the app, which offers it again.
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect("/?signin-failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };
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

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// In production, the built app comes from wwwroot. It runs no
// inline script, so its pages get a strict policy.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = file =>
    {
        var headers = file.Context.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        headers.XContentTypeOptions = "nosniff";
    },
});

// CSRF defense: the app's scripts send this header. A page on
// another origin cannot send it without a CORS preflight, and
// the BFF approves no preflight.
app.Use((context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/bff/user"))
    {
        return next(context);
    }
    if (context.Request.Headers["X-CSRF"] != "1")
    {
        context.Response.StatusCode = 400;
        return Task.CompletedTask;
    }
    // The answers hold personal data, so nothing may cache them.
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    return next(context);
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapDefaultEndpoints();
app.MapBffEndpoints();

// The app's only API route: GET /api/forecast to GET /forecast.
app.MapApiRoute(HttpMethods.Get, "/api/forecast",
    Setting("ApiUrl"), "/forecast");

// In development, the Vite dev server serves the app.
if (app.Configuration["SpaDevServer"] is { } spaDevServer)
{
    app.MapDevServer(spaDevServer);
}

app.Run();
