using PhoneBox.Agent;
using Xunit;

/// <summary>Which media sessions the panel may show: browsers and the players it names, nothing else.</summary>
public class MediaSourceTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", true)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", true)]
    [InlineData("Spotify.exe", true)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", true)]
    // what the owner saw: a torrent shell that registers a media session and names it after magnet-link text
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Stremio-Shell-NG\STREMIO-SHELL-NG.exe", false)]
    [InlineData("com.example.player", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyKnownBrowsersAndPlayersAreShown(string? id, bool expected) =>
        Assert.Equal(expected, MediaSources.Supported(id));
}
