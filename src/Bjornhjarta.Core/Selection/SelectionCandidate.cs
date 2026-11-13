namespace Bjornhjarta.Core.Selection;

/// <summary>
/// Framework-neutral description of a candidate object.
/// A game adapter must populate this from verified game APIs.
/// </summary>
public sealed record SelectionCandidate(
    string Id,
    string TypeId,
    Vector3D Position,
    bool IsProtected = false);
