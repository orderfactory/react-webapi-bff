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
    private static DistributedApplication? app;

    // The keys the BFF gets when RestartBffAsync restarts it.
    private static string? clientKey;
    private static string? otherClientKey;

    public static KeycloakAdmin Admin { get; private set; } = null!;

    // The private key that the AppHost created for the BFF.
    public static string AppHostClientKey { get; private set; } = null!;

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
            .WithEnvironment(environment =>
            {
                if (clientKey is null) return;
                environment.EnvironmentVariables["ClientKey"] = clientKey;
                environment.EnvironmentVariables["OtherClientKey"] =
                    otherClientKey!;
            });
        app = await builder.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("bff", ct);

        var resources = app.Services
            .GetRequiredService<DistributedApplicationModel>().Resources;
        var keycloak = resources.OfType<KeycloakResource>().Single();
        var password = await keycloak.AdminPasswordParameter.GetValueAsync(ct);
        Admin = new KeycloakAdmin(password!);
        AppHostClientKey = (await resources.OfType<ParameterResource>()
            .Single(p => p.Name == "bff-client-key").GetValueAsync(ct))!;
    }

    // Restarts the BFF with a new private key, and with the public
    // key that it publishes beside it.
    public static async Task RestartBffAsync(string key, string otherKey)
    {
        clientKey = key;
        otherClientKey = otherKey;
        var result = await app!.ResourceCommands.ExecuteCommandAsync(
            "bff", KnownResourceCommands.RestartCommand);
        Assert.IsTrue(result.Success, result.Message);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("bff");
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        Admin?.Dispose();
        if (app is not null) await app.DisposeAsync();
    }
}
