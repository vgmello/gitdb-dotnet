namespace GitDocumentDb.Internal;

internal static class NameValidator
{
    private const int MaxLength = 100;

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
        foreach (var c in name)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                     or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    public static void ThrowIfInvalid(string? name, string paramName)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"Name must match [A-Za-z0-9_\\-]{{1,{MaxLength}}}.", paramName);
    }
}
