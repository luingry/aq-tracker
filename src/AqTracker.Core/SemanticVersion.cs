namespace AqTracker.Core;

/// <summary>Minimal SemVer 2.0.0 parser/comparer used to compare local and released app versions.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease)
{
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        // .NET Framework's unannotated reference assemblies mean string.IsNullOrWhiteSpace
        // does not flow-narrow `value` here; check nullity directly so the compiler can prove it.
        if (value is null) return false;

        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(1);

        var buildIndex = trimmed.IndexOf('+');
        if (buildIndex >= 0) trimmed = trimmed.Substring(0, buildIndex);

        string? prerelease = null;
        var core = trimmed;
        var prereleaseIndex = trimmed.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prerelease = trimmed.Substring(prereleaseIndex + 1);
            core = trimmed.Substring(0, prereleaseIndex);
            if (prerelease.Length == 0) return false;
        }

        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        if (!int.TryParse(parts[1], out var minor) || minor < 0) return false;
        if (!int.TryParse(parts[2], out var patch) || patch < 0) return false;

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public bool IsNewerThan(SemanticVersion other) => Compare(this, other) > 0;

    public static int Compare(SemanticVersion left, SemanticVersion right)
    {
        var majorCompare = left.Major.CompareTo(right.Major);
        if (majorCompare != 0) return majorCompare;
        var minorCompare = left.Minor.CompareTo(right.Minor);
        if (minorCompare != 0) return minorCompare;
        var patchCompare = left.Patch.CompareTo(right.Patch);
        return patchCompare != 0 ? patchCompare : ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    // SemVer 2.0.0 §11: a version without a prerelease tag outranks one with a tag.
    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < length; index++)
        {
            if (index >= leftParts.Length) return -1;
            if (index >= rightParts.Length) return 1;
            var comparison = ComparePrereleaseIdentifier(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = int.TryParse(left, out var leftNumber);
        var rightIsNumeric = int.TryParse(right, out var rightNumber);
        if (leftIsNumeric && rightIsNumeric) return leftNumber.CompareTo(rightNumber);
        if (leftIsNumeric) return -1;
        if (rightIsNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
