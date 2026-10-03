using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>Users and grants written straight into a test database, and share grants through the API.</summary>
public static class TestUsers
{
    /// <summary>
    /// Inserts a user that may sign in to the desktop, with the administrator right if asked, and returns its id.
    /// </summary>
    public static async Task<long> InsertAsync(DbConnection connection, string username, bool admin = false, string hash = "x", long? id = null, long? uid = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (uid is { } allocated)
        {
            await connection.ExecuteAsync("INSERT OR IGNORE INTO id_allocations (id, kind, name, allocated_at) VALUES (@allocated, 'user', @username, 'x')", new { allocated, username });
        }
        var userId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO users (id, username, password_hash, uid, created_at, updated_at) VALUES (@id, @username, @hash, @uid, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
            new { id, username, hash, uid });
        await GrantAsync(connection, userId, "right:desktopLogin");
        if (admin)
        {
            await GrantAsync(connection, userId, "right:admin");
        }
        return userId;
    }

    /// <summary>Gives a user a resource; shares take <c>read</c> or <c>readWrite</c>, everything else <c>allow</c>.</summary>
    public static Task GrantAsync(DbConnection connection, long userId, string resource, string level = "allow") =>
        connection.ExecuteAsync(
            "INSERT OR REPLACE INTO grants (subject_kind, subject_id, resource, level) VALUES ('user', @userId, @resource, @level)",
            new { userId, resource, level });

    /// <summary>Sets one user's access to a share through the API, keeping every other grant on it.</summary>
    /// <param name="admin">A client signed in as an administrator.</param>
    /// <param name="shareId">The share.</param>
    /// <param name="userId">The user.</param>
    /// <param name="access"><c>none</c>, <c>read</c> or <c>readWrite</c>.</param>
    public static async Task SetShareAccessAsync(this HttpClient admin, long shareId, long userId, string access)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var current = await admin.GetFromJsonAsync<JsonElement>($"/api/shares/{shareId}/grants");
        var grants = current.GetProperty("grants").EnumerateArray()
            .Where(grant => !(grant.GetProperty("subjectKind").GetString() == "user" && grant.GetProperty("subjectId").GetInt64() == userId))
            .Select(grant => (object)new { subjectKind = grant.GetProperty("subjectKind").GetString(), subjectId = grant.GetProperty("subjectId").GetInt64(), level = grant.GetProperty("level").GetString() })
            .ToList();
        if (access != "none")
        {
            grants.Add(new { subjectKind = "user", subjectId = userId, level = access });
        }
        using var response = await admin.PutAsJsonAsync($"/api/shares/{shareId}/grants", new { grants });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
