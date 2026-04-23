namespace GitDocumentDb.Internal;

internal static class BranchNaming
{
    public const string DatabaseRefPrefix = "refs/heads/db/";
    public static string RefFor(string databaseName) => DatabaseRefPrefix + databaseName;
    public static string NameFrom(string refName) =>
        refName.StartsWith(DatabaseRefPrefix, StringComparison.Ordinal)
            ? refName[DatabaseRefPrefix.Length..]
            : refName;
}
