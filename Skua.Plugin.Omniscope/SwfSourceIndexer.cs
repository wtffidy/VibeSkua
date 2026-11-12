using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Represents one searchable object or source entry in the OMNISCOPE developer catalog.
/// </summary>
internal sealed class GameDataRecord
{
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Metadata { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
}

/// <summary>
/// Exports and indexes ActionScript source from the packaged AQW client SWF using the bundled FFDec tool.
/// </summary>
internal static class SwfSourceIndexer
{
    private const int MaximumSourceFiles = 5000;
    private const long MaximumSourceBytes = 64L * 1024 * 1024;
    private const long MaximumFileBytes = 512L * 1024;
    private static readonly Regex ClassNameRegex = new(@"(?:public|internal|protected)?\s*(?:final\s+)?class\s+([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FunctionRegex = new(@"\bfunction\s+([A-Za-z0-9_]+)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Decompiles the supplied SWF when no matching export exists, then builds a bounded searchable source index.
    /// </summary>
    /// <param name="swfPath">Path to the packaged AQW client SWF.</param>
    /// <param name="ffdecDirectory">Directory containing the bundled FFDec command-line launcher.</param>
    /// <param name="cancellationToken">Token used to cancel the export operation.</param>
    /// <returns>The export directory and indexed ActionScript source records.</returns>
    public static async Task<SwfSourceIndexResult> DecompileAsync(string swfPath, string ffdecDirectory, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(swfPath))
            throw new FileNotFoundException("The packaged AQW client SWF could not be found.", swfPath);

        string ffdecPath = Path.Combine(ffdecDirectory, "ffdec.bat");
        if (!File.Exists(ffdecPath))
            throw new FileNotFoundException("The bundled FFDec launcher could not be found.", ffdecPath);

        FileInfo swfInfo = new(swfPath);
        string versionKey = $"{swfInfo.Length}_{swfInfo.LastWriteTimeUtc.Ticks}";
        string exportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeSkua",
            "Omniscope",
            "DecompiledClient",
            versionKey);
        string markerPath = Path.Combine(exportDirectory, ".omniscope-export-complete");
        Directory.CreateDirectory(exportDirectory);

        bool hasExportedScripts = Directory.EnumerateFiles(exportDirectory, "*.as", SearchOption.AllDirectories).Any();
        if (!File.Exists(markerPath) || !hasExportedScripts)
        {
            ProcessStartInfo startInfo = new("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ffdecDirectory
            };
            startInfo.Environment["FFDEC_MEMORY"] = "2048m";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"call \"{ffdecPath}\" -onerror ignore -export script \"{exportDirectory}\" \"{swfPath}\"");

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("FFDec could not be started.");

            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);

            string standardOutput = await standardOutputTask.ConfigureAwait(false);
            string standardError = await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string diagnostic = string.Join(Environment.NewLine, new[] { standardError, standardOutput }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                if (diagnostic.Length > 5000)
                    diagnostic = diagnostic[^5000..];

                throw new InvalidOperationException($"FFDec exited with code {process.ExitCode}.{Environment.NewLine}{diagnostic}");
            }

            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
        }

        List<GameDataRecord> records = await ReadSourceRecordsAsync(exportDirectory, cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
            throw new InvalidDataException("FFDec completed but did not export any ActionScript source files.");

        return new SwfSourceIndexResult(exportDirectory, records);
    }

    private static async Task<List<GameDataRecord>> ReadSourceRecordsAsync(string exportDirectory, CancellationToken cancellationToken)
    {
        List<GameDataRecord> records = new();
        long totalBytes = 0;

        foreach (string filePath in Directory.EnumerateFiles(exportDirectory, "*.as", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (records.Count >= MaximumSourceFiles)
                break;

            FileInfo fileInfo = new(filePath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaximumFileBytes || totalBytes + fileInfo.Length > MaximumSourceBytes)
                continue;

            string source;
            try
            {
                source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            totalBytes += fileInfo.Length;
            string relativePath = Path.GetRelativePath(exportDirectory, filePath);
            Match classMatch = ClassNameRegex.Match(source);
            string name = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);
            string category = ClassifySource(relativePath, source);
            int functionCount = FunctionRegex.Matches(source).Count;
            string[] matchingSignals = new[] { "achievement", "badge", "quest", "shop", "drop", "mapitem", "reward", "requirement" }
                .Where(signal => source.Contains(signal, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string summary = $"{functionCount:N0} functions  /  {fileInfo.Length:N0} bytes" +
                (matchingSignals.Length > 0 ? $"  /  {string.Join(", ", matchingSignals)}" : string.Empty);
            string details = $"CATEGORY: {category}{Environment.NewLine}" +
                $"SOURCE: {relativePath}{Environment.NewLine}" +
                $"SIZE: {fileInfo.Length:N0} bytes{Environment.NewLine}" +
                $"FUNCTIONS: {functionCount:N0}{Environment.NewLine}" +
                $"INDEXED: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                $"{Environment.NewLine}------------------------------------------------------------{Environment.NewLine}" +
                source;

            records.Add(new GameDataRecord
            {
                Category = category,
                Name = name,
                Summary = summary,
                Metadata = relativePath,
                Details = details,
                SearchText = $"{category} {name} {relativePath} {summary} {source}"
            });
        }

        return records;
    }

    private static string ClassifySource(string relativePath, string source)
    {
        string searchable = $"{relativePath} {source}";
        if (searchable.Contains("achievement", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("badge", StringComparison.OrdinalIgnoreCase))
            return "ACHIEVEMENT";

        if (searchable.Contains("quest", StringComparison.OrdinalIgnoreCase))
            return "QUEST SOURCE";

        if (searchable.Contains("shop", StringComparison.OrdinalIgnoreCase))
            return "SHOP SOURCE";

        if (searchable.Contains("mapitem", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("map item", StringComparison.OrdinalIgnoreCase))
            return "MAP SOURCE";

        if (searchable.Contains("drop", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("reward", StringComparison.OrdinalIgnoreCase))
            return "DROP SOURCE";

        return "SWF SOURCE";
    }
}

/// <summary>
/// Describes the result of exporting and indexing the client SWF.
/// </summary>
internal sealed record SwfSourceIndexResult(string ExportDirectory, IReadOnlyList<GameDataRecord> Records);
