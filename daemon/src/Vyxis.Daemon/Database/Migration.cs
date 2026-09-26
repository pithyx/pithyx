namespace Vyxis.Daemon.Database;

/// <param name="Version">Number from the file name prefix, for example 1 for <c>0001_users_sessions.sql</c>.</param>
/// <param name="Name">The rest of the file name.</param>
/// <param name="Sql">Statements run in one transaction.</param>
public sealed record Migration(int Version, string Name, string Sql)
{
    private const string ResourcePrefix = "migrations/";

    /// <summary>The migrations embedded from <c>Database/Migrations/NNNN_name.sql</c>, ordered by version.</summary>
    public static IReadOnlyList<Migration> Embedded()
    {
        var assembly = typeof(Migration).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                var fileName = Path.GetFileNameWithoutExtension(name[ResourcePrefix.Length..]);
                var separator = fileName.IndexOf('_', StringComparison.Ordinal);
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return new Migration(int.Parse(fileName[..separator], System.Globalization.CultureInfo.InvariantCulture), fileName[(separator + 1)..], reader.ReadToEnd());
            })
            .OrderBy(migration => migration.Version)];
    }
}
