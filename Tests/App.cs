using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

// The tests share one real run of the AppHost: Keycloak in a
// container, the API, the BFF, and the Vite dev server.
[assembly: DoNotParallelize]

[TestClass]
public static class App
{
    private static readonly ECDsa otherKey =
        ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static DistributedApplication? app;

    public static KeycloakAdmin Admin { get; private set; } = null!;

    // A second key, which the BFF publishes as it would during a
    // change of keys. The tests sign with its private half.
    public static string OtherClientKey { get; } =
        otherKey.ExportPkcs8PrivateKeyPem();

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;

        // The realm's redirect URIs need the AppHost's fixed ports.
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AppHost>(
                ["DcpPublisher:RandomizePorts=false"],
                (options, _) => options.DisableDashboard = true,
                ct);
        builder.CreateResourceBuilder<ProjectResource>("bff")
            .WithEnvironment("OtherClientKey",
                otherKey.ExportSubjectPublicKeyInfoPem());
        app = await builder.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("bff", ct);

        var keycloak = app.Services
            .GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<KeycloakResource>()
            .Single();
        var password = await keycloak.AdminPasswordParameter.GetValueAsync(ct);
        Admin = new KeycloakAdmin(password!);
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        Admin?.Dispose();
        if (app is not null) await app.DisposeAsync();
    }
}
