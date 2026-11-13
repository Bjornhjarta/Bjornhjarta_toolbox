using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bjornhjarta.Core.Blueprints;

/// <summary>One user-curated placeable template. This is NOT evidence that a Type ID exists in Enshrouded.</summary>
public sealed class PlaceableCatalogEntry
{
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";
    public string Category { get; set; } = "Miscellaneous";
    public string Notes { get; set; } = "";
    public string Source { get; set; } = "User entry";

    [JsonIgnore]
    public bool IsExample => TypeId.StartsWith("example.", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public string StatusLabel => IsExample ? "EXAMPLE · NOT A GAME ID" : "USER-SUPPLIED · NOT VERIFIED";
    [JsonIgnore]
    public bool IsUserEntry { get; set; }
}

public sealed class PlaceablesCatalogFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<PlaceableCatalogEntry> Entries { get; set; } = [];
}

/// <summary>
/// Locally maintained entries only. No game data is read or modified, and bundled
/// example IDs are conspicuously invalid example.* placeholders.
/// </summary>
public sealed class PlaceablesCatalogStore
{
    public static readonly string[] DefaultCategories =
    [
        "Foundations", "Walls", "Floors", "Roofs", "Stairs", "Doors & Windows",
        "Pillars & Beams", "Fences & Ramps", "Storage & Magic Chests",
        "Furniture", "Crafting Stations", "Lighting", "Decorations",
        "Comfort", "Utilities", "Gardening", "Miscellaneous"
    ];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bjornhjarta", "Placeables", "catalog.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static IReadOnlyList<PlaceableCatalogEntry> ExampleEntries()
    {
        var examples = new (string Name, string Id, string Category)[]
        {
            ("Foundation", "example.foundation", "Foundations"),
            ("Stone foundation", "example.stone_foundation", "Foundations"),
            ("Wall", "example.wall", "Walls"),
            ("Window wall", "example.window_wall", "Walls"),
            ("Floor", "example.floor", "Floors"),
            ("Roof", "example.roof", "Roofs"),
            ("Stairs", "example.stairs", "Stairs"),
            ("Door", "example.door", "Doors & Windows"),
            ("Window", "example.window", "Doors & Windows"),
            ("Pillar", "example.pillar", "Pillars & Beams"),
            ("Beam", "example.beam", "Pillars & Beams"),
            ("Fence", "example.fence", "Fences & Ramps"),
            ("Ramp", "example.ramp", "Fences & Ramps"),
            ("Storage chest", "example.storage_chest", "Storage & Magic Chests"),
            ("Magic chest", "example.magic_chest", "Storage & Magic Chests"),
            ("Table", "example.table", "Furniture"),
            ("Chair", "example.chair", "Furniture"),
            ("Bed", "example.bed", "Comfort"),
            ("Crafting station", "example.crafting_station", "Crafting Stations"),
            ("Lantern", "example.lantern", "Lighting"),
            ("Decoration", "example.decoration", "Decorations"),
            ("Utility object", "example.utility", "Utilities"),
            ("Planter", "example.planter", "Gardening")
        };
        return examples.Select(e => new PlaceableCatalogEntry
        {
            Name = e.Name, TypeId = e.Id, Category = e.Category,
            Source = "Illustrative template (not extracted from Enshrouded)"
        }).ToArray();
    }

    public IReadOnlyList<PlaceableCatalogEntry> Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        return File.Exists(file) ? ReadFile(file) : [];
    }

    public IReadOnlyList<PlaceableCatalogEntry> ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Catalog file not found.", path);
        if (info.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Catalog exceeds the 2 MB import limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        PlaceablesCatalogFile? catalog;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            catalog = new PlaceablesCatalogFile { Entries = JsonSerializer.Deserialize<List<PlaceableCatalogEntry>>(document.RootElement, Options) ?? [] };
        else
            catalog = JsonSerializer.Deserialize<PlaceablesCatalogFile>(document.RootElement, Options);
        if (catalog?.SchemaVersion != 1 || catalog.Entries is null)
            throw new InvalidDataException("Unsupported catalog format (expected schemaVersion 1).");
        ValidateEntries(catalog.Entries);
        foreach (var entry in catalog.Entries)
            entry.IsUserEntry = true;
        return catalog.Entries;
    }

    public void Save(IEnumerable<PlaceableCatalogEntry> entries, string? path = null)
    {
        var file = path ?? DefaultPath;
        var materialized = entries.Select(e => new PlaceableCatalogEntry
        {
            Name = e.Name.Trim(), TypeId = e.TypeId.Trim(), Category = e.Category.Trim(),
            Notes = e.Notes.Trim(), Source = e.Source.Trim()
        }).ToList();
        ValidateEntries(materialized);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PlaceablesCatalogFile { Entries = materialized }, Options);
        if (bytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Catalog exceeds the 2 MB size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(file))
            {
                // Preserve a recoverable copy before each successful replace.
                File.Copy(file, file + ".bak", overwrite: true);
            }
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void ValidateEntries(IReadOnlyList<PlaceableCatalogEntry> entries)
    {
        if (entries.Count > 5000)
            throw new InvalidDataException("Catalog has more than 5,000 entries.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null || !Valid(entry.Name, 140) || !Valid(entry.TypeId, 256) ||
                !Valid(entry.Category, 80) || entry.Notes is null || entry.Notes.Length > 2000 ||
                entry.Source is null || entry.Source.Length > 200)
                throw new InvalidDataException("Catalog entry has missing, invalid or oversized fields.");
            if (!ids.Add(entry.TypeId.Trim()))
                throw new InvalidDataException($"Duplicate catalog Type ID: '{entry.TypeId}'.");
        }
    }

    private static bool Valid(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit &&
        !value.Any(c => char.IsControl(c));
}
