namespace DeziroJoy.App.Services;

/// <summary>
/// One downloadable file of a GitHub release. The releases list endpoint already carries
/// these; the release check simply never looked at them until the update flow needed a
/// package to install rather than a page to visit.
/// </summary>
public sealed record ReleaseAsset
{
    /// <summary>The file name as published, e.g. <c>DeziroJoy_0.0.1_amd64.deb</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The direct download URL GitHub serves the bytes from.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>The size in bytes, or 0 when the API omitted it.</summary>
    public long Size { get; init; }
}
