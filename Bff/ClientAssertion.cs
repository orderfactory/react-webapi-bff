using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// private_key_jwt: the BFF proves who it is with a short-lived JWT
// signed by its private key. Keycloak fetches the public key from
// the BFF's JWKS endpoint, so a new key needs no change in Keycloak.
public sealed class ClientAssertion
{
    private readonly string clientId;
    private readonly string issuer;
    private readonly SigningCredentials credentials;
    private readonly JsonWebTokenHandler handler = new();

    // keyPem is the private key that signs. During a key change,
    // otherKeyPem is the public key before or after it, which the
    // BFF publishes but never signs with.
    public ClientAssertion(string clientId, string issuer,
        string keyPem, string? otherKeyPem = null)
    {
        this.clientId = clientId;
        this.issuer = issuer;

        var key = ECDsa.Create();
        key.ImportFromPem(keyPem);
        var jwk = PublicJwk(key);
        credentials = new(new ECDsaSecurityKey(key) { KeyId = jwk.Kid },
            SecurityAlgorithms.EcdsaSha256);

        Jwk[] keys = [jwk];
        if (otherKeyPem is not null)
        {
            using var otherKey = ECDsa.Create();
            otherKey.ImportFromPem(otherKeyPem);
            keys = [jwk, PublicJwk(otherKey)];
        }
        Jwks = new { keys };
    }

    // Published at /bff/jwks.
    public object Jwks { get; }

    private sealed record Jwk(string Kty, string Crv, string X, string Y,
        string Kid, string Use, string Alg);

    // Public members only: the private key never leaves the BFF.
    private static Jwk PublicJwk(ECDsa key)
    {
        if (key.KeySize != 256)
        {
            throw new ArgumentException("Client keys must be P-256.");
        }
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var x = Base64UrlEncoder.Encode(q.X);
        var y = Base64UrlEncoder.Encode(q.Y);
        // The key ID is the key's RFC 7638 thumbprint.
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));
        return new("EC", "P-256", x, y, kid, "sig", "ES256");
    }

    public void AddTo(OpenIdConnectMessage message)
    {
        message.ClientAssertionType =
            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        message.ClientAssertion = handler.CreateToken(
            new SecurityTokenDescriptor
            {
                TokenType = "client-authentication+jwt",
                Issuer = clientId,
                // The issuer is the only audience (RFC 7523 update).
                Audience = issuer,
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = clientId,
                    ["jti"] = Guid.NewGuid().ToString(),
                },
                Expires = DateTime.UtcNow.AddMinutes(1),
                SigningCredentials = credentials,
            });
    }
}
