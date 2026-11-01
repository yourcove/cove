using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cove.Data;

/// <summary>
/// <c>count(*) OVER ()</c>: how many rows a query matches before its page is cut, read beside each row of
/// that page. A list page and its total then come from one evaluation of the filter instead of two.
/// </summary>
public static class WindowCount
{
    internal static readonly MethodInfo TotalMethod = typeof(WindowCount).GetMethod(nameof(Total))!;

    public static long Total()
        => throw new InvalidOperationException("This method can only be evaluated by a relational database.");

    internal static void Configure(ModelBuilder modelBuilder)
        => modelBuilder.HasDbFunction(TotalMethod)
            .HasTranslation(_ => new SqlFragmentExpression("count(*) OVER ()", typeof(long), new LongTypeMapping("bigint")));
}
