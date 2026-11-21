using System.Globalization;
using System.Text.Json;
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
    private long? _syncedAt;
    private bool _stale;
    private string? _source;
    private long _pushedAtTick;

    public long Version { get { lock (_lock) return _version; } }

    /// <summary>When the phone was last read successfully, or null if it never was.</summary>
    public long? SyncedAt { get { lock (_lock) return _syncedAt; } }

    /// <summary>When a snapshot pushed by the phone's own listener last arrived (Environment.TickCount64); 0 = never.</summary>
    public long PushedAtTick { get { lock (_lock) return _pushedAtTick; } }

    /// <summary>
    /// The phone that pushed last, by its own device id (see <see cref="AgentLink"/>'s device header). Kept so a
    /// second phone with notification access cannot erase the rows the real source pushed.
    /// </summary>
    public string? Source { get { lock (_lock) return _source; } }

    /// <summary>
    /// True when the phone is not reachable right now. The rows, if any, are the last snapshot; with no rows it
    /// means "never read it", which the panel must not show as "connected, nothing to show".
    /// </summary>
    public bool Stale { get { lock (_lock) return _stale; } }

    public IReadOnlyList<MirroredNotification> Snapshot()
    {
        lock (_lock) return _items.Values.OrderByDescending(n => n.At).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Replaces the whole set with a fresh snapshot from the phone: the authoritative list.</summary>
    /// <returns>True when the panel has something new to draw.</returns>
    public bool ReplaceAll(IReadOnlyList<MirroredNotification> items, long at)
    {
        lock (_lock)
        {
            var next = items.OrderByDescending(n => n.At).ThenBy(n => n.Id, StringComparer.Ordinal).Take(MaxItems)
                .GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var same = next.Count == _items.Count
                && next.All(kv => _items.TryGetValue(kv.Key, out var old) && old == kv.Value);
            // coming back from stale is a visible change even when the list is identical
            var changed = !same || _stale;
            _syncedAt = at;
            _stale = false;
            if (!changed) return false;
            _items.Clear();
            foreach (var kv in next) _items[kv.Key] = kv.Value;
            _version++;
            return true;
        }
    }

    /// <summary>
    /// The phone is not there right now. Keep what was on screen and say it is old instead of emptying the
    /// card: a cable pulled for a minute must not look like "no notifications". An empty feed is marked too,
    /// so a panel that never reached the phone does not claim it has nothing to show.
    /// </summary>
    public void MarkStale()
    {
        lock (_lock)
        {
            if (_stale) return;
            _stale = true;
            _version++;
        }
    }

    /// <summary>
    /// A snapshot pushed by the phone's own notification listener over Wi-Fi (see <see cref="MirrorPush"/>), so the
    /// source phone needs no cable. An empty list from a phone that has never shown anything is ignored: a second
    /// phone with notification access must not erase the rows the real source pushed.
    /// </summary>
    /// <returns>True when the panel has something new to draw.</returns>
    public bool Pushed(IReadOnlyList<MirroredNotification> items, long at, string? device)
    {
        lock (_lock)
        {
            if (items.Count == 0 && _source is { } source && device is { Length: > 0 } id && source != id) return false;
            if (device is { Length: > 0 }) _source = device;
            _pushedAtTick = Environment.TickCount64;
            return ReplaceAll(items, at);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _source = null;
            _pushedAtTick = 0;
            if (_items.Count == 0 && !_stale) return;
            _items.Clear(); _stale = false; _version++;
        }
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
    internal const int MaxSender = 80;
    internal const int MaxText = 200;

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

            var sender = Unwrap(Field(block, "android.title=") ?? Field(block, "android.title.big=") ?? "");
            var text = Clean(Unwrap(Field(block, "android.text=") ?? Field(block, "android.bigText=") ?? ""), MaxText);
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

    /// <summary>
    /// dumpsys prints a CharSequence extra as its class name followed by the value in brackets, e.g.
    /// `String (Alice)`. Unwrap exactly that, once: real text keeps its own brackets (a message that is
    /// literally "String (x)" is stored wrapped twice and unwrapped once), and text without the wrapper is
    /// left untouched.
    /// </summary>
    private static string Unwrap(string value)
    {
        var m = CharSequenceDump().Match(value);
        return m.Success ? m.Groups[1].Value : value;
    }

    [GeneratedRegex(@"^(?:String|SpannableString|SpannedString|CharSequence|StringBuilder) \((.*)\)$", RegexOptions.Singleline)]
    private static partial Regex CharSequenceDump();

    /// <summary>One line, trimmed, and short enough for a panel row.</summary>
    internal static string Clean(string value, int max)
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
    /// <summary>The listener on the phone sends a heartbeat, so a silence this long means the phone is really gone.</summary>
    private const long PushFreshMs = 75_000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? usedSerial = null;
        var backoff = TimeSpan.FromSeconds(3);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var conf = cfg.Get().NotifyMirror;
            if (!conf.Enabled)
            {
                feed.Clear();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            // The phone's own listener pushes over Wi-Fi (no cable needed); while those pushes are fresh,
            // adb has nothing to add and must not mark the feed stale behind its back.
            var pushed = feed.PushedAtTick;
            if (pushed != 0 && Environment.TickCount64 - pushed < PushFreshMs)
            {
                usedSerial = null;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            var serial = Source(conf.Serial);
            if (serial is null)
            {
                // nothing to read from: keep the last snapshot, clearly marked, instead of emptying the card
                feed.MarkStale();
                usedSerial = null;
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            var adbPath = AdbReverse.FindAdb(cfg.Get().AdbPath);
            if (adbPath is null) { await Task.Delay(TimeSpan.FromSeconds(5), ct); continue; }

            try
            {
                var dump = await AdbReverse.Adb(adbPath, $"-s {serial} shell dumpsys notification --noredact", ct);
                if (feed.ReplaceAll(NotificationDump.Parse(dump), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    log.LogInformation("notification mirror: {Count} active from {Serial}", feed.Snapshot().Count, serial);
                if (usedSerial != serial) log.LogInformation("notification mirror: reading {Serial}", serial);
                usedSerial = serial;
                failures = 0;
                backoff = TimeSpan.FromSeconds(3);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogDebug("notification mirror: {Msg}", ex.Message);
                // one failed read is not a lost phone; two in a row is, as far as the panel is concerned
                if (++failures >= 2) feed.MarkStale();
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

/// <summary>
/// Reads the snapshot pushed by the phone's own notification listener over the paired link (NotificationRelay in
/// the app), so the mirrored apps need no cable. The phone may name the app; the package id is what is trusted, and
/// the display name comes from it.
/// </summary>
public static class MirrorPush
{
    private const int MaxBytes = 64 * 1024;
    private const int MaxId = 200;
    /// <summary>A phone with many active notifications still gets through: the feed keeps the newest 40.</summary>
    private const int MaxItemsInBody = 200;

    /// <summary>`{"items":[{id,package,sender,text,at}]}`; a record without a title or a body is skipped.</summary>
    public static (IReadOnlyList<MirroredNotification>? Items, string? Error) Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ([], null);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { return (null, "invalid JSON: " + ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (null, "expected {\"items\": [ ... ]}");
            if (arr.GetArrayLength() > MaxItemsInBody) return (null, $"more than {MaxItemsInBody} items");
            var list = new List<MirroredNotification>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var package = Str(e, "package");
                if (package is null || !NotificationDump.Allowed.TryGetValue(package, out var app)) continue;
                var id = NotificationDump.Clean(Str(e, "id") ?? "", MaxId);
                if (id.Length == 0) continue;
                var sender = NotificationDump.Clean(Str(e, "sender") ?? "", NotificationDump.MaxSender);
                var text = NotificationDump.Clean(Str(e, "text") ?? "", NotificationDump.MaxText);
                if (sender.Length == 0 && text.Length == 0) continue;
                var at = e.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var ms) && ms > 0
                    ? ms
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                list.Add(new MirroredNotification(id, app, package, sender, text, at));
            }
            return (list, null);
        }
    }

    /// <summary>Reads a request body with a hard size cap: Content-Length can be absent on a chunked request.</summary>
    public static async Task<(IReadOnlyList<MirroredNotification>? Items, string? Error)> ReadBody(HttpRequest req)
    {
        if (req.ContentLength > MaxBytes) return (null, TooLarge());
        var buf = new byte[MaxBytes + 1];
        var n = 0;
        while (n < buf.Length)
        {
            var count = await req.Body.ReadAsync(buf.AsMemory(n), req.HttpContext.RequestAborted);
            if (count == 0) break;
            n += count;
        }
        if (n > MaxBytes) return (null, TooLarge());
        return Parse(System.Text.Encoding.UTF8.GetString(buf, 0, n));
    }

    private static string TooLarge() => $"body too large (max {MaxBytes / 1024} KB)";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
