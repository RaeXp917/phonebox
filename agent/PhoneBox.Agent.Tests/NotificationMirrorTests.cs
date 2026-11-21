using PhoneBox.Agent;
using Xunit;

/// <summary>The notification mirror: which packages are allowed, what a record yields, and the feed's lifecycle.</summary>
public class NotificationMirrorTests
{
    /// <summary>Shaped like the real `dumpsys notification` output: inline header fields, extras on their own lines.</summary>
    private const string Dump =
        "NotificationRecord(0x01: pkg=com.facebook.orca user=UserHandle{0} id=1 tag=null importance=3 key=0|com.facebook.orca|1|null|10123: Notification(channel=chat flags=0x10 when=1795283261261)\n" +
        "    extras={\n" +
        "        android.title=Alice\n" +
        "        android.text=See you at eight\n" +
        "        android.bigText=See you at eight, at the bar on the corner\n" +
        "    }\n" +
        "NotificationRecord(0x02: pkg=com.facebook.orca user=UserHandle{0} id=2 tag=null importance=2 key=0|com.facebook.orca|2|null|10123: Notification(channel=chat flags=0x200 when=1795283261262)\n" +
        "    extras={\n" +
        "        android.title=2 new messages\n" +
        "        android.text=Alice, Bob\n" +
        "    }\n" +
        "NotificationRecord(0x03: pkg=com.discord user=UserHandle{0} id=3 tag=null importance=3 key=0|com.discord|3|null|10124: Notification(channel=dm flags=0x10 when=1795283261263)\n" +
        "    extras={\n" +
        "        android.title=Bob\n" +
        "        android.text=ping\n" +
        "    }\n" +
        "NotificationRecord(0x04: pkg=app.myspoti.stable user=UserHandle{0} id=4 tag=null importance=2 key=0|app.myspoti.stable|4|null|10548: Notification(channel=play flags=0x10 when=1795283261264)\n" +
        "    extras={\n" +
        "        android.title=Not mirrored\n" +
        "        android.text=another app\n" +
        "    }\n" +
        "NotificationRecord(0x05: pkg=com.instagram.android user=UserHandle{0} id=5 tag=null importance=3 key=0|com.instagram.android|5|null|10125: Notification(channel=like flags=0x10 when=1795283261265)\n" +
        "    extras={\n" +
        "        android.title=dana\n" +
        "        android.text=liked your photo\n" +
        "    }\n";

    private static MirroredNotification Item(string id, string text, long at = 1) =>
        new(id, "Messenger", "com.facebook.orca", "Alice", text, at);

    [Fact]
    public void OnlyTheFourAllowedApplicationsAreRead()
    {
        var items = NotificationDump.Parse(Dump);
        Assert.Equal(["Messenger", "Discord", "Instagram"], items.Select(i => i.App));
        Assert.DoesNotContain(items, i => i.Package == "app.myspoti.stable");
    }

    [Fact]
    public void ARecordCarriesSenderPreviewAndTime()
    {
        var first = NotificationDump.Parse(Dump)[0];
        Assert.Equal("Alice", first.Sender);
        Assert.Equal("See you at eight", first.Text);
        Assert.Equal(1795283261261, first.At);
        Assert.Contains("com.facebook.orca", first.Id);
    }

    // The group summary repeats its children; mirroring it would show every message twice.
    [Fact]
    public void AGroupSummaryIsSkipped()
    {
        Assert.DoesNotContain(NotificationDump.Parse(Dump), i => i.Sender.StartsWith("2 new"));
    }

    [Fact]
    public void ThePreviewFallsBackToTheBigTextAndIsCapped()
    {
        const string longText = "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";
        var dump = "NotificationRecord(0x01: pkg=com.discord user=u id=1 tag=null key=0|com.discord|1|null|1: Notification(flags=0x10 when=5)\n" +
                   "    extras={\n" +
                   $"        android.bigText={longText}\n" +
                   "    }\n";
        var item = Assert.Single(NotificationDump.Parse(dump));
        Assert.True(item.Text.Length <= 200, "the preview is capped");
        Assert.Empty(item.Sender);
    }

    [Fact]
    public void GarbageIsIgnoredInsteadOfThrowing()
    {
        Assert.Empty(NotificationDump.Parse(""));
        Assert.Empty(NotificationDump.Parse("not a notification dump"));
        Assert.Empty(NotificationDump.Parse("NotificationRecord(0x01: pkg=com.discord key= broken)\n"));
    }

    // The phone's active list is the truth: a key that disappears is removed, the same key updates in place.
    [Fact]
    public void TheFeedFollowsAddUpdateAndRemove()
    {
        var feed = new MirrorFeed();
        Assert.True(feed.ReplaceAll([Item("a", "one"), Item("b", "two")]));
        Assert.Equal(2, feed.Snapshot().Count);

        var afterUpdate = feed.Version;
        Assert.True(feed.ReplaceAll([Item("a", "one, edited"), Item("b", "two")]));
        Assert.Equal(afterUpdate + 1, feed.Version);
        Assert.Equal(2, feed.Snapshot().Count);
        Assert.Equal("one, edited", feed.Snapshot().First(i => i.Id == "a").Text);

        Assert.True(feed.ReplaceAll([Item("b", "two")]));
        Assert.Single(feed.Snapshot());
        Assert.Equal("b", feed.Snapshot()[0].Id);
    }

    [Fact]
    public void AnUnchangedListDoesNotBumpTheVersion()
    {
        var feed = new MirrorFeed();
        feed.ReplaceAll([Item("a", "one")]);
        var v = feed.Version;
        Assert.False(feed.ReplaceAll([Item("a", "one")]));
        Assert.Equal(v, feed.Version);
    }

    [Fact]
    public void TheFeedIsBoundedAndKeepsTheNewest()
    {
        var feed = new MirrorFeed();
        var many = Enumerable.Range(0, MirrorFeed.MaxItems + 12).Select(i => Item("id" + i, "t" + i, i)).ToList();
        feed.ReplaceAll(many);
        Assert.Equal(MirrorFeed.MaxItems, feed.Snapshot().Count);
        Assert.Contains(feed.Snapshot(), i => i.Id == "id" + (MirrorFeed.MaxItems + 11));
        Assert.DoesNotContain(feed.Snapshot(), i => i.Id == "id0");
    }

    [Fact]
    public void ClearingEmptiesTheFeedAndBumpsOnce()
    {
        var feed = new MirrorFeed();
        feed.ReplaceAll([Item("a", "one")]);
        var v = feed.Version;
        feed.Clear();
        Assert.Empty(feed.Snapshot());
        Assert.Equal(v + 1, feed.Version);
        feed.Clear();
        Assert.Equal(v + 1, feed.Version);
    }

    /// <summary>One record with the given title and/or text, in the shape `dumpsys notification` really prints.</summary>
    private static string One(string? title = null, string? text = null, string? big = null)
    {
        var extras = "";
        if (title is not null) extras += $"        android.title={title}\n";
        if (text is not null) extras += $"        android.text={text}\n";
        if (big is not null) extras += $"        android.bigText={big}\n";
        return "NotificationRecord(0x01: pkg=com.facebook.orca user=u id=1 tag=null key=0|com.facebook.orca|1|null|1: Notification(flags=0x10 when=5)\n" +
               "    extras={\n" + extras + "    }\n";
    }

    // dumpsys wraps a CharSequence extra as `String (the text)`. That wrapper is serialization, not content.
    [Theory]
    [InlineData("String (Alice)", "Alice")]
    [InlineData("String (Sent you a voice message)", "Sent you a voice message")]
    [InlineData("String (Hi (there))", "Hi (there)")]
    [InlineData("String (String (x))", "String (x)")]
    [InlineData("SpannableString (Alice)", "Alice")]
    [InlineData("String theory", "String theory")]
    [InlineData("Note (draft)", "Note (draft)")]
    [InlineData("Alice", "Alice")]
    public void TheDumpsysCharSequenceWrapperIsUnwrappedOnce(string raw, string expected)
    {
        Assert.Equal(expected, NotificationDump.Parse(One(title: raw)).Single().Sender);
    }

    // Greek, accents and emoji must survive the dump and the parser unchanged (the bytes are UTF-8 end to end).
    [Theory]
    [InlineData("String (Καλημέρα σου)")]
    [InlineData("String (Ça va très bien)")]
    [InlineData("String (🎉🎊 party)")]
    [InlineData("String (👨‍👩‍👧‍👦 family)")]
    [InlineData("String (🇬🇷 σημαία)")]
    public void NonAsciiTextIsPreservedExactly(string raw)
    {
        var inner = raw["String (".Length..^1];
        var item = NotificationDump.Parse(One(title: raw, text: raw)).Single();
        Assert.Equal(inner, item.Sender);
        Assert.Equal(inner, item.Text);
    }

    [Fact]
    public void AnUpdateKeepsTheSameIdentityAndReplacesTheText()
    {
        var feed = new MirrorFeed();
        feed.ReplaceAll(NotificationDump.Parse(One(title: "String (Alice)", text: "String (one)")));
        Assert.True(feed.ReplaceAll(NotificationDump.Parse(One(title: "String (Alice)", text: "String (two)"))));
        var item = Assert.Single(feed.Snapshot());
        Assert.Equal("Alice", item.Sender);
        Assert.Equal("two", item.Text);
        Assert.True(feed.ReplaceAll(NotificationDump.Parse("")), "a withdrawal empties the feed");
        Assert.Empty(feed.Snapshot());
    }
}
