using System.Net;
using System.Text.RegularExpressions;

// An HTTP client that keeps cookies and signs in through Keycloak's
// login form, as a browser would. SameSite and cookie prefixes are
// browser rules; a browser test checks those.
public sealed partial class Browser : IDisposable
{
    public static readonly Uri Bff = new("https://localhost:3446");

    private readonly HttpClient http;

    public Browser()
    {
        http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = Cookies,
            AllowAutoRedirect = false,
        });
    }

    public CookieContainer Cookies { get; } = new();

    // The Set-Cookie headers of the sign-in callback.
    public IReadOnlyList<string> SessionCookies { get; private set; } = [];

    // The Cookie header that this browser sends to the BFF.
    public string CookieHeader => Cookies.GetCookieHeader(Bff);

    public async Task SignInAsync(string username)
    {
        // /bff/login leads to Keycloak's login page.
        var page = await FollowAsync(new Uri(Bff, "/bff/login"));
        var (action, _) = Form(page);
        var response = await http.PostAsync(action, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = "password",
            }));
        // Keycloak answers with a form that posts the code to the BFF.
        var (callback, fields) = Form(
            await response.Content.ReadAsStringAsync());
        response = await http.PostAsync(
            callback, new FormUrlEncodedContent(fields));
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        SessionCookies = [.. response.Headers.GetValues("Set-Cookie")];
    }

    public Task<HttpResponseMessage> GetAsync(string path, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Bff, path));
        if (csrf) request.Headers.Add("X-CSRF", "1");
        return http.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
        http.SendAsync(request);

    // Follows redirects, including to Keycloak, and returns the page.
    public async Task<string> FollowAsync(Uri url)
    {
        while (true)
        {
            using var response = await http.GetAsync(url);
            if (response.Headers.Location is not { } location)
            {
                return await response.Content.ReadAsStringAsync();
            }
            url = new Uri(url, location);
        }
    }

    // Sends a request with a given Cookie header, as a browser
    // that has not yet seen a newer cookie would.
    public static async Task<HttpResponseMessage> SendWithCookieAsync(
        string cookieHeader, string path)
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
        });
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Bff, path));
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add("Cookie", cookieHeader);
        return await client.SendAsync(request);
    }

    private static (Uri Action, Dictionary<string, string> Fields) Form(string html)
    {
        var action = FormAction().Match(html).Groups[1].Value;
        var fields = HiddenInput().Matches(html).ToDictionary(
            m => WebUtility.HtmlDecode(m.Groups[1].Value),
            m => WebUtility.HtmlDecode(m.Groups[2].Value));
        return (new Uri(WebUtility.HtmlDecode(action)), fields);
    }

    [GeneratedRegex("<form[^>]*action=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex FormAction();

    [GeneratedRegex("<input[^>]*name=\"([^\"]+)\"[^>]*value=\"([^\"]*)\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();

    public void Dispose() => http.Dispose();
}
