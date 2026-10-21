using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// Keycloak's admin API, for changing the realm during a test and
// reading the events it records.
public sealed class KeycloakAdmin(string password) : IDisposable
{
    private const string Realm = "https://localhost:3443/admin/realms/demo";
    private readonly HttpClient http = new();

    public async Task UpdateRealmAsync(object changes)
    {
        using var response = await SendAsync(HttpMethod.Put, Realm, changes);
        response.EnsureSuccessStatusCode();
    }

    // Starts recording token events, with none recorded yet.
    public async Task RecordEventsAsync()
    {
        await UpdateRealmAsync(new
        {
            eventsEnabled = true,
            enabledEventTypes = new[]
            {
                "LOGIN", "CODE_TO_TOKEN", "REFRESH_TOKEN",
                "REFRESH_TOKEN_ERROR", "LOGOUT",
            },
        });
        using var response = await SendAsync(
            HttpMethod.Delete, $"{Realm}/events");
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> CountEventsAsync(string type)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"{Realm}/events?type={type}&max=1000");
        var events = await response.Content.ReadFromJsonAsync<JsonElement>();
        return events.GetArrayLength();
    }

    // Ends all of the user's sessions, as an administrator would.
    public async Task SignOutEverywhereAsync(string username)
    {
        using var found = await SendAsync(
            HttpMethod.Get, $"{Realm}/users?exact=true&username={username}");
        var users = await found.Content.ReadFromJsonAsync<JsonElement>();
        var id = users[0].GetProperty("id").GetString();
        using var response = await SendAsync(
            HttpMethod.Post, $"{Realm}/users/{id}/logout");
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = new("Bearer", await TokenAsync());
        return await http.SendAsync(request);
    }

    // A new admin token per call: master-realm tokens last 60 seconds.
    private async Task<string> TokenAsync()
    {
        using var response = await http.PostAsync(
            "https://localhost:3443/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = "admin",
                ["password"] = password,
            }));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        return token.GetProperty("access_token").GetString()!;
    }

    public void Dispose() => http.Dispose();
}
