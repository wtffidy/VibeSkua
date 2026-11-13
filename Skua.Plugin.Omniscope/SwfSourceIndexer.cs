using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Represents one searchable object or source entry in the OMNISCOPE developer catalog.
/// </summary>
internal sealed class GameDataRecord
{
    public string Category { get; init; } = string.Empty;
    public string DisplayCategory => Category switch
    {
        "CLIENT CORE" => "Client Core",
        "NETWORK / API" => "Network / API",
        "GAME UI" => "Game UI",
        "WORLD / MAP" => "World / Map",
        "PLAYER / AVATAR" => "Player / Avatar",
        "INVENTORY / ITEMS" => "Inventory / Items",
        "COMBAT / SKILLS" => "Combat / Skills",
        "GAME SYSTEMS" => "Game Systems",
        "OTHER" => "Other Client Code",
        "MAP SHOP" => "Map Shop",
        "MAP ITEM" => "Map Item",
        _ => Category
    };
    public string Name { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Metadata { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public int SourceFunctionCount { get; init; }
    public int? SourceShopId { get; init; }
    public int? SourceQuestId { get; init; }
    public string? SourceMapName { get; init; }
    public object? SourceObject { get; init; }
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
                SearchText = $"{category} {name} {relativePath} {summary} {source}",
                SourceFunctionCount = functionCount
            });
        }

        return records;
    }

    private static string ClassifySource(string relativePath, string source)
    {
        string fileName = Path.GetFileNameWithoutExtension(relativePath);
        string searchable = $"{fileName} {relativePath} {source}";

        if (ContainsAny(searchable, "socket", "packet", "gateway", "network", "connection", "request", "response", "sendto", "recvfrom", "geturl"))
            return "NETWORK / API";

        if (ContainsAny(searchable, "mcinterface", "interface", "textfield", "button", "panel", "window", "popup", "tooltip", "dialog", "movieclip", "hud", "frame"))
            return "GAME UI";

        if (ContainsAny(searchable, "map", "world", "room", "cell", "zone", "location", "teleport", "spawn"))
            return "WORLD / MAP";

        if (ContainsAny(searchable, "avatar", "player", "character", "username", "guild", "health", "mana", "stats"))
            return "PLAYER / AVATAR";

        if (ContainsAny(searchable, "inventory", "item", "shop", "bank", "drop", "reward", "quest", "buy", "sell"))
            return "INVENTORY / ITEMS";

        if (ContainsAny(searchable, "combat", "skill", "attack", "target", "battle", "damage", "cooldown", "aggro", "monster"))
            return "COMBAT / SKILLS";

        if (ContainsAny(searchable, "loader", "bootstrap", "config", "client", "main", "root", "initializ", "startup"))
            return "CLIENT CORE";

        if (ContainsAny(searchable, "faction", "achievement", "reputation", "house", "enhancement", "pet", "class"))
            return "GAME SYSTEMS";

        return "OTHER";
    }

    private static bool ContainsAny(string source, params string[] terms) =>
        terms.Any(term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
}

}

/// <summary>
/// Describes the result of exporting and indexing the client SWF.}

/// <summary>
/// Describes the result of exporting and indexing the client SWF.
/// </summary>
internal sealed record SwfSourceIndexResult(string ExportDirectory, IReadOnlyList<GameDataRecord> Records);
