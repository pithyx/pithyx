using System.Text.Json;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Tests.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Catalog;

public sealed class CatalogValidatorTests
{
    public static TheoryData<string> ValidCatalogs => Corpus.Files("catalog", "catalogs", "valid");

    public static TheoryData<string> InvalidCatalogs => Corpus.Files("catalog", "catalogs", "invalid");

    public static TheoryData<string> ValidIndexes => Corpus.Files("catalog", "index", "valid");

    public static TheoryData<string> InvalidIndexes => Corpus.Files("catalog", "index", "invalid");

    private static CatalogValidationResult ValidateCatalog(string kind, string file)
    {
        var allowReservedId = Corpus.Json("catalog", "catalogs", "options.json").TryGetProperty($"{kind}/{file}", out var options)
            && options.TryGetProperty("allowReservedId", out var allow)
            && allow.GetBoolean();
        return CatalogValidator.ValidateCatalog(Corpus.Read("catalog", "catalogs", kind, file), allowReservedId);
    }

    private static CatalogValidationResult ValidateIndex(string kind, string file)
    {
        var expectation = new IndexExpectation("org.pithyx.official", "stable");
        if (Corpus.Json("catalog", "index", "options.json").TryGetProperty($"{kind}/{file}", out var options))
        {
            expectation = new IndexExpectation(
                options.TryGetProperty("catalog", out var catalog) ? catalog.GetString()! : expectation.Catalog,
                options.TryGetProperty("channel", out var channel) ? channel.GetString()! : expectation.Channel,
                options.TryGetProperty("knownSequence", out var known) ? known.GetInt64() : null);
        }
        return CatalogValidator.ValidateIndex(Corpus.Read("catalog", "index", kind, file), expectation);
    }

    private static JsonElement.ArrayEnumerator Expected(string area, string file)
    {
        var expected = Corpus.Json("catalog", area, "expected.json");
        Assert.True(expected.TryGetProperty(file, out var issues), $"catalog/{area}/expected.json lacks {file}");
        return issues.EnumerateArray();
    }

    private static void AssertEveryExpectationHasAFixture(string area, TheoryData<string> invalid) =>
        Assert.Equal(
            invalid.Select(row => row.Data),
            Corpus.Json("catalog", area, "expected.json").EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(ValidCatalogs))]
    public void ValidCatalogsPass(string file)
    {
        var result = ValidateCatalog("valid", file);

        Assert.Empty(Corpus.Describe(result.Issues));
        Assert.NotNull(result.Document);
    }

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public void InvalidCatalogsFailWithExactlyTheExpectedIssues(string file)
    {
        var result = ValidateCatalog("invalid", file);

        Assert.Null(result.Document);
        Corpus.AssertExactly(result.Issues, Expected("catalogs", file));
    }

    [Fact]
    public void EveryCatalogExpectationHasAFixture() => AssertEveryExpectationHasAFixture("catalogs", InvalidCatalogs);

    [Theory]
    [MemberData(nameof(ValidIndexes))]
    public void ValidIndexesPass(string file)
    {
        var result = ValidateIndex("valid", file);

        Assert.Empty(Corpus.Describe(result.Issues));
        Assert.NotNull(result.Document);
    }

    [Theory]
    [MemberData(nameof(InvalidIndexes))]
    public void InvalidIndexesFailWithExactlyTheExpectedIssues(string file)
    {
        var result = ValidateIndex("invalid", file);

        Assert.Null(result.Document);
        Corpus.AssertExactly(result.Issues, Expected("index", file));
    }

    [Fact]
    public void EveryIndexExpectationAndOptionHasAFixture()
    {
        AssertEveryExpectationHasAFixture("index", InvalidIndexes);
        Assert.All(Corpus.Json("catalog", "index", "options.json").EnumerateObject(), option =>
            Assert.True(File.Exists(Corpus.Directory("catalog", "index", option.Name)), option.Name));
    }
}
