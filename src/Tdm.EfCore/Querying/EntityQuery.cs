using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;

namespace Tdm.EfCore.Querying;

/// <summary>
/// Non-generic gateway to <c>DbContext.Set&lt;T&gt;()</c> queries over runtime-discovered
/// entity types. One cached generic handler per CLR type; filters become
/// <c>x =&gt; x.Prop == value</c> expressions EF can translate.
/// </summary>
internal static class EntityQuery
{
    private static readonly ConcurrentDictionary<Type, IHandler> Handlers = new();

    private interface IHandler
    {
        Task<List<object>> WhereAsync(DbContext ctx, IReadOnlyList<PropertyFilter> filters, int? take, CancellationToken ct);
        Task<int> CountAsync(DbContext ctx, IReadOnlyList<PropertyFilter> filters, CancellationToken ct);
    }

    private static IHandler For(Type entityType) =>
        Handlers.GetOrAdd(entityType, t => (IHandler)Activator.CreateInstance(typeof(Handler<>).MakeGenericType(t))!);

    public static Task<List<object>> WhereAsync(DbContext ctx, Type entityType,
        IReadOnlyList<PropertyFilter> filters, int? take = null, CancellationToken ct = default) =>
        For(entityType).WhereAsync(ctx, filters, take, ct);

    public static Task<int> CountAsync(DbContext ctx, Type entityType,
        IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default) =>
        For(entityType).CountAsync(ctx, filters, ct);

    private sealed class Handler<T> : IHandler where T : class
    {
        public async Task<List<object>> WhereAsync(DbContext ctx, IReadOnlyList<PropertyFilter> filters, int? take, CancellationToken ct)
        {
            var query = Build(ctx, filters);
            if (take is { } n) query = query.Take(n);
            var list = await query.ToListAsync(ct).ConfigureAwait(false);
            return [.. list.Cast<object>()];
        }

        public Task<int> CountAsync(DbContext ctx, IReadOnlyList<PropertyFilter> filters, CancellationToken ct) =>
            Build(ctx, filters).CountAsync(ct);

        private static IQueryable<T> Build(DbContext ctx, IReadOnlyList<PropertyFilter> filters)
        {
            IQueryable<T> query = ctx.Set<T>();
            foreach (var filter in filters)
            {
                var parameter = Expression.Parameter(typeof(T), "x");
                var body = Expression.Equal(
                    Expression.Property(parameter, filter.Property),
                    Expression.Constant(filter.Value, filter.Property.PropertyType));
                query = query.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
            }
            return query;
        }
    }
}
