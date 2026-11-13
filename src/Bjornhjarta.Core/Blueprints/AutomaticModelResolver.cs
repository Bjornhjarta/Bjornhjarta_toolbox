namespace Bjornhjarta.Core.Blueprints;

/// <summary>
/// Deterministically maps user-supplied Type IDs to local model assets. A mapping
/// is never guessed from a filename or category. Ambiguous IDs fail closed.
/// Original game data and element Type IDs are never modified here.
/// </summary>
public sealed class AutomaticModelResolver
{
    private readonly Dictionary<string, ModelAssetRecord> _byType = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);

    public AutomaticModelResolver(IEnumerable<ModelAssetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var group in records
            .Where(x => x.IsValid && x.IsModelValid &&
                        !string.IsNullOrWhiteSpace(x.TypeId) && !string.IsNullOrWhiteSpace(x.AssetId))
            .GroupBy(x => x.TypeId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            // The model database can contain several distinct asset IDs with the
            // same Type ID. Never silently choose one based on folder ordering.
            var candidates = group.ToArray();
            if (candidates.Length != 1)
                _ambiguous.Add(group.Key);
            else
                _byType[group.Key] = candidates[0];
        }
    }

    public bool TryResolve(string? typeId, out ModelAssetRecord? record)
    {
        record = null;
        var normalized = typeId?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
               _byType.TryGetValue(normalized, out record);
    }

    public bool IsAmbiguous(string? typeId) =>
        !string.IsNullOrWhiteSpace(typeId) && _ambiguous.Contains(typeId.Trim());

    /// <summary>
    /// Applies only missing asset references. Existing explicit user assignments
    /// remain unchanged, even when the library has a different Type ID mapping.
    /// The caller owns undo, persistence, and rendering.
    /// </summary>
    public ModelMappingReport AssignMissing(IEnumerable<BlueprintElement> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var report = new ModelMappingReport();
        foreach (var piece in pieces)
        {
            if (!string.IsNullOrWhiteSpace(piece.ModelAssetId))
            {
                report.SkippedExisting++;
                continue;
            }
            if (IsAmbiguous(piece.TypeId))
            {
                report.Ambiguous++;
                report.AmbiguousTypeIds.Add(piece.TypeId);
                continue;
            }
            if (!TryResolve(piece.TypeId, out var model) || model is null)
            {
                report.Missing++;
                if (!string.IsNullOrWhiteSpace(piece.TypeId))
                    report.MissingTypeIds.Add(piece.TypeId);
                continue;
            }
            piece.ModelAssetId = model.AssetId;
            piece.ModelAssetName = model.DisplayName;
            report.Assigned++;
        }
        return report;
    }
}

public sealed class ModelMappingReport
{
    public int Assigned { get; internal set; }
    public int SkippedExisting { get; internal set; }
    public int Ambiguous { get; internal set; }
    public int Missing { get; internal set; }
    public HashSet<string> AmbiguousTypeIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissingTypeIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Summary =>
        $"{Assigned} auto-linked • {SkippedExisting} existing • {Missing} without a mapping • {Ambiguous} ambiguous";
}
