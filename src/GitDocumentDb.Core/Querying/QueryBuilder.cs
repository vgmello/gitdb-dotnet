using System.Linq.Expressions;

namespace GitDocumentDb;

public sealed class QueryBuilder<T> where T : class
{
    private Expression<Func<T, bool>>? _predicate;
    private LambdaExpression? _orderKey;
    private bool _orderDesc;
    private int? _skip;
    private int? _take;

    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        _predicate = predicate;
        return this;
    }

    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> key)
    {
        _orderKey = key; _orderDesc = false;
        return this;
    }

    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key)
    {
        _orderKey = key; _orderDesc = true;
        return this;
    }

    public QueryBuilder<T> Skip(int count) { _skip = count; return this; }
    public QueryBuilder<T> Take(int count) { _take = count; return this; }

    public Query Build() => new(_predicate, _orderKey, _orderDesc, _skip, _take);
}
