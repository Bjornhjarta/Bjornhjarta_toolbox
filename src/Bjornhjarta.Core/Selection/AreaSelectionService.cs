namespace Bjornhjarta.Core.Selection;

public sealed class AreaSelectionService
{
    public IReadOnlyList<SelectionCandidate> FindInside(
        WorldRegion region,
        IEnumerable<SelectionCandidate> candidates,
        Func<SelectionCandidate, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => !candidate.IsProtected)
            .Where(candidate => region.Contains(candidate.Position))
            .Where(candidate => filter is null || filter(candidate))
            .ToArray();
    }
}
