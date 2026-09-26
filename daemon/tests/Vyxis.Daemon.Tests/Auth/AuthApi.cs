using System.Net.Http.Json;
using System.Text.Json;

namespace Vyxis.Daemon.Tests.Auth;

/// <summary>Small helpers for calling the auth endpoints in tests.</summary>
internal static class AuthApi
{
    public const string AdminPassword = "correct horse battery";

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> PostAsync(this HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, Token);

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).Clone();

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    public static async Task<string> SetupAdminAsync(this HttpClient client, string username = "admin")
    {
        using var response = await client.PostAsync("/api/setup", new { username, password = AdminPassword });
        response.EnsureSuccessStatusCode();
        return (await response.JsonAsync()).GetProperty("recoveryKey").GetString()!;
    }

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string username, string password) =>
        await client.PostAsync("/api/auth/login", new { username, password });
}
