using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Nvm.Foundation.Web;

/// <summary>Lỗi cú pháp hoặc kiểu trong tham số OData; trả HTTP 400.</summary>
public sealed class ODataQueryException(string message) : Exception(message);

/// <summary>
/// Tập con OData mà màn và script chẩn đoán dùng: <c>$filter</c> (eq ne gt ge lt le, and or not, ngoặc, contains,
/// startswith, endswith), <c>$orderby</c>, <c>$top</c>, <c>$skip</c>, <c>$count</c>. Dịch thành LINQ trên query trì hoãn,
/// nên điều kiện chạy ở SQL.
/// </summary>
public static partial class ODataQuery
{
    private static readonly MethodInfo ApplyMethod = typeof(ODataQuery).GetMethod(nameof(ApplyTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Áp các tham số lên <paramref name="source"/>. Trả query mới và query đếm (trước <c>$top</c>/<c>$skip</c>).</summary>
    public static (IQueryable Rows, IQueryable Filtered) Apply(IQueryable source, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ((IQueryable, IQueryable))ApplyMethod.MakeGenericMethod(source.ElementType)
            .Invoke(null, BindingFlags.DoNotWrapExceptions, null, [source, options], CultureInfo.InvariantCulture)!;
    }

    private static (IQueryable, IQueryable) ApplyTyped<T>(IQueryable<T> source, IReadOnlyDictionary<string, string> options)
    {
        var query = source;
        if (options.TryGetValue("$filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            var item = Expression.Parameter(typeof(T), "item");
            var body = new Parser(Tokenize(filter), item).ParseExpression();
            query = query.Where(Expression.Lambda<Func<T, bool>>(body, item));
        }

        var filtered = query;
        if (options.TryGetValue("$orderby", out var orderBy) && !string.IsNullOrWhiteSpace(orderBy))
        {
            var first = true;
            foreach (var part in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var descending = pieces.Length > 1 && pieces[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
                var item = Expression.Parameter(typeof(T), "item");
                var property = Property(item, pieces[0]);
                var lambda = Expression.Lambda(property, item);
                var method = (first, descending) switch
                {
                    (true, false) => nameof(Queryable.OrderBy),
                    (true, true) => nameof(Queryable.OrderByDescending),
                    (false, false) => nameof(Queryable.ThenBy),
                    (false, true) => nameof(Queryable.ThenByDescending),
                };
                query = (IQueryable<T>)query.Provider.CreateQuery(Expression.Call(typeof(Queryable), method,
                    [typeof(T), property.Type], query.Expression, Expression.Quote(lambda)));
                first = false;
            }
        }

        if (options.TryGetValue("$skip", out var skip))
        {
            query = query.Skip(Number(skip, "$skip"));
        }

        if (options.TryGetValue("$top", out var top))
        {
            query = query.Take(Number(top, "$top"));
        }

        return (query, filtered);
    }

    private static int Number(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ODataQueryException($"{name} must be a non-negative integer.");

    private static MemberExpression Property(Expression item, string name)
    {
        var property = item.Type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ODataQueryException($"Property '{name}' does not exist on {item.Type.Name}.");
        return Expression.Property(item, property);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match match in TokenPattern().Matches(text))
        {
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                tokens.Add(match.Value);
            }
        }

        return tokens;
    }

    // Chuỗi trong nháy đơn ('' là nháy), ngày giờ ISO, Guid, số, tên, ngoặc, dấu phẩy.
    [GeneratedRegex(@"'(?:[^']|'')*'|\d{4}-\d{2}-\d{2}T[0-9:.]+(?:Z|[+-]\d{2}:\d{2})|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|-?\d+(?:\.\d+)?|[A-Za-z_][A-Za-z0-9_]*|[(),]|\S")]
    private static partial Regex TokenPattern();

    private sealed class Parser(List<string> tokens, ParameterExpression item)
    {
        private int _position;

        public Expression ParseExpression()
        {
            var result = ParseOr();
            if (_position < tokens.Count)
            {
                throw new ODataQueryException($"Unexpected '{tokens[_position]}' in $filter.");
            }

            return result;
        }

        private Expression ParseOr()
        {
            var left = ParseAnd();
            while (Accept("or"))
            {
                left = Expression.OrElse(left, ParseAnd());
            }

            return left;
        }

        private Expression ParseAnd()
        {
            var left = ParseUnary();
            while (Accept("and"))
            {
                left = Expression.AndAlso(left, ParseUnary());
            }

            return left;
        }

        private Expression ParseUnary()
        {
            if (Accept("not"))
            {
                return Expression.Not(ParseUnary());
            }

            if (Accept("("))
            {
                var inner = ParseOr();
                Expect(")");
                return inner;
            }

            var name = Next();
            if (name is "contains" or "startswith" or "endswith")
            {
                Expect("(");
                var property = Property(item, Next());
                Expect(",");
                var value = Literal(Next(), typeof(string));
                Expect(")");
                if (property.Type != typeof(string))
                {
                    throw new ODataQueryException($"{name} needs a string property.");
                }

                var method = name switch
                {
                    "contains" => nameof(string.Contains),
                    "startswith" => nameof(string.StartsWith),
                    _ => nameof(string.EndsWith),
                };
                return Expression.Call(property, typeof(string).GetMethod(method, [typeof(string)])!, value);
            }

            var member = Property(item, name);
            var op = Next();
            var literal = Literal(Next(), member.Type);
            return op switch
            {
                "eq" => Expression.Equal(member, literal),
                "ne" => Expression.NotEqual(member, literal),
                "gt" or "ge" or "lt" or "le" when member.Type == typeof(string) =>
                    throw new ODataQueryException("gt/ge/lt/le are not supported on strings."),
                "gt" => Expression.GreaterThan(member, literal),
                "ge" => Expression.GreaterThanOrEqual(member, literal),
                "lt" => Expression.LessThan(member, literal),
                "le" => Expression.LessThanOrEqual(member, literal),
                _ => throw new ODataQueryException($"Unknown operator '{op}'."),
            };
        }

        private static ConstantExpression Literal(string token, Type type)
        {
            if (token == "null")
            {
                return Expression.Constant(null, type);
            }

            var target = Nullable.GetUnderlyingType(type) ?? type;
            object value;
            try
            {
                value = target switch
                {
                    _ when target == typeof(string) => token.StartsWith('\'')
                        ? token[1..^1].Replace("''", "'", StringComparison.Ordinal)
                        : throw new ODataQueryException($"Expected a quoted string, got {token}."),
                    _ when target == typeof(Guid) => Guid.Parse(token.Trim('\''), CultureInfo.InvariantCulture),
                    _ when target == typeof(DateTimeOffset) => DateTimeOffset.Parse(token.Trim('\''), CultureInfo.InvariantCulture),
                    _ when target == typeof(bool) => bool.Parse(token),
                    _ => Convert.ChangeType(token, target, CultureInfo.InvariantCulture),
                };
            }
            catch (FormatException)
            {
                throw new ODataQueryException($"'{token}' is not a valid {target.Name}.");
            }

            return Expression.Constant(value, type);
        }

        private string Next() =>
            _position < tokens.Count ? tokens[_position++] : throw new ODataQueryException("$filter ended too early.");

        private bool Accept(string token)
        {
            if (_position < tokens.Count && string.Equals(tokens[_position], token, StringComparison.Ordinal))
            {
                _position++;
                return true;
            }

            return false;
        }

        private void Expect(string token)
        {
            if (!Accept(token))
            {
                throw new ODataQueryException($"Expected '{token}' in $filter.");
            }
        }
    }
}
