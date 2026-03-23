using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HomelabBackup.Core.Models;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Infrastructure;

public sealed class ArchiveService : IArchiveService
{
    private readonly ILogger<ArchiveService> _logger;

    public ArchiveService(ILogger<ArchiveService> logger)
    {
        _logger = logger;
    }

    public static CompressionLevel ParseCompressionLevel(string value) => value.ToLowerInvariant() switch
    {
        "optimal" => CompressionLevel.Optimal,
        "fastest" => CompressionLevel.Fastest,
        "no_compression" => CompressionLevel.NoCompression,
        _ => CompressionLevel.Optimal
    };

    public async Task<ArchiveResult> CreateArchiveAsync(
        string sourcePath,
        string outputZipPath,
        CompressionLevel level,
        IReadOnlyList<string> excludePatterns,
        IProgress<BackupProgressEvent>? progress = null,
        string? sourceName = null,
        CancellationToken ct = default)
    {
        sourceName ??= Path.GetFileName(sourcePath);

        // Scan phase
        _logger.LogInformation("Scanning {SourcePath} for files...", sourcePath);
        var files = ScanFiles(sourcePath, excludePatterns);
        _logger.LogInformation("Found {Count} files to archive", files.Count);

        progress?.Report(new BackupProgressEvent(
            sourceName, "", 0, files.Count, 0, BackupPhase.Scanning));

        // Compress phase
        var manifestEntries = new List<ManifestEntry>();
        long uncompressedTotal = 0;
        int processed = 0;

        var outputDir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await Task.Run(() =>
        {
            using var zipStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
                var fileInfo = new FileInfo(filePath);
                var fileSize = fileInfo.Length;
                uncompressedTotal += fileSize;

                progress?.Report(new BackupProgressEvent(
                    sourceName, relativePath, processed, files.Count, uncompressedTotal, BackupPhase.Compressing));

                var entry = archive.CreateEntry(relativePath, level);

                string sha256Hash;
                using (var entryStream = entry.Open())
                using (var fileStream = File.OpenRead(filePath))
                using (var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        entryStream.Write(buffer, 0, bytesRead);
                        hashAlgorithm.AppendData(buffer, 0, bytesRead);
                    }
                    sha256Hash = Convert.ToHexString(hashAlgorithm.GetHashAndReset()).ToLowerInvariant();
                }

                manifestEntries.Add(new ManifestEntry(relativePath, fileSize, sha256Hash));
                processed++;
            }
        }, ct);

        var compressedSize = new FileInfo(outputZipPath).Length;
        _logger.LogInformation("Archive created: {ZipPath} ({Compressed:N0} bytes, {Ratio:P1} ratio)",
            outputZipPath, compressedSize, uncompressedTotal > 0 ? (double)compressedSize / uncompressedTotal : 0);

        return new ArchiveResult(outputZipPath, uncompressedTotal, compressedSize, manifestEntries);
    }

    public Task ExtractArchiveAsync(string zipPath, string destinationPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var fullDestination = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(fullDestination);

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                    continue; // Skip directory entries

                var entryDestination = Path.GetFullPath(Path.Combine(fullDestination, entry.FullName));
                if (!entryDestination.StartsWith(fullDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !entryDestination.Equals(fullDestination, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Zip entry '{entry.FullName}' would extract outside the destination directory. Aborting.");
                }

                var entryDir = Path.GetDirectoryName(entryDestination);
                if (!string.IsNullOrEmpty(entryDir))
                    Directory.CreateDirectory(entryDir);

                entry.ExtractToFile(entryDestination, overwrite: true);
            }

            _logger.LogInformation("Extracted {ZipPath} → {Destination}", zipPath, fullDestination);
        }, ct);
    }

    private static List<string> ScanFiles(string rootPath, IReadOnlyList<string> excludePatterns)
    {
        if (!Directory.Exists(rootPath))
            return [];

        var regexPatterns = excludePatterns
            .Select(GlobToRegex)
            .ToList();

        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            var fileName = Path.GetFileName(file);

            bool excluded = false;
            foreach (var pattern in regexPatterns)
            {
                if (pattern.IsMatch(relativePath) || pattern.IsMatch(fileName))
                {
                    excluded = true;
                    break;
                }
            }

            if (!excluded)
                files.Add(file);
        }

        return files;
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1));
    }
}
