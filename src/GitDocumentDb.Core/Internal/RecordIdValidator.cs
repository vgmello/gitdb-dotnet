namespace GitDocumentDb.Internal;

internal static class RecordIdValidator
{
    private const int MaxLength = 200;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || id[0] == '.')
            return false;
        foreach (var c in id)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                     or '_' or '-' or '.';
            if (!ok) return false;
        }
        return true;
    }

    public static void ThrowIfInvalid(string? id, string paramName)
    {
        if (!IsValid(id))
            throw new ArgumentException(
                $"Record id must match [A-Za-z0-9_\\-.]{{1,{MaxLength}}} and not start with '.'.",
                paramName);
    }
}
