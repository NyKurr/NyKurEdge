using NyKurEdge.Core.Media;

namespace NyKurEdge.Core.Tests.Media;

[TestClass]
public sealed class MediaSourceNameFormatterTests
{
    [TestMethod]
    [DataRow("Spotify.exe", "SPOTIFY")]
    [DataRow(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "GOOGLE CHROME")]
    [DataRow("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "SPOTIFY")]
    [DataRow("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEDGE", "MICROSOFT EDGE")]
    [DataRow("", "WINDOWS MEDIA")]
    public void ProducesAReadableSourceLabel(string sourceAppId, string expected)
    {
        Assert.AreEqual(expected, MediaSourceNameFormatter.Format(sourceAppId));
    }
}
