using System.Net;
using System.Text;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// Which release the check picks, and what it carries. The selection is by semantic version
/// with <c>published_at</c> as a tie-break only: re-publishing an old tag later must not hide
/// the release that is actually newer, and a stale timestamp must not promote an older one.
/// </summary>
public sealed class UpdateServiceSelectionTests
{
    private const string Current = "0.0.1-beta.3-preview";

    private static UpdateService Service(HttpMessageHandler handler)
        => new(handler, TimeSpan.FromSeconds(10), Current);

    private static StubHandler Responds(string json) => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    [Fact]
    public async Task The_Highest_Semver_Wins_Even_When_An_Older_Release_Was_Published_Later()
    {
        // The old tag was re-published afterwards, so published_at would pick it: the newer
        // version must still win, or the update stays hidden behind a fresh timestamp.
        var handler = Responds(
            """
            [
              {"tag_name":"v0.0.1-beta.3-preview","published_at":"2026-03-01T12:00:00Z","html_url":"https://example.invalid/republished","draft":false,"prerelease":true},
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/newer","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v0.0.2-alpha.1", result.Latest!.TagName);
    }

    [Fact]
    public async Task Published_At_Only_Breaks_A_Tie()
    {
        // Two tags that parse to the same version: the one published later is the release the
        // user should be sent to.
        var handler = Responds(
            """
            [
              {"tag_name":"0.0.2","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/earlier","draft":false,"prerelease":false},
              {"tag_name":"v0.0.2","published_at":"2026-03-01T12:00:00Z","html_url":"https://example.invalid/later","draft":false,"prerelease":false}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v0.0.2", result.Latest!.TagName);
        Assert.Equal("https://example.invalid/later", result.Latest!.PageUrl);
    }

    [Fact]
    public async Task The_Selected_Release_Carries_Its_Assets()
    {
        var handler = Responds(
            """
            [
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/newer","draft":false,"prerelease":true,
               "assets":[
                 {"name":"DeziroJoy_0.0.2-alpha.1_amd64.deb","browser_download_url":"https://example.invalid/deb","size":4096},
                 {"name":"DeziroJoy_0.0.2-alpha.1_amd64.deb.sha256","browser_download_url":"https://example.invalid/deb-sha256","size":87},
                 {"name":"DeziroJoy-0.0.2-alpha.1-win-x64-setup.exe","browser_download_url":"https://example.invalid/setup","size":8192}
               ]}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        var assets = result.Latest!.Assets;
        Assert.Equal(3, assets.Count);

        var deb = Assert.Single(assets, asset => asset.Name == "DeziroJoy_0.0.2-alpha.1_amd64.deb");
        Assert.Equal("https://example.invalid/deb", deb.DownloadUrl);
        Assert.Equal(4096, deb.Size);

        Assert.Contains(assets, asset => asset.Name == "DeziroJoy_0.0.2-alpha.1_amd64.deb.sha256");
    }

    [Fact]
    public async Task A_Release_Without_Assets_Has_An_Empty_List()
    {
        var handler = Responds(
            """
            [
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/newer","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Latest!.Assets);
    }

    [Fact]
    public async Task An_Asset_Without_A_Name_Or_A_Download_Url_Is_Dropped()
    {
        // An unusable entry must not reach the selector as a package that cannot be fetched.
        var handler = Responds(
            """
            [
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/newer","draft":false,"prerelease":true,
               "assets":[
                 {"name":null,"browser_download_url":"https://example.invalid/noname","size":1},
                 {"name":"DeziroJoy_0.0.2-alpha.1_amd64.deb","browser_download_url":null,"size":1},
                 {"name":"DeziroJoy-0.0.2-alpha.1-win-x64-setup.exe","browser_download_url":"https://example.invalid/setup","size":8192}
               ]}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        var asset = Assert.Single(result.Latest!.Assets);
        Assert.Equal("DeziroJoy-0.0.2-alpha.1-win-x64-setup.exe", asset.Name);
    }

    [Fact]
    public async Task Drafts_And_Non_Semver_Tags_Are_Still_Skipped()
    {
        var handler = Responds(
            """
            [
              {"tag_name":"nightly","published_at":"2026-04-01T12:00:00Z","html_url":"https://example.invalid/nightly","draft":false,"prerelease":true},
              {"tag_name":"v9.9.9-draft","published_at":"2026-05-01T12:00:00Z","html_url":"https://example.invalid/draft","draft":true,"prerelease":true},
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/newer","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("v0.0.2-alpha.1", result.Latest!.TagName);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }
}
