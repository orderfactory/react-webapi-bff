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

    public ClientAssertion(string clientId, string issuer, ECDsa key)
    {
        if (key.KeySize != 256)
        {
            throw new ArgumentException("The client key must be P-256.");
        }
        this.clientId = clientId;
        this.issuer = issuer;

        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var x = Base64UrlEncoder.Encode(q.X);
        var y = Base64UrlEncoder.Encode(q.Y);
        // The key ID is the key's RFC 7638 thumbprint.
        var keyId = Base64UrlEncoder.Encode(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));

        credentials = new(new ECDsaSecurityKey(key) { KeyId = keyId },
            SecurityAlgorithms.EcdsaSha256);
        // Public members only: the private key never leaves the BFF.
        Jwks = new
        {
            keys = new[]
            {
                new { kty = "EC", crv = "P-256", x, y,
                    kid = keyId, use = "sig", alg = "ES256" },
            },
        };
    }

    // Published at /bff/jwks.
    public object Jwks { get; }

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
