namespace Bjornhjarta.Core.Selection;

/// <summary>
/// Axis-aligned region in toolkit/world coordinates.
/// Coordinates are abstract until a game adapter confirms the game's coordinate system.
/// </summary>
public sealed class WorldRegion
{
    public required Vector3D Min { get; init; }
    public required Vector3D Max { get; init; }

    public bool Contains(Vector3D point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    public static WorldRegion FromCorners(Vector3D first, Vector3D second) =>
        new()
        {
            Min = new Vector3D(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Min(first.Z, second.Z)),
            Max = new Vector3D(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y), Math.Max(first.Z, second.Z))
        };
}

public sealed record Vector3D(double X, double Y, double Z);
