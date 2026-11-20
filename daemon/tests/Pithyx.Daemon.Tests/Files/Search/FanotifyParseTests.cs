using Pithyx.Daemon.Files.Search;

namespace Pithyx.Daemon.Tests.Files.Search;

public sealed class FanotifyParseTests
{
    // Read on dev01 (Linux 6.12, OpenZFS 2.3.9) from a group with FAN_REPORT_DFID_NAME and a filesystem mark on a ZFS
    // dataset: a.txt created, the folder Ordner created and removed, b.txt moved into Ordner and deleted there. The
    // kernel merges events of one name, so each record carries several mask bits.
    private const string Recorded =
        "3c000000030018004001000000000000ffffffff4cb70c0002002400d01605c00691418208000000010000000200020059fb7e86612e7478740000003c000000"
        + "030018000003004000000000ffffffff4cb70c0002002400d01605c00691418208000000010000000200020059fb7e864f72646e657200003c000000030018"
        + "008002000000000000ffffffff4cb70c0002002400d01605c006914182080000000100000004000200af9e63ce622e747874000000";

    [Fact]
    public void EachEventNamesTheFileSystemAndTheDirectoryOfTheChangedEntry()
    {
        var events = Fanotify.Parse(Convert.FromHexString(Recorded));

        Assert.Equal(3, events.Count);
        Assert.All(events, change => Assert.Equal((0x82419106c00516d0UL, false), (change.Fsid, change.Overflow)));
        var root = Convert.FromHexString("08000000010000000200020059fb7e86");
        Assert.Equal(root, events[0].Directory);
        Assert.Equal(root, events[1].Directory);
        Assert.Equal(Convert.FromHexString("080000000100000004000200af9e63ce"), events[2].Directory);
    }

    [Fact]
    public void AQueueOverflowIsReportedAndATruncatedRecordEndsTheBuffer()
    {
        // fanotify_event_metadata of FAN_Q_OVERFLOW: no info records, fd FAN_NOFD.
        var overflow = Convert.FromHexString("18000000030018000040000000000000ffffffff00000000");
        var buffer = overflow.Concat(Convert.FromHexString(Recorded)[..40]).ToArray();

        var events = Fanotify.Parse(buffer);

        Assert.Equal([true], events.Select(change => change.Overflow));
    }

    [Fact]
    public void AnInfoRecordWithAHandleLongerThanItselfIsSkipped()
    {
        var bytes = Convert.FromHexString(Recorded[..120]);
        bytes[36] = 0xFF;

        Assert.Empty(Fanotify.Parse(bytes));
    }
}
