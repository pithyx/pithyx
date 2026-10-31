using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Pithyx.Daemon.Tests.Apps.Packages;

/// <summary>
/// Writes tar archives byte by byte, so a test can build exactly the malformed or hostile archive it wants: any type
/// flag, name, prefix, size field, magic, pax record or GNU long name, a wrong checksum, missing end blocks.
/// </summary>
internal sealed class TarBuilder
{
    private readonly List<byte> _tar = [];

    /// <summary>A header as written, changed by the test before its checksum is computed (unless it breaks that too).</summary>
    public sealed class Header
    {
        public byte[] Bytes { get; } = new byte[512];

        public void Set(int offset, int length, string value) => Set(offset, length, Encoding.UTF8.GetBytes(value));

        public void Set(int offset, int length, byte[] value)
        {
            Array.Clear(Bytes, offset, length);
            value.AsSpan(0, Math.Min(length, value.Length)).CopyTo(Bytes.AsSpan(offset));
        }
    }

    /// <summary>The header of a valid POSIX ustar entry.</summary>
    public static Header Ustar(string name, char type, long size, string mode = "0000644")
    {
        var header = new Header();
        header.Set(0, 100, name);
        header.Set(100, 8, mode + "\0");
        header.Set(108, 8, "0000000\0");
        header.Set(116, 8, "0000000\0");
        header.Set(124, 12, Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
        header.Set(136, 12, "14000000000\0");
        header.Bytes[156] = (byte)type;
        header.Set(257, 8, "ustar\000");
        header.Set(265, 32, "root");
        header.Set(297, 32, "root");
        return header;
    }

    public TarBuilder File(string name, string content, string mode = "0000644") => File(name, Encoding.UTF8.GetBytes(content), mode);

    public TarBuilder File(string name, byte[] content, string mode = "0000644") => Entry(Ustar(name, '0', content.Length, mode), content);

    public TarBuilder Folder(string name) => Entry(Ustar(name, '5', 0), []);

    /// <summary>An entry of any type flag with optional data, for links, devices and the like.</summary>
    public TarBuilder Typed(string name, char type, byte[]? data = null, string? link = null)
    {
        var header = Ustar(name, type, data?.Length ?? 0);
        if (link is not null)
        {
            header.Set(157, 100, link);
        }
        return Entry(header, data ?? []);
    }

    /// <summary>A pax extended header ('x', or 'g' for a global one) with the records in order.</summary>
    public TarBuilder Pax(params (string Key, string Value)[] records) => PaxTyped('x', records);

    public TarBuilder PaxTyped(char type, params (string Key, string Value)[] records)
    {
        var data = new MemoryStream();
        foreach (var (key, value) in records)
        {
            data.Write(PaxRecord(key, value));
        }
        return Entry(Ustar("PaxHeaders/entry", type, data.Length), data.ToArray());
    }

    /// <summary>One pax record with its self-counting length.</summary>
    public static byte[] PaxRecord(string key, string value)
    {
        var body = Encoding.UTF8.GetBytes($" {key}={value}\n");
        var length = body.Length + 1;
        while ((length.ToString(CultureInfo.InvariantCulture).Length + body.Length) != length)
        {
            length++;
        }
        return [.. Encoding.ASCII.GetBytes(length.ToString(CultureInfo.InvariantCulture)), .. body];
    }

    /// <summary>A GNU long name header ('L') for the next entry, as GNU tar writes it (name and a NUL).</summary>
    public TarBuilder GnuLongName(string name)
    {
        var data = Encoding.UTF8.GetBytes(name + "\0");
        var header = Ustar("././@LongLink", 'L', data.Length);
        header.Set(257, 8, "ustar  \0");
        return Entry(header, data);
    }

    /// <summary>Writes a header (its checksum computed unless <paramref name="checksum"/> is given) and padded data.</summary>
    public TarBuilder Entry(Header header, byte[] data, string? checksum = null)
    {
        header.Set(148, 8, "        ");
        var sum = header.Bytes.Sum(value => (long)value);
        header.Set(148, 8, checksum ?? Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
        _tar.AddRange(header.Bytes);
        _tar.AddRange(data);
        var padding = (512 - (data.Length % 512)) % 512;
        _tar.AddRange(new byte[padding]);
        return this;
    }

    public TarBuilder Raw(byte[] bytes)
    {
        _tar.AddRange(bytes);
        return this;
    }

    /// <summary>The end marker (two zero blocks by default) and padding to a 10 KB record like GNU tar.</summary>
    public TarBuilder End(int zeroBlocks = 2, bool pad = true)
    {
        _tar.AddRange(new byte[512 * zeroBlocks]);
        if (pad && _tar.Count % 10240 != 0)
        {
            _tar.AddRange(new byte[10240 - (_tar.Count % 10240)]);
        }
        return this;
    }

    public byte[] Tar() => [.. _tar];

    public byte[] Gzip() => Compress(Tar());

    public static byte[] Compress(byte[] data)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    /// <summary>A minimal valid app package: manifest, icon and entry.</summary>
    public static TarBuilder MinimalPackage(string id = "com.example.probe", string version = "1.0.0", string? manifest = null) =>
        new TarBuilder()
            .File("pithyx.json", manifest ?? Manifest(id, version))
            .File("icon.svg", Icon)
            .Folder("frontend/")
            .File("frontend/index.html", "<!doctype html><title>Probe</title><script type=\"module\" src=\"./app.js\"></script>")
            .File("frontend/app.js", "console.log('probe');");

    public const string Icon = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" fill=\"#08f\"/></svg>";

    /// <summary>A backend's image reference as the test packages write it.</summary>
    public const string BackendImage = "ghcr.io/example/web:1.0.0@sha256:a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5a3f5";

    /// <summary>The manifest part of a backend with the endpoint api on the service web.</summary>
    public const string BackendManifest = ",\n  \"backend\": { \"compose\": \"backend/compose.yaml\", \"endpoints\": { \"api\": { \"service\": \"web\", \"port\": 8080 } }, \"lifecycle\": \"onDemand\" }";

    /// <summary>A Compose file with the one service web.</summary>
    public const string BackendCompose = "services:\n  web:\n    image: " + BackendImage + "\n";

    /// <summary>An images lock for <see cref="BackendImage"/> on both architectures.</summary>
    public static string BackendLock(string image = BackendImage) =>
        $$"""
        {
          "lockVersion": 1,
          "images": {
            "{{image}}": {
              "amd64": { "manifest": "sha256:{{new string('b', 64)}}", "config": "sha256:{{new string('c', 64)}}" },
              "arm64": { "manifest": "sha256:{{new string('d', 64)}}", "config": "sha256:{{new string('e', 64)}}" }
            }
          }
        }
        """;

    /// <summary>A package with a backend: the manifest, the Compose file and, unless left out, the lock.</summary>
    public static TarBuilder BackendPackage(string id = "com.example.probe", string version = "1.0.0", string? lockJson = null, bool withLock = true)
    {
        var builder = MinimalPackage(id, version, Manifest(id, version, extra: BackendManifest)).File("backend/compose.yaml", BackendCompose);
        return withLock ? builder.File("backend/images.lock.json", lockJson ?? BackendLock()) : builder;
    }

    /// <summary>
    /// A manifest of version 2 whose <c>permissions</c> map has an entry with a reason for every name of
    /// <paramref name="permissions"/> (quoted names, comma-separated, elevated rights too), <c>optional</c> on the devices
    /// of <paramref name="optionalDevices"/>.
    /// </summary>
    public static string Manifest(string id = "com.example.probe", string version = "1.0.0", string permissions = "\"notifications\"", string extra = "", string optionalDevices = "")
    {
        var optional = JsonSerializer.Deserialize<string[]>($"[{optionalDevices}]")!;
        var map = string.Join(", ", JsonSerializer.Deserialize<string[]>($"[{permissions}]")!.Select(name =>
            $$"""{{JsonSerializer.Serialize(name)}}: { "reason": { "en": "Needed by the probe." }{{(name.StartsWith("device:", StringComparison.Ordinal) && optional.Contains(name["device:".Length..]) ? ", \"optional\": true" : "")}} }"""));
        return $$"""
            {
              "manifestVersion": 2,
              "id": "{{id}}",
              "version": "{{version}}",
              "name": { "en": "Probe", "de": "Sonde" },
              "author": { "name": "Example" },
              "entry": "frontend/index.html",
              "sdk": "^1.0.0",
              "arch": ["amd64", "arm64"],
              "source": "private",
              "permissions": { {{map}} },
              "window": { "instances": "single", "defaultSize": { "width": 800, "height": 600 }, "minSize": { "width": 320, "height": 240 } }{{extra}}
            }
            """;
    }
}
