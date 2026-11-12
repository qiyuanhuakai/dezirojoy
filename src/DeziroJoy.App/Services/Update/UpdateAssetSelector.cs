namespace DeziroJoy.App.Services;

/// <summary>
/// The package this machine should install, plus the checksum sidecar that proves it. The
/// sidecar may be absent - the release workflow publishes one per package, but a release
/// assembled by hand might not - and the downloader refuses to install an unverified
/// package rather than silently trusting the transport.
/// </summary>
public sealed record UpdateAssetSelection(ReleaseAsset Asset, ReleaseAsset? ChecksumAsset, string PackageName);

/// <summary>
/// Maps an install form onto the asset a release publishes for it. The names are the ones
/// the release workflow produces, so a mismatch means either a wrong build or a release
/// that predates a format - both are reported instead of guessed at.
/// </summary>
public static class UpdateAssetSelector
{
    /// <summary>The suffix the checksum sidecar of a package carries.</summary>
    public static string ChecksumAssetName(string packageName) => packageName + ".sha256";

    /// <summary>
    /// The asset name this (rid, kind) pair needs, or null when the form ships no package at
    /// all - Flatpak and Portable have nothing to replace in place, and a Windows asset on a
    /// Linux RID (or the reverse) is a mismatch rather than a missing file.
    /// </summary>
    public static string? PackageAssetName(string rid, InstallKind kind, SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (!TrySplitRid(rid, out var os, out var architecture))
        {
            return null;
        }

        var text = version.ToString();

        // The operating system of the RID has to match the form as well as its architecture:
        // a Windows RID with a Linux package (or the reverse) is a mismatch, not a missing
        // file, and must not be reported as one.
        return (kind, os, architecture) switch
        {
            (InstallKind.WindowsInstall, "win", "x64") => $"DeziroJoy-{text}-win-x64-setup.exe",
            (InstallKind.WindowsInstall, "win", "arm64") => $"DeziroJoy-{text}-win-arm64-setup.exe",
            (InstallKind.Deb, "linux", "x64") => $"DeziroJoy_{text}_amd64.deb",
            (InstallKind.Deb, "linux", "arm64") => $"DeziroJoy_{text}_arm64.deb",
            (InstallKind.Rpm, "linux", "x64") => $"DeziroJoy-{text}-x86_64.rpm",
            (InstallKind.Rpm, "linux", "arm64") => $"DeziroJoy-{text}-aarch64.rpm",
            (InstallKind.AppImage, "linux", "x64") => $"DeziroJoy-{text}-x86_64.AppImage",
            (InstallKind.AppImage, "linux", "arm64") => $"DeziroJoy-{text}-aarch64.AppImage",
            _ => null,
        };
    }

    /// <summary>
    /// Picks the package - and its checksum sidecar - out of a release. Returns null both
    /// when the form has no package and when the release does not carry one; the caller
    /// tells the two apart with <see cref="PackageAssetName"/>, because the message differs:
    /// one is "this form cannot be updated in place", the other is "this release has no
    /// build for your machine".
    /// </summary>
    public static UpdateAssetSelection? TrySelect(ReleaseInfo release, InstallKindInfo install)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(install);

        var name = PackageAssetName(install.Rid, install.Kind, release.Version);
        if (name is null)
        {
            return null;
        }

        var assets = release.Assets;
        var asset = assets.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            return null;
        }

        var checksum = assets.FirstOrDefault(candidate => string.Equals(
            candidate.Name,
            ChecksumAssetName(name),
            StringComparison.OrdinalIgnoreCase));

        // The selection names the file that was actually published, because that is what
        // lands on disk and what an installer has to name on a command line.
        return new UpdateAssetSelection(asset, checksum, asset.Name);
    }

    /// <summary>
    /// Splits a runtime identifier into its operating system and the architecture this product
    /// builds for. A RID without a recognized architecture - or without an architecture at all
    /// - yields false rather than a wrong guess: the update surface then says it cannot pick a
    /// package, which is true.
    /// </summary>
    private static bool TrySplitRid(string? rid, out string os, out string architecture)
    {
        os = string.Empty;
        architecture = string.Empty;

        if (string.IsNullOrWhiteSpace(rid))
        {
            return false;
        }

        var separator = rid.LastIndexOf('-');
        if (separator <= 0 || separator == rid.Length - 1)
        {
            return false;
        }

        architecture = rid[(separator + 1)..] switch
        {
            "x64" or "amd64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => string.Empty,
        };

        if (architecture.Length == 0)
        {
            return false;
        }

        os = rid[..separator];
        return true;
    }
}
