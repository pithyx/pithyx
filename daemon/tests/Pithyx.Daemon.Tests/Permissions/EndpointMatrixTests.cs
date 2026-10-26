using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Permissions;

/// <summary>
/// Every endpoint against every role (step 11): anonymous, a user with nothing but the desktop, a user with the right
/// to restart, and an administrator. The table names what each endpoint needs; a new endpoint fails here until it is
/// added, so nothing reaches the API without a decision about who may call it.
/// </summary>
public sealed partial class EndpointMatrixTests : IDisposable
{
    private enum Need
    {
        Anonymous,
        AnyUser,
        Power,
        Admin,

        /// <summary>The daemon's API for apps: a host token only; a session cookie is refused for every role.</summary>
        AppToken,
    }

    private static readonly Dictionary<string, Need> _table = new(StringComparer.Ordinal)
    {
        ["GetHealth"] = Need.Anonymous,
        ["GetSetupStatus"] = Need.Anonymous,
        ["GetInstallId"] = Need.Anonymous,
        ["Setup"] = Need.Anonymous,
        ["Login"] = Need.Anonymous,
        ["LoginTotp"] = Need.Anonymous,
        ["Recover"] = Need.Anonymous,
        ["Logout"] = Need.Anonymous,
        ["PasskeySignInOptions"] = Need.Anonymous,
        ["PasskeySignIn"] = Need.Anonymous,

        ["GetSession"] = Need.AnyUser,
        ["EnrollTotp"] = Need.AnyUser,
        ["ConfirmTotp"] = Need.AnyUser,
        ["DisableTotp"] = Need.AnyUser,
        ["ListPasskeys"] = Need.AnyUser,
        ["PasskeyCreateOptions"] = Need.AnyUser,
        ["CreatePasskey"] = Need.AnyUser,
        ["DeletePasskey"] = Need.AnyUser,
        ["GetDesktop"] = Need.AnyUser,
        ["SaveDesktopLayout"] = Need.AnyUser,
        ["ListDesktopDevices"] = Need.AnyUser,
        ["CopyDesktopLayout"] = Need.AnyUser,
        ["SetWindowPreference"] = Need.AnyUser,
        ["SetDesktopAppearance"] = Need.AnyUser,
        ["SetFileDefault"] = Need.AnyUser,
        ["RemoveFileDefault"] = Need.AnyUser,
        ["SetStartupApps"] = Need.AnyUser,
        ["ListDesktopItems"] = Need.AnyUser,
        ["CreateDesktopItem"] = Need.AnyUser,
        ["ChangeDesktopItem"] = Need.AnyUser,
        ["DeleteDesktopItem"] = Need.AnyUser,
        ["ListJobs"] = Need.AnyUser,
        ["GetJob"] = Need.AnyUser,
        ["CancelJob"] = Need.AnyUser,
        ["ListNotifications"] = Need.AnyUser,
        ["ClearNotifications"] = Need.AnyUser,
        ["MarkNotificationsRead"] = Need.AnyUser,
        ["DeleteNotification"] = Need.AnyUser,
        ["CreateAppNotification"] = Need.AnyUser,
        ["SetNotificationLanguage"] = Need.AnyUser,
        ["ListNotificationChannels"] = Need.AnyUser,
        ["CreateNotificationChannel"] = Need.AnyUser,
        ["UpdateNotificationChannel"] = Need.AnyUser,
        ["DeleteNotificationChannel"] = Need.AnyUser,
        ["TestNotificationChannel"] = Need.AnyUser,
        ["ListShares"] = Need.AnyUser,
        ["StartFileOperation"] = Need.AnyUser,
        ["WriteAppLogs"] = Need.AnyUser,
        ["ListFolder"] = Need.AnyUser,
        ["GetFileEntry"] = Need.AnyUser,
        ["DownloadFile"] = Need.AnyUser,
        ["UploadFile"] = Need.AnyUser,
        ["CreateFolder"] = Need.AnyUser,
        ["ListTrash"] = Need.AnyUser,
        ["RestoreTrash"] = Need.AnyUser,
        ["DeleteTrashItems"] = Need.AnyUser,
        ["EmptyTrash"] = Need.AnyUser,
        ["ListFileVersions"] = Need.AnyUser,
        ["RestoreFileVersion"] = Need.AnyUser,
        ["RequestThumbnails"] = Need.AnyUser,
        ["GetThumbnail"] = Need.AnyUser,
        ["RenameFileEntry"] = Need.AnyUser,
        ["GetAppIcon"] = Need.AnyUser,
        ["CreateAppToken"] = Need.AnyUser,

        ["AppWriteLogs"] = Need.AppToken,
        ["AppNotify"] = Need.AppToken,
        ["AppListStorageKeys"] = Need.AppToken,
        ["AppGetStorageValue"] = Need.AppToken,
        ["AppSetStorageValue"] = Need.AppToken,
        ["AppDeleteStorageValue"] = Need.AppToken,
        ["AppListUsers"] = Need.AppToken,
        ["AppListGroups"] = Need.AppToken,
        ["AppListShares"] = Need.AppToken,
        ["AppListFolder"] = Need.AppToken,
        ["AppGetFileEntry"] = Need.AppToken,
        ["AppReadFile"] = Need.AppToken,
        ["AppWriteFile"] = Need.AppToken,
        ["AppCreateFolder"] = Need.AppToken,
        ["AppRenameFileEntry"] = Need.AppToken,
        ["AppStartFileOperation"] = Need.AppToken,
        ["AppListFileVersions"] = Need.AppToken,
        ["AppRestoreFileVersion"] = Need.AppToken,
        ["AppListTrash"] = Need.AppToken,
        ["AppRestoreTrash"] = Need.AppToken,
        ["AppDeleteTrashItems"] = Need.AppToken,
        ["AppEmptyTrash"] = Need.AppToken,
        ["AppHandFile"] = Need.AppToken,
        ["AppCreateFileUrl"] = Need.AppToken,
        ["AppCancelJob"] = Need.AppToken,
        ["AppReadFileByToken"] = Need.Anonymous,
        ["AppCreateTicket"] = Need.AppToken,

        ["PowerSystem"] = Need.Power,

        ["GetPermissionCatalog"] = Need.Admin,
        ["GetUserGrants"] = Need.Admin,
        ["SetUserGrants"] = Need.Admin,
        ["GetGroupGrants"] = Need.Admin,
        ["SetGroupGrants"] = Need.Admin,
        ["SetShareTrash"] = Need.Admin,
        ["CreateShare"] = Need.Admin,
        ["ListShareDetails"] = Need.Admin,
        ["UpdateShare"] = Need.Admin,
        ["GetShareGrants"] = Need.Admin,
        ["SetShareGrants"] = Need.Admin,
        ["SetShareRestorePoints"] = Need.Admin,
        ["PlanShareAction"] = Need.Admin,
        ["GetUsers"] = Need.Admin,
        ["CreateUser"] = Need.Admin,
        ["UpdateUser"] = Need.Admin,
        ["DeleteUser"] = Need.Admin,
        ["ResetUserPassword"] = Need.Admin,
        ["GetGroups"] = Need.Admin,
        ["CreateGroup"] = Need.Admin,
        ["UpdateGroup"] = Need.Admin,
        ["DeleteGroup"] = Need.Admin,
        ["GetHomes"] = Need.Admin,
        ["SetHomeSettings"] = Need.Admin,
        ["SetHomeQuota"] = Need.Admin,
        ["MoveHomes"] = Need.Admin,
        ["DeleteHome"] = Need.Admin,
        ["ReleaseHome"] = Need.Admin,
        ["GetFileServices"] = Need.Admin,
        ["UpdateFileServices"] = Need.Admin,
        ["GetPools"] = Need.Admin,
        ["GetStorageDisks"] = Need.Admin,
        ["ScanImportablePools"] = Need.Admin,
        ["PlanStorageAction"] = Need.Admin,
        ["RunStoragePlan"] = Need.Admin,
        ["CreatePoolKey"] = Need.Admin,
        ["UnlockPool"] = Need.Admin,
        ["ChangePoolKey"] = Need.Admin,
        ["RemovePoolKeyFromVault"] = Need.Admin,
        ["ListSnapshots"] = Need.Admin,
        ["CreateSnapshot"] = Need.Admin,
        ["HoldSnapshot"] = Need.Admin,
        ["ReleaseSnapshot"] = Need.Admin,
        ["GetSnapshotSchedules"] = Need.Admin,
        ["SetSnapshotSchedules"] = Need.Admin,
        ["GetStorageAlerts"] = Need.Admin,
        ["MuteStorageAlert"] = Need.Admin,
        ["UnmuteStorageAlert"] = Need.Admin,
        ["StartScrub"] = Need.Admin,
        ["StopScrub"] = Need.Admin,
        ["PauseScrub"] = Need.Admin,
        ["GetStorageSchedules"] = Need.Admin,
        ["SetStorageSchedule"] = Need.Admin,
        ["GetLogs"] = Need.Admin,
        ["GetLogSources"] = Need.Admin,
        ["ExportLogs"] = Need.Admin,
        ["GetLogSettings"] = Need.Admin,
        ["SetLogSettings"] = Need.Admin,
        ["GetSystemOverview"] = Need.Admin,
        ["GetSystemNetwork"] = Need.Admin,
        ["CheckNetworkConfig"] = Need.Admin,
        ["SetNetworkConfig"] = Need.Admin,
        ["GetSystemUpdates"] = Need.Admin,
        ["CheckSystemUpdates"] = Need.Admin,
        ["SetSystemUpdateSettings"] = Need.Admin,
        ["UploadSystemUpdate"] = Need.Admin,
        ["DiscardSystemUpdate"] = Need.Admin,
        ["InstallSystemUpdate"] = Need.Admin,
        ["SaveUpdateSourceCandidate"] = Need.Admin,
        ["DiscardUpdateSourceCandidate"] = Need.Admin,
        ["RollBackSystem"] = Need.Admin,
        ["ConfirmNetworkChange"] = Need.Admin,
        ["RevertNetworkChange"] = Need.Admin,
        ["GetSystemTime"] = Need.Admin,
        ["SetSystemTime"] = Need.Admin,
        ["GetSystemUps"] = Need.Admin,
        ["SetSystemUps"] = Need.Admin,
        ["ListSystemNotificationChannels"] = Need.Admin,
        ["CreateSystemNotificationChannel"] = Need.Admin,
        ["UpdateSystemNotificationChannel"] = Need.Admin,
        ["DeleteSystemNotificationChannel"] = Need.Admin,
        ["TestSystemNotificationChannel"] = Need.Admin,
        ["GetNotificationSmtp"] = Need.Admin,
        ["SetNotificationSmtp"] = Need.Admin,
        ["GetSystemRegion"] = Need.Admin,
        ["SetSystemRegion"] = Need.Admin,
        ["GetRootCertificate"] = Need.Anonymous,
        ["GetTlsSettings"] = Need.Admin,
        ["SetTlsNames"] = Need.Admin,
        ["CreateCertificateAuthority"] = Need.Admin,
        ["IssueCertificate"] = Need.Admin,
        ["GetIssuedCertificate"] = Need.Admin,
        ["DeleteIssuedCertificate"] = Need.Admin,
        ["SetCustomCertificate"] = Need.Admin,
        ["RemoveCustomCertificate"] = Need.Admin,
        ["CreateProxyTicket"] = Need.AnyUser,
        ["GetProxySettings"] = Need.Admin,
        ["SetProxyOptions"] = Need.Admin,
        ["CreateProxyHost"] = Need.Admin,
        ["UpdateProxyHost"] = Need.Admin,
        ["DeleteProxyHost"] = Need.Admin,
        ["CheckProxyTarget"] = Need.Admin,
        ["RenewProxyHostCertificate"] = Need.Admin,
        ["RenewProxyRedirectCertificate"] = Need.Admin,
        ["GetProxyAcme"] = Need.Admin,
        ["SetProxyAcme"] = Need.Admin,
        ["CreateProxyRedirect"] = Need.Admin,
        ["UpdateProxyRedirect"] = Need.Admin,
        ["DeleteProxyRedirect"] = Need.Admin,
        ["CreateProxyStream"] = Need.Admin,
        ["UpdateProxyStream"] = Need.Admin,
        ["DeleteProxyStream"] = Need.Admin,
        ["CreateConfigBackup"] = Need.Admin,
        ["InspectConfigBackup"] = Need.Admin,
        ["RestoreConfigBackup"] = Need.Admin,
        ["ListApps"] = Need.Admin,
        ["UploadAppPackage"] = Need.Admin,
        ["GetAppPackage"] = Need.Admin,
        ["DiscardAppPackage"] = Need.Admin,
        ["InstallAppPackage"] = Need.Admin,
        ["UpdateApp"] = Need.Admin,
        ["UninstallApp"] = Need.Admin,
        ["SetAppPermissions"] = Need.Admin,
        ["MapAppStorage"] = Need.Admin,
        ["UnmapAppStorage"] = Need.Admin,
        ["SetAppLogLevel"] = Need.Admin,
        ["RollbackApp"] = Need.Admin,
        ["SetAppUpdatePolicy"] = Need.Admin,
        ["GetStore"] = Need.Admin,
        ["SetStoreSettings"] = Need.Admin,
        ["CheckStore"] = Need.Admin,
        ["AddCatalog"] = Need.Admin,
        ["UpdateCatalog"] = Need.Admin,
        ["RemoveCatalog"] = Need.Admin,
        ["SyncCatalog"] = Need.Admin,
        ["SearchStoreApps"] = Need.Admin,
        ["GetStoreApp"] = Need.Admin,
        ["GetStoreAppIcon"] = Need.Admin,
        ["GetStoreAppScreenshot"] = Need.Admin,
        ["StageStoreApp"] = Need.Admin,
        ["GetAppGrants"] = Need.Admin,
        ["SetAppGrants"] = Need.Admin,
    };

    private readonly DaemonFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IReadOnlyList<(string Name, string Method, string Pattern, Need Declared)> Endpoints()
    {
        var source = _factory.Services.GetRequiredService<EndpointDataSource>();
        return [.. source.Endpoints.OfType<RouteEndpoint>()
            // The API reference and its document (development only) are not part of the API.
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true
                && !endpoint.RoutePattern.RawText.StartsWith("/api/docs", StringComparison.Ordinal)
                && endpoint.RoutePattern.RawText != "/api/openapi.json")
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method =>
            {
                var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();
                var declared = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null ? Need.Anonymous
                    : policies.Contains(Pithyx.Daemon.Auth.Policies.AppToken) ? Need.AppToken
                    : policies.Contains(Pithyx.Daemon.Auth.Policies.Admin) ? Need.Admin
                    : policies.Contains(Pithyx.Daemon.Auth.Policies.Power) ? Need.Power
                    : Need.AnyUser;
                return (endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? $"{method} {endpoint.RoutePattern.RawText}", method, endpoint.RoutePattern.RawText!, declared);
            }))];
    }

    [Fact]
    public void EveryEndpointCarriesThePolicyOfTheTable()
    {
        var endpoints = Endpoints();
        Assert.Equal(_table.Keys.Order(StringComparer.Ordinal), endpoints.Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal));
        Assert.All(endpoints, endpoint => Assert.True(_table[endpoint.Name] == endpoint.Declared, $"{endpoint.Name} declares {endpoint.Declared}, the table says {_table[endpoint.Name]}"));
    }

    [Fact]
    public async Task EveryEndpointRefusesEveryRoleWithoutTheRight()
    {
        using var setup = _factory.CreateApiClient();
        await setup.SetupAdminAsync();
        long plain, power;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            plain = await TestUsers.InsertAsync(connection, "plain");
            power = await TestUsers.InsertAsync(connection, "power");
            await TestUsers.GrantAsync(connection, power, "right:power");
        }
        var admin = await AdminIdAsync(setup);
        var problems = new List<string>();
        foreach (var (name, method, pattern, _) in Endpoints().OrderBy(endpoint => endpoint.Name, StringComparer.Ordinal))
        {
            var need = _table[name];
            if (need == Need.Anonymous)
            {
                continue;
            }
            var path = Sample(pattern);
            using (var anonymous = _factory.CreateApiClient())
            {
                Expect(problems, name, "anonymous", await SendAsync(anonymous, method, path), HttpStatusCode.Unauthorized);
            }
            foreach (var (role, userId, allowed) in new[] { ("plain user", plain, need == Need.AnyUser), ("power user", power, need is Need.AnyUser or Need.Power), ("administrator", admin, need != Need.AppToken) })
            {
                using var client = _factory.ClientWithCookie(await _factory.SessionCookieAsync(userId));
                var status = await SendAsync(client, method, path);
                if (need == Need.AppToken)
                {
                    // A session cookie never authenticates the API for apps, not even an administrator's.
                    Expect(problems, name, role, status, HttpStatusCode.Unauthorized);
                }
                else if (allowed)
                {
                    // The right is there: whatever the answer (not found, a bad body), it is not a refusal of the caller.
                    if (status is HttpStatusCode.Unauthorized || (status == HttpStatusCode.Forbidden && need != Need.AnyUser))
                    {
                        problems.Add($"{name} as {role}: {(int)status}");
                    }
                }
                else
                {
                    Expect(problems, name, role, status, HttpStatusCode.Forbidden);
                }
            }
        }
        Assert.Empty(problems);
    }

    private static void Expect(List<string> problems, string name, string role, HttpStatusCode status, HttpStatusCode expected)
    {
        if (status != expected)
        {
            problems.Add($"{name} as {role}: {(int)status}, expected {(int)expected}");
        }
    }

    private static async Task<long> AdminIdAsync(HttpClient admin)
    {
        var users = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<System.Text.Json.JsonElement>(admin, "/api/users", Token);
        return users[0].GetProperty("id").GetInt64();
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request, Token);
        return response.StatusCode;
    }

    /// <summary>The pattern with sample values: numbers for numeric parameters, a word for the rest.</summary>
    private static string Sample(string pattern) => Parameter().Replace(pattern, match => match.Value.Contains(":long", StringComparison.Ordinal) ? "4711" : "sample");

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Parameter();
}
