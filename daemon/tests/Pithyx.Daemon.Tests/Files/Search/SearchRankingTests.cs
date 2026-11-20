using Pithyx.Daemon.Files.Search;

namespace Pithyx.Daemon.Tests.Files.Search;

public sealed class SearchRankingTests
{
    [Fact]
    public void ASearchIsSplitIntoLowerCaseComposedWordsWithoutRepeats()
    {
        // An accent written as a combining mark, as some clients write names, is composed.
        Assert.Equal(["café".Normalize(System.Text.NormalizationForm.FormC), "plan"], SearchRanking.Terms("  Café   PLAN plan "));
        Assert.Empty(SearchRanking.Terms("   "));
        Assert.Empty(SearchRanking.Terms(null));
        Assert.Equal(FileSearch.MaxTerms, SearchRanking.Terms(string.Join(' ', Enumerable.Range(0, 20).Select(i => $"w{i}"))).Count);
        Assert.Equal(FileSearch.MaxQueryLength, SearchRanking.Terms(new string('x', 500))[0].Length);
    }

    [Theory]
    [InlineData("Plan.md", "plan", 0)]
    [InlineData("plan", "plan", 0)]
    [InlineData("My Plan.txt", "my plan", 0)]
    [InlineData("planning.txt", "plan", 1)]
    [InlineData("2026 plan.pdf", "plan", 2)]
    [InlineData("draft_plan.md", "plan", 2)]
    [InlineData("explanation.pdf", "plan", 3)]
    [InlineData(".plan", "plan", 2)]
    public void ScoresTheWholeNameThenTheStartThenAWordThenAnyMatch(string name, string query, int score) =>
        Assert.Equal(score, SearchRanking.Score(name, SearchRanking.Terms(query)));
}
