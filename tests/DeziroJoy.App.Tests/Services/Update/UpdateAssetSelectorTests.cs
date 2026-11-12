using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The (rid, kind) to asset-name mapping and the selection of a package out of a release.
/// The names are the ones the release workflow publishes, so a wrong guess here is a 404 at
/// download time - which is why every combination is pinned.
/// </summary>
public sealed class UpdateAssetSelectorTests
{
    private static ReleaseAsset Asset(string name, long size = 1024)
        => new() { Name = name, DownloadUrl = $"https://example.invalid/{name}", Size = size };

    private static ReleaseInfo Release(params ReleaseAsset[] assets)
        => new()
        {
            TagName = "v0.0.1",
            Version = SemanticVersion.Parse("0.0.1"),
            PageUrl = "https://example.invalid/release",
            Assets = assets,
        };

    [Theory]
    [InlineData("win-x64", InstallKind.WindowsInstall, "DeziroJoy-0.0.1-win-x64-setup.exe")]
    [InlineData("win-arm64", InstallKind.WindowsInstall, "DeziroJoy-0.0.1-win-arm64-setup.exe")]
    [InlineData("linux-x64", InstallKind.Deb, "DeziroJoy_0.0.1_amd64.deb")]
    [InlineData("linux-arm64", InstallKind.Deb, "DeziroJoy_0.0.1_arm64.deb")]
    [InlineData("linux-x64", InstallKind.Rpm, "DeziroJoy-0.0.1-x86_64.rpm")]
    [InlineData("linux-arm64", InstallKind.Rpm, "DeziroJoy-0.0.1-aarch64.rpm")]
    [InlineData("linux-x64", InstallKind.AppImage, "DeziroJoy-0.0.1-x86_64.AppImage")]
    [InlineData("linux-arm64", InstallKind.AppImage, "DeziroJoy-0.0.1-aarch64.AppImage")]
    public void The_Asset_Name_Follows_The_Release_Workflow(string rid, InstallKind kind, string expected)
    {
        var name = UpdateAssetSelector.PackageAssetName(rid, kind, SemanticVersion.Parse("0.0.1"));

        Assert.Equal(expected, name);
    }

    [Fact]
    public void A_Prerelease_Version_Numbers_The_Asset_As_The_Workflow_Does()
    {
        var name = UpdateAssetSelector.PackageAssetName(
            "linux-x64",
            InstallKind.Deb,
            SemanticVersion.Parse("0.0.1-beta.5-preview"));

        Assert.Equal("DeziroJoy_0.0.1-beta.5-preview_amd64.deb", name);
    }

    [Theory]
    [InlineData("linux-x64", InstallKind.Flatpak)]
    [InlineData("linux-x64", InstallKind.Portable)]
    [InlineData("win-x64", InstallKind.Deb)]
    [InlineData("linux-x64", InstallKind.WindowsInstall)]
    public void A_Form_Without_A_Package_For_That_Rid_Has_No_Asset_Name(string rid, InstallKind kind)
    {
        Assert.Null(UpdateAssetSelector.PackageAssetName(rid, kind, SemanticVersion.Parse("0.0.1")));
    }

    [Theory]
    [InlineData("any")]
    [InlineData("linux-riscv64")]
    [InlineData("linux-x64-extra")]
    [InlineData("")]
    public void An_Unrecognized_Architecture_Selects_Nothing(string rid)
    {
        Assert.Null(UpdateAssetSelector.PackageAssetName(rid, InstallKind.Deb, SemanticVersion.Parse("0.0.1")));
    }

    [Fact]
    public void The_Selection_Picks_The_Package_And_Its_Sidecar()
    {
        var release = Release(
            Asset("DeziroJoy-0.0.1-source.tar.gz"),
            Asset("DeziroJoy_0.0.1_amd64.deb", 4096),
            Asset("DeziroJoy_0.0.1_amd64.deb.sha256", 87),
            Asset("DeziroJoy-0.0.1-win-x64-setup.exe"));

        var selection = UpdateAssetSelector.TrySelect(
            release,
            new InstallKindInfo(InstallKind.Deb, "linux-x64"));

        Assert.NotNull(selection);
        Assert.Equal("DeziroJoy_0.0.1_amd64.deb", selection!.PackageName);
        Assert.Equal(4096, selection.Asset.Size);
        Assert.Equal("https://example.invalid/DeziroJoy_0.0.1_amd64.deb", selection.Asset.DownloadUrl);
        Assert.NotNull(selection.ChecksumAsset);
        Assert.Equal("DeziroJoy_0.0.1_amd64.deb.sha256", selection.ChecksumAsset!.Name);
    }

    [Fact]
    public void The_Asset_Name_Match_Ignores_Case()
    {
        // A release assembled by hand may carry ".appimage" instead of ".AppImage": the file
        // is still the package, so the comparison is case-insensitive.
        var release = Release(Asset("dezirojoy-0.0.1-x86_64.appimage"), Asset("dezirojoy-0.0.1-x86_64.appimage.sha256"));

        var selection = UpdateAssetSelector.TrySelect(
            release,
            new InstallKindInfo(InstallKind.AppImage, "linux-x64"));

        Assert.NotNull(selection);
        Assert.Equal("dezirojoy-0.0.1-x86_64.appimage", selection!.PackageName);
    }

    [Fact]
    public void A_Release_Without_The_Package_Selects_Nothing()
    {
        var release = Release(Asset("DeziroJoy_0.0.1_arm64.deb"));

        var selection = UpdateAssetSelector.TrySelect(
            release,
            new InstallKindInfo(InstallKind.Deb, "linux-x64"));

        Assert.Null(selection);
    }

    [Fact]
    public void A_Release_Without_A_Sidecar_Still_Selects_The_Package()
    {
        // The downloader refuses an unverified package; the selector's job is only to find
        // what the release published.
        var release = Release(Asset("DeziroJoy_0.0.1_amd64.deb"));

        var selection = UpdateAssetSelector.TrySelect(
            release,
            new InstallKindInfo(InstallKind.Deb, "linux-x64"));

        Assert.NotNull(selection);
        Assert.Null(selection!.ChecksumAsset);
    }

    [Fact]
    public void A_Form_Without_Any_Package_Selects_Nothing()
    {
        var release = Release(Asset("DeziroJoy-0.0.1-source.tar.gz"));

        var selection = UpdateAssetSelector.TrySelect(
            release,
            new InstallKindInfo(InstallKind.Flatpak, "linux-x64"));

        Assert.Null(selection);
    }

    [Fact]
    public void The_Sidecar_Name_Is_The_Package_Name_Plus_The_Suffix()
    {
        Assert.Equal(
            "DeziroJoy_0.0.1_amd64.deb.sha256",
            UpdateAssetSelector.ChecksumAssetName("DeziroJoy_0.0.1_amd64.deb"));
    }

    [Fact]
    public void An_Empty_Asset_List_Selects_Nothing()
    {
        var selection = UpdateAssetSelector.TrySelect(
            Release(),
            new InstallKindInfo(InstallKind.Deb, "linux-x64"));

        Assert.Null(selection);
    }
}
