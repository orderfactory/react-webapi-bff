using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

[TestClass]
public sealed class BffTests
{
    [TestMethod]
    public async Task TheApiDecidesByRole()
    {
        using var jane = new Browser();
        await jane.SignInAsync("jane");
        using var forecast = await jane.GetAsync("/api/forecast");
        Assert.AreEqual(HttpStatusCode.OK, forecast.StatusCode);
        Assert.IsTrue((await forecast.Content.ReadAsStringAsync())
            .Contains("\"for\":\"jane\""));

        using var bob = new Browser();
        await bob.SignInAsync("bob");
        using var refused = await bob.GetAsync("/api/forecast");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [TestMethod]
    public async Task TheSessionCookieIsHostOnlyHttpOnlySecureAndStrict()
    {
        using var browser = new Browser();
        await browser.SignInAsync("jane");
        var session = browser.SessionCookies
            .Where(c => c.StartsWith("__Host-Http-bff"))
            .ToList();
        Assert.IsTrue(session.Count > 0);
        foreach (var cookie in session)
        {
            var attributes = cookie.ToLowerInvariant();
            Assert.IsTrue(attributes.Contains("; path=/;"), cookie);
            Assert.IsTrue(attributes.Contains("; secure"), cookie);
            Assert.IsTrue(attributes.Contains("; samesite=strict"), cookie);
            Assert.IsTrue(attributes.Contains("; httponly"), cookie);
            Assert.IsFalse(attributes.Contains("domain="), cookie);
        }
    }

    [TestMethod]
    public async Task TheUserEndpointReturnsOnlyTheName()
    {
        using var browser = new Browser();
        await browser.SignInAsync("jane");
        using var response = await browser.GetAsync("/bff/user");
        Assert.AreEqual(
            "{\"name\":\"jane\"}", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task CallsWithoutTheCsrfHeaderAreRefused()
    {
        using var browser = new Browser();
        await browser.SignInAsync("jane");
        foreach (var path in new[] { "/bff/user", "/api/forecast" })
        {
            using var response = await browser.GetAsync(path, csrf: false);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, path);
        }

        // A script on another origin needs a preflight, which fails.
        var preflight = new HttpRequestMessage(
            HttpMethod.Options, new Uri(Browser.Bff, "/api/forecast"));
        preflight.Headers.Add("Origin", "https://attacker.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-csrf");
        using var answer = await browser.SendAsync(preflight);
        Assert.AreEqual(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.IsFalse(answer.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task SignOutFromAnotherOriginIsRefused()
    {
        using var browser = new Browser();
        await browser.SignInAsync("jane");
        var post = new HttpRequestMessage(
            HttpMethod.Post, new Uri(Browser.Bff, "/bff/logout"));
        post.Headers.Add("Sec-Fetch-Site", "same-site");
        using var response = await browser.SendAsync(post);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        using var user = await browser.GetAsync("/bff/user");
        Assert.AreEqual(HttpStatusCode.OK, user.StatusCode);
    }

    [TestMethod]
    public async Task SignOutEndsTheSession()
    {
        using var browser = new Browser();
        await browser.SignInAsync("jane");
        var post = new HttpRequestMessage(
            HttpMethod.Post, new Uri(Browser.Bff, "/bff/logout"));
        post.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await browser.SendAsync(post);
        // Off to Keycloak's end-session endpoint and back.
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        await browser.FollowAsync(response.Headers.Location!);

        using var user = await browser.GetAsync("/bff/user");
        Assert.AreEqual(HttpStatusCode.Unauthorized, user.StatusCode);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "Without rotation")]
    [DataRow(true, DisplayName = "With rotation")]
    public async Task ParallelAndLateCallsShareOneRefresh(bool rotation)
    {
        // With 60-second tokens, every call finds a refresh due.
        await App.Admin.UpdateRealmAsync(new
        {
            accessTokenLifespan = 60,
            revokeRefreshToken = rotation,
            refreshTokenMaxReuse = 0,
        });
        await App.Admin.RecordEventsAsync();
        try
        {
            using var browser = new Browser();
            await browser.SignInAsync("jane");
            var cookie = browser.CookieHeader;

            var calls = Enumerable.Range(0, 20).Select(_ =>
                Browser.SendWithCookieAsync(cookie, "/api/forecast"));
            foreach (var response in await Task.WhenAll(calls))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            // A call that still sends the old cookie gets the same tokens.
            using var late = await Browser.SendWithCookieAsync(
                cookie, "/api/forecast");
            Assert.AreEqual(HttpStatusCode.OK, late.StatusCode);

            Assert.AreEqual(1, await App.Admin.CountEventsAsync("REFRESH_TOKEN"));
            Assert.AreEqual(0,
                await App.Admin.CountEventsAsync("REFRESH_TOKEN_ERROR"));
        }
        finally
        {
            await App.Admin.UpdateRealmAsync(new
            {
                accessTokenLifespan = 300,
                revokeRefreshToken = false,
            });
        }
    }

    [TestMethod]
    public async Task AnEndedSessionIsReportedAtTheNextCheck()
    {
        await App.Admin.UpdateRealmAsync(new { accessTokenLifespan = 60 });
        try
        {
            using var browser = new Browser();
            await browser.SignInAsync("bob");
            await App.Admin.SignOutEverywhereAsync("bob");

            using var user = await browser.GetAsync("/bff/user");
            Assert.AreEqual(HttpStatusCode.Unauthorized, user.StatusCode);
            Assert.IsFalse(browser.CookieHeader.Contains("__Host-Http-bff"));
        }
        finally
        {
            await App.Admin.UpdateRealmAsync(new { accessTokenLifespan = 300 });
        }
    }

    [TestMethod]
    public async Task EachSessionHasItsOwnRateLimit()
    {
        using var first = new Browser();
        await first.SignInAsync("bob");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 101; i++)
        {
            using var response = await first.GetAsync("/api/forecast");
            statuses.Add(response.StatusCode);
        }
        // bob lacks the role, so the API answers 403 until the limit.
        Assert.AreEqual(100, statuses.Count(s => s == HttpStatusCode.Forbidden));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, statuses[^1]);

        using var second = new Browser();
        await second.SignInAsync("bob");
        using var other = await second.GetAsync("/api/forecast");
        Assert.AreEqual(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [TestMethod]
    public async Task TheJwksHoldsOnlyPublicKeys()
    {
        // One key, or two after the key change test.
        foreach (var key in await PublishedKeysAsync())
        {
            Assert.AreEqual("EC", key.GetProperty("kty").GetString());
            Assert.AreEqual("P-256", key.GetProperty("crv").GetString());
            Assert.IsTrue(key.TryGetProperty("kid", out _));
            Assert.IsFalse(key.TryGetProperty("d", out _));
        }
    }

    [TestMethod]
    public async Task ANewClientKeyNeedsNoChangeInKeycloak()
    {
        // With 60-second tokens, the session's next call refreshes.
        await App.Admin.UpdateRealmAsync(new { accessTokenLifespan = 60 });
        try
        {
            using var before = new Browser();
            await before.SignInAsync("jane");
            var signedIn = Stopwatch.StartNew();

            // The BFF signs with a new key and still publishes the old one.
            using var oldKey = ECDsa.Create();
            oldKey.ImportFromPem(App.AppHostClientKey);
            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await App.RestartBffAsync(newKey.ExportPkcs8PrivateKeyPem(),
                oldKey.ExportSubjectPublicKeyInfoPem());
            Assert.AreEqual(2, (await PublishedKeysAsync()).Count);

            // Keycloak fetches a client's keys at most every 10 seconds.
            var wait = TimeSpan.FromSeconds(11) - signedIn.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            // The session from before the change refreshes with the new key.
            await App.Admin.RecordEventsAsync();
            using var user = await before.GetAsync("/bff/user");
            Assert.AreEqual(HttpStatusCode.OK, user.StatusCode);
            Assert.AreEqual(1, await App.Admin.CountEventsAsync("REFRESH_TOKEN"));
            Assert.AreEqual(0,
                await App.Admin.CountEventsAsync("REFRESH_TOKEN_ERROR"));

            // And a new sign-in works.
            using var after = new Browser();
            await after.SignInAsync("bob");
        }
        finally
        {
            await App.Admin.UpdateRealmAsync(new { accessTokenLifespan = 300 });
        }
    }

    [TestMethod]
    public async Task AFailedSignInReturnsToTheApp()
    {
        using var browser = new Browser();
        using var response = await browser.GetAsync(
            "/signin-oidc?code=forged&state=forged", csrf: false);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual(
            "/?signin-failed", response.Headers.Location?.OriginalString);
    }

    private static async Task<List<JsonElement>> PublishedKeysAsync()
    {
        using var browser = new Browser();
        using var response = await browser.GetAsync("/bff/jwks", csrf: false);
        var jwks = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. jwks.GetProperty("keys").EnumerateArray()];
    }
}
