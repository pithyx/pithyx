using System.Text;
using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Files;

namespace Vyxis.Daemon.Tests.Files;

public sealed class NaturalSortTests
{
    private static string[] Sorted(params string[] names) =>
        [.. names.OrderBy(NaturalSort.Key, StringComparer.Ordinal)];

    [Fact]
    public void OrdersNumbersByValueAndIgnoresCase() =>
        Assert.Equal(
            ["file1.txt", "File2.txt", "file10.txt", "file010b.txt", "file100.txt", "image", "Image 2"],
            Sorted("file100.txt", "file10.txt", "Image 2", "File2.txt", "image", "file1.txt", "file010b.txt"));

    [Fact]
    public void PutsDigitsBeforeLettersAndKeepsEqualNamesApart()
    {
        Assert.Equal(["1 a", "a"], Sorted("a", "1 a"));
        Assert.NotEqual(NaturalSort.Key("A"), NaturalSort.Key("a"));
        Assert.NotEqual(NaturalSort.Key("7"), NaturalSort.Key("007"));
    }

    [Fact]
    public void HandlesVeryLongNumbers() =>
        Assert.Equal(["2", "99999999999999999999999"], Sorted("99999999999999999999999", "2"));

    [Fact]
    public void ComparesNamesExactlyLikeTheirKeys()
    {
        var random = new Random(7);
        const string Alphabet = "aAbB09_ .-ü10";
        var names = Enumerable.Range(0, 400)
            .Select(_ => new string([.. Enumerable.Range(0, random.Next(1, 8)).Select(_ => Alphabet[random.Next(Alphabet.Length)])]))
            .Concat(["", "0", "00", "a", "A", "a0", "a00", "a1", "a01"])
            .Where(name => name.Length > 0)
            .ToList();
        foreach (var a in names)
        {
            foreach (var b in names.Take(60))
            {
                var expected = Math.Sign(string.CompareOrdinal(NaturalSort.Key(a), NaturalSort.Key(b)));
                Assert.Equal(expected, Math.Sign(NaturalSort.Compare(a, b)));
                Assert.Equal(expected, Math.Sign(NaturalSort.CompareToKey(a, NaturalSort.Key(b))));
            }
        }
    }

    [Fact]
    public void RoundTripsCursorsAndRefusesForeignOnes()
    {
        var cursor = FolderCursor.Encode(FileSort.Size, SortOrder.Desc, "KEY", 42);
        Assert.Equal(("KEY", 42L), FolderCursor.Decode(cursor, FileSort.Size, SortOrder.Desc));
        Assert.Equal(ErrorCodes.FsCursorInvalid, Assert.Throws<VyxisException>(() => FolderCursor.Decode(cursor, FileSort.Name, SortOrder.Desc)).Code);
        Assert.Equal(ErrorCodes.FsCursorInvalid, Assert.Throws<VyxisException>(() => FolderCursor.Decode("not a cursor!", FileSort.Name, SortOrder.Asc)).Code);
        var garbage = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"x\":1}"));
        Assert.Equal(ErrorCodes.FsCursorInvalid, Assert.Throws<VyxisException>(() => FolderCursor.Decode(garbage, FileSort.Name, SortOrder.Asc)).Code);
    }
}
