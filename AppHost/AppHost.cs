using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

var builder = DistributedApplication
    .CreateBuilder(args);

// The realm's redirect URIs and
// the issuer URL use these ports.
const int keycloakPort = 3443;
const int bffPort = 3446;
const string realm = "demo";
const string keycloakImageTag = "26.8.1";

var authority = $"https://localhost:{keycloakPort}/realms/{realm}";

// A new key pair on every run. Keycloak gets the public key in
// a self-signed certificate; the BFF gets the private key.
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var certificate = new CertificateRequest(
        "CN=weather-bff", key, HashAlgorithmName.SHA256)
    .CreateSelfSigned(DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(1));
var clientKey = builder.AddParameter("bff-client-key",
    Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
    secret: true);

var keycloak = builder
    .AddKeycloak("keycloak", keycloakPort)
    .WithImageTag(keycloakImageTag)
    .WithRealmImport("./realms")
    .WithEnvironment("BFF_CERTIFICATE",
        Convert.ToBase64String(certificate.RawData));

var api = builder
    .AddProject<Projects.Api>("api")
    .WithEnvironment("Authority", authority)
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak);

// Reached only through the BFF.
var spa = builder.AddViteApp("spa", "../spa");

builder.AddProject<Projects.Bff>("bff")
    .WithEndpoint("https", e => e.Port = bffPort)
    .WithEnvironment("Authority", authority)
    .WithEnvironment("ClientKey", clientKey)
    .WithEnvironment("ApiUrl", api.GetEndpoint("https"))
    .WithEnvironment("SpaDevServer", spa.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(api)
    .WaitFor(spa);

builder.Build().Run();
