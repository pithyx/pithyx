using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Tests.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Catalog;

public sealed class CatalogRulesTests
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TheoryData<string> Cases =>
        [.. Corpus.Json("catalog", "rules", "cases.json").GetProperty("cases").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];

    /// <summary>The app folder every case starts from, by path relative to it.</summary>
    private static Dictionary<string, byte[]> Base()
    {
        var directory = Corpus.Directory("catalog", "rules", "base");
        return System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);
    }

    private static byte[] Bytes(JsonElement source)
    {
        if (source.TryGetProperty("text", out var text))
        {
            return Encoding.UTF8.GetBytes(text.GetString()!);
        }
        if (source.TryGetProperty("base64", out var base64))
        {
            return Convert.FromBase64String(base64.GetString()!);
        }
        if (source.TryGetProperty("fixture", out var fixture))
        {
            return Corpus.Read(fixture.GetString()!.Split('/'));
        }
        if (source.TryGetProperty("lines", out var lines))
        {
            var line = new string('a', lines.GetProperty("width").GetInt32()) + "\n";
            return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, lines.GetProperty("count").GetInt32())));
        }
        var bytes = source.GetProperty("bytes");
        var result = new byte[bytes.GetProperty("size").GetInt32()];
        Convert.FromHexString(bytes.GetProperty("hex").GetString()!).CopyTo(result, 0);
        return result;
    }

    private static List<string>? Strings(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var values) ? [.. values.EnumerateArray().Select(value => value.GetString()!)] : null;

    private static string Text(JsonElement entry, string name, string fallback) =>
        entry.TryGetProperty(name, out var value) ? value.GetString()! : fallback;

    [Theory]
    [MemberData(nameof(Cases))]
    public void PullRequestsGetTheExpectedVerdict(string name)
    {
        var entry = Corpus.Json("catalog", "rules", "cases.json").GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var tree = Base();
        if (entry.TryGetProperty("manifest", out var patch))
        {
            var manifest = JsonNode.Parse(tree["pithyx.json"])!.AsObject();
            foreach (var field in patch.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.Null)
                {
                    manifest.Remove(field.Name);
                }
                else
                {
                    manifest[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                }
            }
            tree["pithyx.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString(_indented) + "\n");
        }
        if (entry.TryGetProperty("files", out var overlay))
        {
            foreach (var file in overlay.EnumerateObject())
            {
                if (file.Value.ValueKind == JsonValueKind.Null)
                {
                    tree.Remove(file.Name);
                }
                else
                {
                    tree[file.Name] = Bytes(file.Value);
                }
            }
        }
        var root = $"apps/{Text(entry, "folder", "com.example.notes")}/";
        var files = tree.ToDictionary(file => root + file.Key, file => file.Value, StringComparer.Ordinal);

        var issues = CatalogRules.CheckPullRequest(new CatalogPullRequest(
            Text(entry, "catalog", "org.pithyx.community"),
            Text(entry, "author", "jan-dev"),
            [.. Strings(entry, "changed") ?? [.. files.Keys], .. Strings(entry, "alsoChanged") ?? []],
            files,
            Strings(entry, "published") ?? [],
            Strings(entry, "maintainers")));

        Corpus.AssertExactly(issues, entry.GetProperty("expected").EnumerateArray());
    }
}
