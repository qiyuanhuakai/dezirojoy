using System.IO.Compression;

// Zips a directory into an archive, with the entries rooted at the directory's contents
// rather than at its name. The release workflow needs this on Linux, where the runner image
// carries no `zip` binary and the step would otherwise fail silently under `set -e`.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ZipPack <source-dir> <output-zip>");
    return 2;
}

var source = args[0];
var output = args[1];

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"source directory not found: {source}");
    return 2;
}

if (File.Exists(output))
{
    File.Delete(output);
}

var root = Path.GetFullPath(source);
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();

using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
{
    foreach (var file in files)
    {
        var relative = Path.GetRelativePath(root, file);
        // Zip entries always use forward slashes, whatever the host platform prefers.
        var entryName = relative.Replace('\\', '/');
        archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
    }
}

Console.WriteLine($"zipped {files.Count} files into {output} ({new FileInfo(output).Length} bytes)");
return 0;
