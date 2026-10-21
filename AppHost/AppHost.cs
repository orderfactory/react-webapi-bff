using System.Security.Cryptography;

var builder = DistributedApplication
    .CreateBuilder(args);

// The realm's redirect URIs and
// the issuer URL use these ports.
const int keycloakPort = 3443;
const int bffPort = 3446;
const string realm = "demo";
const string keycloakImageTag = "26.8.1";

var authority = $"https://localhost:{keycloakPort}/realms/{realm}";

// A new client key on every run. The BFF gets the private key
// and publishes the public key, which Keycloak fetches.
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var clientKey = builder.AddParameter("bff-client-key",
    key.ExportPkcs8PrivateKeyPem(), secret: true);

var keycloak = builder
    .AddKeycloak("keycloak", keycloakPort)
    .WithImageTag(keycloakImageTag)
    .WithRealmImport("./realms")
    // Keycloak trusts the PEM files in conf/truststores, so it
    // trusts the development certificate that the BFF serves.
    .WithContainerCertificatePaths("/opt/keycloak/conf/truststores");

var api = builder
    .AddProject<Projects.Api>("api")
    .WithEnvironment("Authority", authority)
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak);

// Reached only through the BFF.
var spa = builder.AddViteApp("spa", "../spa");

var bff = builder.AddProject<Projects.Bff>("bff")
    .WithEndpoint("https", e => e.Port = bffPort)
    .WithEnvironment("Authority", authority)
    .WithEnvironment("ClientKey", clientKey)
    .WithEnvironment("ApiUrl", api.GetEndpoint("https"))
    .WithEnvironment("SpaDevServer", spa.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(api)
    .WaitFor(spa);

// The realm file reads this URL from the environment.
keycloak.WithEnvironment("BFF_JWKS_URL",
    ReferenceExpression.Create($"{bff.GetEndpoint("https")}/bff/jwks"));

builder.Build().Run();
