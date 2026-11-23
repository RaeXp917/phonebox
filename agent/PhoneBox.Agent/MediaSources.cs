namespace PhoneBox.Agent;

/// <summary>
/// The media sources the panel shows. Windows reports a media session for anything that registers one, including apps
/// that are not playback, so the widget reads only the browsers and players it knows; anything else is "no media"
/// instead of raw text the app happens to call a title.
/// </summary>
public static class MediaSources
{
    public static bool Supported(string? id)
    {
        var l = (id ?? "").ToLowerInvariant();
        return l.Contains("chrome") || l.Contains("chromium") || l.Contains("msedge") || l.Contains("brave")
            || l.Contains("opera") || l.Contains("vivaldi") || l.Contains("firefox") || l.Contains("308046b0af4a39cb")
            || l.Contains("spotify") || l.Contains("vlc") || l.Contains("zune") || l.Contains("wmplayer");
    }
}
