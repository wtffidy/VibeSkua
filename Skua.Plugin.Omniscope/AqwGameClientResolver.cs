using System.Net.Http;
using System.Text.Json;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Resolves and downloads the live AQW game client SWF using the same game-version endpoint as the Flash client.
/// </summary>
internal static class AqwGameClientResolver
{
    private const string GameRoot = "https://game.aq.com/game/";
    private const long MaximumSwfBytes = 128L * 1024 * 1024;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    /// <summary>
    /// Downloads the current game client SWF using the sFile value published by AQW.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel metadata retrieval and download.</param>
    /// <returns>The downloaded SWF path and the source filename reported by AQW.</returns>
    public static async Task<AqwGameClientFile> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage versionResponse = await HttpClient.GetAsync(
            new Uri(new Uri(GameRoot), "api/data/gameversion"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        versionResponse.EnsureSuccessStatusCode();

        await using Stream versionStream = await versionResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument versionDocument = await JsonDocument.ParseAsync(versionStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!versionDocument.RootElement.TryGetProperty("sFile", out JsonElement fileElement))
            throw new InvalidDataException("AQW's gameversion response did not contain an sFile entry.");

        string? fileName = fileElement.GetString();
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".swf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"AQW returned an unexpected game client filename: {fileName ?? "null"}");
        }

        Uri gameClientUri = new(new Uri(GameRoot), $"gamefiles/{Uri.EscapeDataString(fileName)}");
        using HttpResponseMessage swfResponse = await HttpClient.GetAsync(
            gameClientUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        swfResponse.EnsureSuccessStatusCode();

        long? contentLength = swfResponse.Content.Headers.ContentLength;
        if (contentLength is > MaximumSwfBytes)
            throw new InvalidDataException($"AQW game client SWF exceeds the {MaximumSwfBytes / 1024 / 1024:N0} MB safety limit.");

        string destinationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeSkua",
            "Omniscope",
            "GameClient");
        Directory.CreateDirectory(destinationDirectory);

        string destinationPath = Path.Combine(destinationDirectory, fileName);
        string temporaryPath = destinationPath + ".download";
        try
        {
            await using Stream source = await swfResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream destination = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[81920];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > MaximumSwfBytes)
                    throw new InvalidDataException($"AQW game client SWF exceeded the {MaximumSwfBytes / 1024 / 1024:N0} MB safety limit.");

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }

            if (totalBytes == 0)
                throw new InvalidDataException("AQW returned an empty game client SWF.");

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Close();
            File.Move(temporaryPath, destinationPath, true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the original download failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original download failure.
            }

            throw;
        }

        return new AqwGameClientFile(fileName, destinationPath, gameClientUri);
    }
}

/// <summary>
/// Describes the AQW game client SWF downloaded from the current game-version manifest.
/// </summary>
internal sealed record AqwGameClientFile(string FileName, string LocalPath, Uri SourceUri);
