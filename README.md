# react-webapi-bff

A React single-page app signs users in through an ASP.NET Core backend for frontend (BFF). The BFF is the OAuth client: it signs in with Keycloak using the authorization code flow with PKCE and pushed authorization requests, proves who it is with a signed JWT instead of a client secret, keeps the tokens in an encrypted HttpOnly cookie, and forwards the app's API calls with the access token attached. The browser never holds a token. Aspire starts Keycloak, the API, the BFF, and the Vite dev server with one command, and an end-to-end test project checks the security behavior against all of them.

This repository is the companion to the article [Backend for Frontend: The Browser Gets a Cookie, Not a Token](https://www.orderfactory.com/articles/backend-for-frontend.html), which explains every setting below, including what a deployment must add. It continues [react-webapi-oidc](https://github.com/orderfactory/react-webapi-oidc) and its article, [OpenID Connect End to End](https://www.orderfactory.com/articles/openid-connect-end-to-end.html), where the browser app held its own tokens. [RFC 10017](https://www.rfc-editor.org/rfc/rfc10017.html) ranks the BFF as the most secure of the three architectures it describes for browser-based apps.

## Features

- The BFF is a confidential client that authenticates with `private_key_jwt` (ES256). It publishes its public key at `/bff/jwks`, and Keycloak reads it from there, so no secret exists and a new key needs no change in Keycloak. During a change of keys, the BFF publishes the other key as well.
- Sign-in uses the authorization code flow with PKCE (S256) and pushed authorization requests (PAR), which the realm requires for this client.
- The session cookie, `__Host-Http-bff`, is HttpOnly, Secure, SameSite=Strict, and Path=/, with no Domain, as RFC 10017 recommends. ASP.NET Core Data Protection encrypts the tokens inside it.
- CSRF defense: the app sends `X-CSRF: 1` on every call, and the BFF refuses calls without it. Sign-out, a form post, must come from the BFF's own origin (`Sec-Fetch-Site: same-origin`).
- The BFF forwards exactly one route, `GET /api/forecast`, to `GET /forecast` on the API, with the access token, and removes the browser's cookie.
- One refresh per refresh token: concurrent requests share it, and for a minute afterward, requests that still send the old cookie get the same tokens.
- A session counts as active only while the BFF can still get an access token for it. `/bff/user` refreshes the token when it is due and answers 401 once Keycloak has ended the session, and a 401 from the API ends the BFF's session too.
- Each session may make 100 calls a minute; the 101st gets 429.
- A failed sign-in returns to the app, which offers it again. When Keycloak does not answer within 10 seconds, the BFF keeps using a token that has not expired yet and logs a warning.
- Answers to the app's calls carry `Cache-Control: no-store`. Built for production, the app is served with the Content Security Policy `default-src 'self'; base-uri 'none'; frame-ancestors 'none'`, its hashed assets are cached for a year and its page is checked again on every visit, and the BFF sends HSTS.
- The API is the one from react-webapi-oidc without its CORS policy, which the browser no longer needs. It checks the signature, issuer, audience, expiry (with a 30-second clock tolerance), and the `meteorologist` role.

## System overview

| Component | Project | URL |
|-----------|---------|-----|
| Keycloak 26.8.1, realm `demo` | container started by `AppHost` | https://localhost:3443 |
| ASP.NET Core API on .NET 10 | `Api` | https://localhost:3445/forecast |
| ASP.NET Core BFF on .NET 10, the app's only origin | `Bff` | https://localhost:3446 |
| React 19 app on Vite 8, reached through the BFF | `spa` | a port Aspire assigns |
| Aspire 13 dashboard | `AppHost` | https://localhost:17175 |

The Keycloak and BFF ports are fixed: the token issuer and the realm's redirect URIs depend on them. Keycloak, the API, and the BFF serve HTTPS with the ASP.NET Core development certificate, and Keycloak trusts that certificate when it fetches the BFF's public key.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) 22.12 or later
- A container runtime: [Docker Desktop](https://www.docker.com/products/docker-desktop/), [Rancher Desktop](https://rancherdesktop.io/) with the dockerd (moby) engine, or [Podman](https://podman.io/) with `ASPIRE_CONTAINER_RUNTIME` set to `podman`
- A trusted ASP.NET Core development certificate. Aspire serves Keycloak over HTTPS only when the certificate is present and trusted:

  ```shell
  dotnet dev-certs https --trust
  ```

The [Aspire CLI](https://aspire.dev/get-started/install-cli/) is optional. Without it, `dotnet run` and the IDEs fetch the matching version through `dnx`.

## Run

Clone the repository and start the AppHost from the repository root, in any of three ways:

- `aspire run`
- `dotnet run --project AppHost`
- In Visual Studio 2026 or Rider, open `react-webapi-bff.slnx` and run the `AppHost` project.

The command line prints a sign-in link to the Aspire dashboard. `spa-installer` runs `npm install` and finishes, and `spa` starts the Vite dev server. `keycloak` starts in a container, imports the realm, and turns healthy in about a minute, longer on the first run while the image downloads. Then `api` starts, then `bff`. The dashboard also lists `bff-client-key`, the BFF's private key, as a secret parameter.

## Usage

1. Open https://localhost:3446 and select **Sign in**.
2. Sign in as one of the two users from the realm file. Both use the password `password`.

   | User | Role | Show forecast |
   |------|------|---------------|
   | `jane` | `meteorologist` | `{"for":"jane","days":["Cool","Mild","Warm"]}` |
   | `bob` | none | `API: 403` |

3. Select **Show forecast**, then **Sign out**.

The browser's developer tools show the difference from react-webapi-oidc: the app's storage is empty, `document.cookie` is an empty string, and the only cookies are the BFF's HttpOnly `__Host-Http-bff` chunks.

### Keycloak admin console

Open https://localhost:3443/admin and sign in as `admin`. To find the generated password, open **View details** for the `keycloak` resource in the dashboard and reveal `KC_BOOTSTRAP_ADMIN_PASSWORD` under **Environment variables**, or run `dotnet user-secrets list --project AppHost` and look for `Parameters:keycloak-password`.

Keycloak runs without a data volume. It starts empty and imports `AppHost/realms/demo-realm.json` on every run, so changes made in the admin console last only until the AppHost stops. Put lasting changes in the realm file.

## Test

The `Tests` project starts the AppHost, with Keycloak in its container, and checks the behavior above: sign-in through Keycloak's login form, the role check, the cookie's attributes, the CSRF header, the sign-out check, the shared refresh with and without rotation, a session ended in Keycloak, the rate limit, the published keys, Keycloak's acceptance of a second published key, as during a change of keys, and a failed sign-in. Stop any running AppHost first, because the tests use the same fixed ports:

```shell
dotnet test --project Tests
```

The tests use MSTest on Microsoft.Testing.Platform, which `global.json` selects for `dotnet test`. A run takes about a minute and a half, most of it Keycloak's startup. The `Test` workflow runs them on every push.

## Keycloak configuration

`AppHost/realms/demo-realm.json` holds only the settings that differ from Keycloak's defaults.

**Realm `demo`:** refresh token rotation stays off, Keycloak's default. RFC 9700 requires rotation or sender-constrained refresh tokens only for public clients, and the BFF's refresh tokens work only with its key. If a realm must rotate for other clients, the BFF copes within one instance; see the article.

**Client `weather-api`**, the API: every flow off. It owns the client role `meteorologist`, which `jane` holds.

**Client `weather-bff`**, the BFF:

| Setting | Value |
|---------|-------|
| Client authentication | On (a confidential client) |
| Client authenticator | Signed JWT, signature algorithm ES256 |
| Keys | Use JWKS URL on; the URL, the BFF's `/bff/jwks`, comes from the `BFF_JWKS_URL` placeholder, which the AppHost sets |
| Standard flow | On |
| Direct access grants, Implicit flow, Service accounts roles | Off |
| Require PKCE | On (S256) |
| Pushed authorization request required | On |
| Valid redirect URIs | `https://localhost:3446/signin-oidc` |
| Valid post logout redirect URIs | `https://localhost:3446/signout-callback-oidc` |
| Web origins | None; the browser only navigates to Keycloak |
| Dedicated scope, Scope tab | Full scope allowed off; role `meteorologist` of `weather-api` assigned |
| Dedicated scope, Audience mapper | Included Client Audience `weather-api`, added to the access token |
| Dedicated scope, User Client Role mapper | Client ID `weather-api`, Token Claim Name `role`, added to the access token |

## Deploying it

The demo runs on one machine. A deployment supplies these settings to the BFF:

| Setting | What it is |
|---------|------------|
| `Authority` | Keycloak's issuer URL for the realm |
| `ApiUrl` | The API's base URL |
| `ClientKey` | The BFF's P-256 private key in PEM, from a secret store. Register the client in Keycloak with **Use JWKS URL** pointing at the BFF's `/bff/jwks`. |
| `OtherClientKey` | Only during a change of keys: the public key, in PEM, of the key before or after `ClientKey`, which the BFF publishes but does not sign with. The article describes the steps. |
| `DataProtection:KeysPath` | A folder for the Data Protection key ring that survives restarts and that every instance shares. Required outside Development; protect it, because the keys decrypt every session. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind a TLS-terminating proxy, so that the BFF builds `https://` redirect URIs; only when the proxy is the only way in |
| `AllowedHosts` | The BFF's public host name |

Serve the built app from `Bff/wwwroot`: run `npm run build` in `spa` and copy `spa/dist` there. The article's *Production readiness* section covers what this repository leaves to the deployment, such as server-side sessions, back-channel logout, health probes, several APIs, and rotating Keycloak's signing keys.

## Project layout

```text
AppHost/                   Aspire AppHost
  AppHost.cs               The BFF's client key; Keycloak, API, Vite, and BFF
  realms/demo-realm.json   The realm Keycloak imports at startup
Api/                       ASP.NET Core minimal API
  Program.cs               Token validation, role policy, GET /forecast
Bff/                       ASP.NET Core backend for frontend
  Program.cs               Data Protection, rate limits, cookie, OpenID Connect, CSP, CSRF check
  BffEndpoints.cs          Sign-in, sign-out, the session check, the JWKS, and the API route
  ClientAssertion.cs       The signed JWT that authenticates the BFF, and its public key
  AccessTokens.cs          The session's access token, refreshed once per refresh token
ServiceDefaults/           Aspire service defaults: OpenTelemetry, health checks
Tests/                     End-to-end tests against the AppHost (MSTest)
spa/                       React, TypeScript, and Vite
  src/bff.ts               getUser() and apiGet(), with the X-CSRF header
  src/App.tsx              Sign in, Show forecast, Sign out
```

In `spa`, `npm run lint` runs [Oxlint](https://oxc.rs/docs/guide/usage/linter.html), and `npm run build` type-checks and builds the app.

## Troubleshooting

- **Keycloak is served over `http://`, or sign-in fails:** the development certificate is missing or untrusted. Run `dotnet dev-certs https --check --trust`. If it finds no trusted certificate, run `dotnet dev-certs https --trust` and restart the AppHost.
- **A resource fails with an address already in use:** ports 3443, 3445, and 3446 must be free. react-webapi-oidc and the tests use the same ports, so stop them first.
- **`keycloak` stays unhealthy:** make sure the container runtime is running, and read the resource's console logs in the dashboard.
- **Asked to sign in again after a restart:** each run imports a new realm with new keys, so a session from an earlier run ends at its next API call.
- **"Sign-in failed" after you restart only `keycloak`:** the realm came back with new signing keys that the running BFF and API haven't fetched. Each fetches them after the first token it can't verify (`IDX10503` in the BFF's log), but at most once every 5 minutes. Restart the whole AppHost instead.
- **The BFF fails to start with `DirectoryNotFoundException` for `Bff\wwwroot`:** you built the BFF with a `wwwroot` folder and then removed it. Delete `Bff/obj` and `Bff/bin` and build again.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
