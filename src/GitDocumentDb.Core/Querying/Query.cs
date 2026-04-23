using System.Linq.Expressions;

namespace GitDocumentDb;

public sealed class Query
{
    internal LambdaExpression? Predicate { get; }
    internal LambdaExpression? OrderKey { get; }
    internal bool OrderDescending { get; }
    internal int? SkipCount { get; }
    internal int? TakeCount { get; }

    internal Query(LambdaExpression? predicate, LambdaExpression? orderKey, bool orderDescending, int? skip, int? take)
    {
        Predicate = predicate;
        OrderKey = orderKey;
        OrderDescending = orderDescending;
        SkipCount = skip;
        TakeCount = take;
    }

    public static QueryBuilder<T> For<T>() where T : class => new();
}
