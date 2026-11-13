using System.Text.Json.Serialization;

namespace Bjornhjarta.Core.Blueprints;

public sealed class BlueprintDocument
{
    public int SchemaVersion { get; set; } = 1;
    public BlueprintMetadata Metadata { get; set; } = new();
    public BlueprintVector3 Origin { get; set; } = new();
    public List<BlueprintElement> Elements { get; set; } = [];
}

public sealed class BlueprintMetadata
{
    public string Name { get; set; } = "Untitled Blueprint";
    public string Description { get; set; } = "";
    public string CreatedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string ToolkitVersion { get; set; } = "1.0.0-rc.2";
}

public sealed class BlueprintElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TypeId { get; set; } = "example.foundation";
    public BlueprintVector3 Position { get; set; } = new();
    public BlueprintRotation Rotation { get; set; } = new();
    public string? Variant { get; set; }
    /// <summary>Optional local asset-library identity used by the future textured 3D renderer.</summary>
    public string? ModelAssetId { get; set; }
    public string? ModelAssetName { get; set; }
}

public sealed class BlueprintVector3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class BlueprintRotation
{
    public double Pitch { get; set; }
    public double Yaw { get; set; }
    public double Roll { get; set; }
}
