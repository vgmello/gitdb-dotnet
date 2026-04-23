using System.Linq.Expressions;

namespace GitDocumentDb;

internal static class QueryCompiler
{
    public sealed record IndexClause(string Field, IndexOp Op, object Value);

    public enum IndexOp { Equal, Greater, GreaterOrEqual, Less, LessOrEqual }

    public static List<IndexClause>? ExtractIndexClauses(LambdaExpression predicate)
    {
        var clauses = new List<IndexClause>();
        if (!Visit(predicate.Body, clauses)) return null;
        return clauses;
    }

    private static bool Visit(Expression expr, List<IndexClause> clauses)
    {
        if (expr is BinaryExpression bin)
        {
            if (bin.NodeType == ExpressionType.AndAlso)
                return Visit(bin.Left, clauses) && Visit(bin.Right, clauses);

            if (TryExtractComparison(bin, out var clause))
            {
                clauses.Add(clause);
                return true;
            }
        }
        return false;
    }

    private static bool TryExtractComparison(BinaryExpression bin, out IndexClause clause)
    {
        clause = default!;
        var op = bin.NodeType switch
        {
            ExpressionType.Equal => IndexOp.Equal,
            ExpressionType.GreaterThan => IndexOp.Greater,
            ExpressionType.GreaterThanOrEqual => IndexOp.GreaterOrEqual,
            ExpressionType.LessThan => IndexOp.Less,
            ExpressionType.LessThanOrEqual => IndexOp.LessOrEqual,
            _ => (IndexOp?)null,
        };
        if (op is null) return false;

        string? field = null;
        object? value = null;
        if (bin.Left is MemberExpression ml && TryGetConstant(bin.Right, out var rv))
        { field = ml.Member.Name; value = rv; }
        else if (bin.Right is MemberExpression mr && TryGetConstant(bin.Left, out var lv))
        {
            field = mr.Member.Name; value = lv;
            op = Flip(op.Value);
        }
        if (field is null || value is null) return false;
        clause = new IndexClause(field, op.Value, value);
        return true;
    }

    private static bool TryGetConstant(Expression expr, out object? value)
    {
        if (expr is ConstantExpression c) { value = c.Value; return true; }
        // Captured closure/local: evaluate via compile.
        try
        {
            var lambda = Expression.Lambda(expr).Compile();
            value = lambda.DynamicInvoke();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static IndexOp Flip(IndexOp op) => op switch
    {
        IndexOp.Equal => IndexOp.Equal,
        IndexOp.Greater => IndexOp.Less,
        IndexOp.GreaterOrEqual => IndexOp.LessOrEqual,
        IndexOp.Less => IndexOp.Greater,
        IndexOp.LessOrEqual => IndexOp.GreaterOrEqual,
        _ => op,
    };
}
