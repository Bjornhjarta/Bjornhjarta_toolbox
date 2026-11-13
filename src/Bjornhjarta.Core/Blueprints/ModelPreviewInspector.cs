using System.Text.Json;

namespace Bjornhjarta.Core.Blueprints;

/// <summary>
/// Reports what the current WPF 3D preview can actually display. This checks
/// glTF material declarations, NOT their visual accuracy or Enshrouded origin.
/// Full PBR and extension-based material decoding are not available yet.
/// </summary>
public static class ModelPreviewInspector
{
    public sealed record Assessment(string Status, string Details, bool HasBaseColorTexture, bool RequiresUnsupportedExtensions);

    public static Assessment Assess(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Expected a glTF JSON object.", nameof(root));

        var required = Strings(root, "extensionsRequired").ToArray();
        if (required.Length > 0)
            return new Assessment("Unsupported GLB preview",
                "Required glTF extension(s) not decoded by this WPF renderer: " + string.Join(", ", required) + ".",
                false, true);

        var hasBaseColor = false;
        var hasNormal = false;
        var hasOrm = false;
        var hasOcclusion = false;
        var hasEmissive = false;
        var usesMaterialExtensions = false;
        if (root.TryGetProperty("materials", out var materials) && materials.ValueKind == JsonValueKind.Array)
        {
            foreach (var material in materials.EnumerateArray())
            {
                if (material.ValueKind != JsonValueKind.Object) continue;
                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.ValueKind == JsonValueKind.Object)
                {
                    hasBaseColor |= ObjectProperty(pbr, "baseColorTexture");
                    hasOrm |= ObjectProperty(pbr, "metallicRoughnessTexture");
                }
                hasNormal |= ObjectProperty(material, "normalTexture");
                hasOcclusion |= ObjectProperty(material, "occlusionTexture");
                hasEmissive |= ObjectProperty(material, "emissiveTexture");
                usesMaterialExtensions |= ObjectProperty(material, "extensions");
            }
        }

        var limitations = new List<string>();
        if (hasNormal) limitations.Add("normal maps");
        if (hasOrm) limitations.Add("metallic/roughness maps");
        if (hasOcclusion) limitations.Add("occlusion maps");
        if (hasEmissive) limitations.Add("emissive maps");
        if (usesMaterialExtensions) limitations.Add("material extensions");

        var preview = hasBaseColor
            ? "WPF preview can display base-color textures (when the referenced images/UVs load)."
            : "No base-color texture is declared; the preview uses a base-color factor or fallback material.";
        if (limitations.Count > 0)
            preview += " Not rendered in this preview: " + string.Join(", ", limitations) + ".";
        preview += " glTF units, pivots and Enshrouded material fidelity are not independently verified.";
        return new Assessment(limitations.Count == 0 ? "Basic WPF preview" : "Partial material preview", preview,
            hasBaseColor, false);
    }

    private static bool ObjectProperty(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object;

    private static IEnumerable<string> Strings(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                yield return value.GetString()!;
    }
}
