using System.Net;
using DeckWrench.Core.Compat;
using Xunit;

namespace DeckWrench.Core.Tests;

public class CompatReportsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deckwrench-compat-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private const string ProtonDb = """{"bestReportedTier":"platinum","confidence":"strong","score":0.76,"tier":"Gold","total":2140,"trendingTier":"platinum"}""";

    private const string Deck = """
        {"success":1,"results":{"appid":1245620,"resolved_category":2,"resolved_items":[
          {"display_type":3,"loc_token":"#SteamDeckVerified_TestResult_SomeInGameTextSmall"},
          {"display_type":4,"loc_token":"#SteamDeckVerified_TestResult_DefaultControllerConfigFullySupported"},
          {"display_type":3,"loc_token":"#SteamDeckVerified_TestResult_BrandNewThingToCheck"}]}}
        """;

    [Fact]
    public void ParsesProtonDbAndDeckReports()
    {
        var c = CompatReports.Parse("1245620", ProtonDb, Deck, DateTime.UtcNow);
        Assert.Equal("gold", c.Tier);
        Assert.Equal("platinum", c.Trending);
        Assert.Equal(2140, c.Reports);
        Assert.True(c.TierWorks);
        Assert.Equal(DeckRating.Playable, c.Deck);
        Assert.Equal(["Some text is small and hard to read", "Full controller support", "Brand new thing to check"], c.DeckNotes);
        Assert.Equal("Gold", CompatInfo.TierLabel(c.Tier));
    }

    [Fact]
    public void MissingOrBrokenReportsAreUnknown()
    {
        var c = CompatReports.Parse("1", null, "<html>", DateTime.UtcNow);
        Assert.Null(c.Tier);
        Assert.Equal(DeckRating.Unknown, c.Deck);
        Assert.Equal(DeckRating.Unknown, CompatReports.Parse("1", "{}", """{"success":1,"results":[]}""", DateTime.UtcNow).Deck);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task RefreshCachesAndKeepsGoodAnswersWhenOffline()
    {
        var online = true;
        var stub = new Stub(r => !online ? throw new HttpRequestException("offline")
            : r.RequestUri!.Host.Contains("protondb") ? (r.RequestUri.AbsolutePath.Contains("/7.json") ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage { Content = new StringContent(ProtonDb) })
            : new HttpResponseMessage { Content = new StringContent(Deck) });
        var file = Path.Combine(_dir, "ratings.json");
        var reports = new CompatReports(file, new HttpClient(stub));
        Assert.Equal(["1245620", "7"], reports.Stale(["1245620", "7", "7"], DateTime.UtcNow));

        Assert.Equal(2, await reports.Refresh(["1245620", "7"]));
        Assert.Equal("gold", reports.Get("1245620")!.Tier);
        Assert.Null(reports.Get("7")!.Tier);           // no ProtonDB reports isn't a failure
        Assert.False(reports.Get("7")!.Failed);
        Assert.Empty(reports.Stale(["1245620", "7"], DateTime.UtcNow));

        // A new instance reads the cache; going offline keeps the last good answer.
        var again = new CompatReports(file, new HttpClient(stub));
        Assert.Equal(DeckRating.Playable, again.Get("1245620")!.Deck);
        online = false;
        var later = DateTime.UtcNow + CompatReports.Fresh + TimeSpan.FromHours(1);
        Assert.Equal(["1245620"], again.Stale(["1245620"], later));
        await again.Refresh(["1245620"]);
        Assert.Equal("gold", again.Get("1245620")!.Tier);
        Assert.False(again.Get("1245620")!.Failed);
    }
}
