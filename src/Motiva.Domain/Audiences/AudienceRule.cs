namespace Motiva.Domain.Audiences;

/// <summary>
/// Three tag sets of a campaign or task audience (B11): any — at least one tag must match;
/// all — every tag must match; none — no tag may match. Empty sets do not restrict.
/// </summary>
public sealed record AudienceRule(IReadOnlySet<string> Any, IReadOnlySet<string> All, IReadOnlySet<string> None)
{
    public static readonly AudienceRule Unrestricted = new(
        new HashSet<string>(), new HashSet<string>(), new HashSet<string>());

    public bool Matches(IReadOnlySet<string> employeeTags)
    {
        if (None.Count > 0 && None.Overlaps(employeeTags))
        {
            return false;
        }

        if (All.Count > 0 && !All.IsSubsetOf(employeeTags))
        {
            return false;
        }

        if (Any.Count > 0 && !Any.Overlaps(employeeTags))
        {
            return false;
        }

        return true;
    }
}
