using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>One mirrored notification, as the panel shows it. Text is trimmed; nothing here is written to disk.</summary>
public sealed record MirroredNotification(string Id, string App, string Package, string Sender, string Text, long At);

/// <summary>
/// The active notifications of the monitored phone, keyed by Android's own notification key, so an update
/// replaces its item and a withdrawal removes it. In memory only: a private message does not outlive the run.
/// </summary>
public sealed class MirrorFeed
{
    public const int MaxItems = 40;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MirroredNotification> _items = new(StringComparer.Ordinal);
    private long _version;

    public long Version { get { lock (_lock) return _version; } }

    public IReadOnlyList<MirroredNotification> Snapshot()
    {
        lock (_lock) return _items.Values.OrderByDescending(n => n.At).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Replaces the whole set: the phone's active list is authoritative, so removals need no history.</summary>
    /// <returns>True when the panel has something new to draw.</returns>
    public bool ReplaceAll(IReadOnlyList<MirroredNotification> items)
    {
        lock (_lock)
        {
            var next = items.OrderByDescending(n => n.At).ThenBy(n => n.Id, StringComparer.Ordinal).Take(MaxItems)
                .GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (next.Count == _items.Count && next.All(kv => _items.TryGetValue(kv.Key, out var old) && old == kv.Value)) return false;
            _items.Clear();
            foreach (var kv in next) _items[kv.Key] = kv.Value;
            _version++;
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock) { if (_items.Count == 0) return; _items.Clear(); _version++; }
    }
}

/// <summary>Reads the active notifications out of `dumpsys notification` and keeps only the allow-listed apps.</summary>
public static partial class NotificationDump
{
    /// <summary>The only packages mirrored. A package id is the one thing trusted; everything else is ignored.</summary>
    public static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["com.facebook.orca"] = "Messenger",
        ["com.discord"] = "Discord",
        ["com.microsoft.teams"] = "Teams",
        ["com.instagram.android"] = "Instagram",
    };

    /// <summary>A summary is a container for its children, not a message; mirroring it would duplicate every row.</summary>
    private const int FlagGroupSummary = 0x200;
    private const int MaxSender = 80;
    private const int MaxText = 200;

    public static IReadOnlyList<MirroredNotification> Parse(string dump)
    {
        var list = new List<MirroredNotification>();
        if (string.IsNullOrEmpty(dump)) return list;
        foreach (var block in Records().Split(dump).Skip(1))
        {
            var package = Token(block, "pkg=");
            if (package is null || !Allowed.TryGetValue(package, out var app)) continue;
            var key = KeyOf(block);
            if (key is null) continue;
            if (FlagsOf(block) is { } flags && (flags & FlagGroupSummary) != 0) continue;

            var sender = Field(block, "android.title=") ?? Field(block, "android.title.big=") ?? "";
            var text = Clean(Field(block, "android.text=") ?? Field(block, "android.bigText=") ?? "", MaxText);
            // nothing to show: a record without a title or a body (a media-style notification, or a truncated
            // dump) would only add an empty row
            if (sender.Trim().Length == 0 && text.Length == 0) continue;
            var at = 0L;
            if (Field(block, "when=") is { } whenRaw)
            {
                // inline in the header, so the value runs to end of line and may end with ")" or another field
                var digits = new string(whenRaw.TakeWhile(char.IsDigit).ToArray());
                long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out at);
            }
            list.Add(new MirroredNotification(key, app, package, Clean(sender, MaxSender), text, at));
        }
        return list;
    }

    /// <summary>The first value for a field inside one record. Most fields sit inline in the header line
    /// (`pkg=… flags=0x… when=…`), the extras on their own lines, so the search is not line-anchored.</summary>
    private static string? Field(string block, string prefix)
    {
        var i = block.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + prefix.Length;
        var end = block.IndexOf('\n', start);
        return (end < 0 ? block[start..] : block[start..end]).Trim();
    }

    /// <summary>A field that is followed by more fields on the same line, e.g. `pkg=com.discord user=…`.</summary>
    private static string? Token(string block, string prefix)
    {
        var v = Field(block, prefix);
        if (v is null) return null;
        var space = v.IndexOf(' ');
        return space < 0 ? v : v[..space];
    }

    /// <summary>`key=0|com.discord|123|null|10123: Notification(...` — the part before the first colon.</summary>
    private static string? KeyOf(string block)
    {
        var v = Field(block, "key=");
        if (v is null) return null;
        var colon = v.IndexOf(": Notification", StringComparison.Ordinal);
        var key = (colon > 0 ? v[..colon] : v).Trim();
        return key.Length == 0 ? null : key;
    }

    private static int? FlagsOf(string block)
    {
        var v = Field(block, "flags=0x");
        return v is not null && int.TryParse(v.Split(' ')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) ? flags : null;
    }

    /// <summary>One line, trimmed, and short enough for a panel row.</summary>
    private static string Clean(string value, int max)
    {
        var s = Whitespace().Replace(value, " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Splits the dump at each record header; the first piece is the preamble.</summary>
    [GeneratedRegex(@"^ *NotificationRecord\(", RegexOptions.Multiline)]
    private static partial Regex Records();
}

/// <summary>
/// Copies the active notifications of the personal phone into <see cref="MirrorFeed"/> about twice a second,
/// so the panel follows the phone's own lifecycle: a new key appears, an updated key replaces its row, and a
/// key the phone no longer reports disappears. One-way: it only reads, never replies or dismisses.
/// </summary>
public sealed class NotificationMirror(ConfigStore cfg, AdbReverse adb, MirrorFeed feed, ILogger<NotificationMirror> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? usedSerial = null;
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            var conf = cfg.Get().NotifyMirror;
            if (!conf.Enabled)
            {
                feed.Clear();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var serial = Source(conf.Serial);
            if (serial is null)
            {
                // no phone to read from (or only the panel itself): forget what we had, so a later reconnect
                // starts from the phone's current list instead of replaying something dismissed meanwhile
                feed.Clear();
                usedSerial = null;
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            var adbPath = AdbReverse.FindAdb(cfg.Get().AdbPath);
            if (adbPath is null) { await Task.Delay(TimeSpan.FromSeconds(5), ct); continue; }

            try
            {
                var dump = await AdbReverse.Adb(adbPath, $"-s {serial} shell dumpsys notification --noredact", ct);
                if (feed.ReplaceAll(NotificationDump.Parse(dump)))
                    log.LogInformation("notification mirror: {Count} active from {Serial}", feed.Snapshot().Count, serial);
                if (usedSerial != serial) log.LogInformation("notification mirror: reading {Serial}", serial);
                usedSerial = serial;
                backoff = TimeSpan.FromSeconds(3);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogDebug("notification mirror: {Msg}", ex.Message);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(conf.PollSeconds, 1, 30)), ct);
        }
    }

    /// <summary>The phone to read: the configured serial, or the first attached device that is not the panel.</summary>
    private string? Source(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        var panel = cfg.Get().DeviceSerial;
        return adb.AllDevices.FirstOrDefault(d => !string.Equals(d, panel, StringComparison.Ordinal));
    }
}
