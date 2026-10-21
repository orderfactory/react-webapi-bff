# react-webapi-bff

A React single-page app signs users in through an ASP.NET Core backend for frontend (BFF). The BFF is the OAuth client: it signs in with Keycloak using the authorization code flow with PKCE and pushed authorization requests, proves who it is with a signed JWT instead of a client secret, keeps the tokens in an encrypted HttpOnly cookie, and forwards the app's API calls with the access token attached. The browser never holds a token. Aspire starts Keycloak, the API, the BFF, and the Vite dev server with one command.

This repository is the companion to the article [Backend for Frontend: The Browser Gets a Cookie, Not a Token](https://www.orderfactory.com/articles/backend-for-frontend.html), which explains every setting below. It continues [react-webapi-oidc](https://github.com/orderfactory/react-webapi-oidc) and its article, [OpenID Connect End to End](https://www.orderfactory.com/articles/openid-connect-end-to-end.html), where the browser app held its own tokens. [RFC 10017](https://www.rfc-editor.org/rfc/rfc10017.html) ranks the BFF as the most secure of the three architectures it describes for browser-based apps.

## Features

- The BFF is a confidential client that authenticates with `private_key_jwt`. The AppHost creates a new ES256 key pair on every run; Keycloak gets only the public key, in a self-signed certificate, so no secret exists anywhere.
- Sign-in uses the authorization code flow with PKCE (S256) and pushed authorization requests (PAR), which the realm requires for this client.
- The session cookie, `__Host-Http-bff`, is HttpOnly, Secure, SameSite=Strict, and Path=/, with no Domain, as RFC 10017 recommends. ASP.NET Core Data Protection encrypts the tokens inside it.
- CSRF defense: the app sends `X-CSRF: 1` on every call, and the BFF refuses calls without it. Sign-out, a form post, must come from the BFF's own origin (`Sec-Fetch-Site: same-origin`).
- The BFF forwards exactly one route, `GET /api/forecast`, to `GET /forecast` on the API, with the access token, and removes the browser's cookie.
- One refresh per refresh token: concurrent requests share it, and for a minute afterward, requests that still send the old cookie get the same tokens.
- A session counts as active only while the BFF can still get an access token for it. `/bff/user` refreshes the token when it is due and answers 401 once Keycloak has ended the session, and a 401 from the API ends the BFF's session too.
- Built for production, the app is served with a strict Content Security Policy: `default-src 'self'; base-uri 'none'; frame-ancestors 'none'`.
- The API is the one from react-webapi-oidc without its CORS policy, which the browser no longer needs. It checks the signature, issuer, audience, expiry (with a 30-second clock tolerance), and the `meteorologist` role.

## System overview

| Component | Project | URL |
|-----------|---------|-----|
| Keycloak 26.8.1, realm `demo` | container started by `AppHost` | https://localhost:3443 |
| ASP.NET Core API on .NET 10 | `Api` | https://localhost:3445/forecast |
| ASP.NET Core BFF on .NET 10, the app's only origin | `Bff` | https://localhost:3446 |
| React 19 app on Vite 8, reached through the BFF | `spa` | a port Aspire assigns |
| Aspire 13 dashboard | `AppHost` | https://localhost:17175 |

The Keycloak and BFF ports are fixed: the token issuer and the realm's redirect URIs depend on them. Keycloak, the API, and the BFF serve HTTPS with the ASP.NET Core development certificate.

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

## Keycloak configuration

`AppHost/realms/demo-realm.json` holds only the settings that differ from Keycloak's defaults.

**Realm `demo`:** refresh token rotation stays off, Keycloak's default. RFC 9700 requires rotation or sender-constrained refresh tokens only for public clients, and the BFF's refresh tokens work only with its key. If a realm must rotate for other clients, the BFF copes within one instance; see the article.

**Client `weather-api`**, the API: every flow off. It owns the client role `meteorologist`, which `jane` holds.

**Client `weather-bff`**, the BFF:

| Setting | Value |
|---------|-------|
| Client authentication | On (a confidential client) |
| Client authenticator | Signed JWT, signature algorithm ES256 |
| Keys | Use JWKS URL off; the certificate comes from the `BFF_CERTIFICATE` placeholder, which the AppHost sets |
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

## Project layout

```text
AppHost/                   Aspire AppHost
  AppHost.cs               The BFF's key pair; Keycloak, API, Vite, and BFF
  realms/demo-realm.json   The realm Keycloak imports at startup
Api/                       ASP.NET Core minimal API
  Program.cs               Token validation, role policy, GET /forecast
Bff/                       ASP.NET Core backend for frontend
  Program.cs               Cookie, OpenID Connect, CSP, CSRF check, endpoints, forwarder
  ClientAssertion.cs       The signed JWT that authenticates the BFF
  AccessTokens.cs          The session's access token, refreshed once per refresh token
ServiceDefaults/           Aspire service defaults: OpenTelemetry, health checks
spa/                       React, TypeScript, and Vite
  src/bff.ts               getUser() and apiGet(), with the X-CSRF header
  src/App.tsx              Sign in, Show forecast, Sign out
```

In `spa`, `npm run lint` runs [Oxlint](https://oxc.rs/docs/guide/usage/linter.html), and `npm run build` type-checks and builds the app.

## Beyond the demo

- **Serving the app.** In development, the BFF forwards every other request to the Vite dev server. Without the `SpaDevServer` setting, it serves the built app from `Bff/wwwroot` instead: run `npm run build` in `spa` and copy `spa/dist` there.
- **Data Protection keys.** The cookie is encrypted with ASP.NET Core Data Protection. Every BFF instance needs the same keys, persisted outside the process, and the same application name (`SetApplicationName`); otherwise users are signed out on every restart and on every other instance. By default, Data Protection isolates apps by their content root path: a clone of this repository in another folder could not read a cookie that the original had issued, though both used the same keys.
- **The client key.** Keep the private key in a key vault or a certificate store, and let Keycloak read the public key from a JWKS URL the BFF publishes, so keys can rotate without a realm change.
- **More than one instance.** The one-minute refresh cache lives in each process. Without rotation, a request on another instance only costs an extra refresh. With rotation, an old cookie that reaches another instance ends the session, so route each session to one instance or leave rotation off.
- **Rotating Keycloak's keys.** Keycloak signs with the active key of the highest priority. ASP.NET Core's handlers fetch new keys only when a token names one they don't have, and they fail that request: a key added at a higher priority made the BFF's next sign-in fail and the API refuse the next session's first token. Add a new key at a lower priority instead, let the BFF and the API fetch it (they refresh every 12 hours), and then raise its priority.

## Troubleshooting

- **Keycloak is served over `http://`, or sign-in fails:** the development certificate is missing or untrusted. Run `dotnet dev-certs https --check --trust`. If it finds no trusted certificate, run `dotnet dev-certs https --trust` and restart the AppHost.
- **A resource fails with an address already in use:** ports 3443, 3445, and 3446 must be free. react-webapi-oidc uses the same ports, so stop it first.
- **`keycloak` stays unhealthy:** make sure the container runtime is running, and read the resource's console logs in the dashboard.
- **Asked to sign in again after a restart:** each run imports a new realm with new keys, so a session from an earlier run ends at its next API call.
- **Sign-in fails with `IDX10503: Signature validation failed` after you restart only `keycloak`:** the realm came back with new signing keys that the running BFF and API haven't fetched. Each fetches them after the first token it can't verify, but at most once every 5 minutes. Restart the whole AppHost instead.
- **The BFF fails to start with `DirectoryNotFoundException` for `Bff\wwwroot`:** you built the BFF with a `wwwroot` folder and then removed it. Delete `Bff/obj` and `Bff/bin` and build again.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
