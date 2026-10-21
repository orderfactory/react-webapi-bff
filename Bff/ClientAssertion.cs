using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// private_key_jwt: the BFF proves who it is with a short-lived JWT
// signed by its private key. Keycloak holds only the public key.
public sealed class ClientAssertion(
    string clientId, string issuer, ECDsa key)
{
    private readonly JsonWebTokenHandler handler = new();
    private readonly SigningCredentials credentials =
        new(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256);

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
