using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;

namespace Nvm.Foundation.Runtime;

public sealed record BlockModel(Type Type, string Name, ModelDomain Domain);

public sealed record PropertyModel(PropertyInfo Property, string ColumnName, bool Mandatory, int TextLength)
{
    public string Name => Property.Name;

    public Type ClrType => Property.PropertyType;
}

public sealed record EntityModel(Type Type, BlockModel Block, string TableName, IReadOnlyList<PropertyModel> Properties)
{
    public string Name => Type.Name;
}

public sealed record FacetModel(Type Type, BlockModel Block, EntityModel Entity, IReadOnlyList<PropertyModel> Properties)
{
    public string Name => Type.Name;
}

public sealed record EntityFacetTable(EntityModel Entity, string TableName, IReadOnlyList<FacetModel> Facets)
{
    public Type RootType => typeof(Facet<>).MakeGenericType(Entity.Type);

    public string EntityIdColumn => Entity.Name + "_Id";
}

/// <summary>Command của FB (có <see cref="Block"/>) hoặc composite command của app (có <see cref="App"/>).</summary>
public sealed record CommandModel(
    Type Type,
    Type ResponseType,
    BlockModel? Block,
    string? App,
    Type HandlerType,
    IReadOnlyList<PropertyModel> Parameters)
{
    public string Name => Type.Name;

    public bool IsComposite => App is not null;

    /// <summary>Tên đầy đủ, như cột ContextName của trace.</summary>
    public string FullName => (Block?.Name ?? App) + "." + Type.Name;
}

public sealed record PreCheckModel(Type Type, BlockModel Block, CommandModel Command)
{
    public string Name => Type.Name;
}

public sealed record PostActionModel(Type Type, BlockModel Block, CommandModel Command, bool Optional)
{
    public string Name => Type.Name;
}

public sealed record EventModel(Type Type, BlockModel Block, int Version)
{
    public string Name => Block.Name + "." + Type.Name;
}

/// <summary>Model của solution, dựng một lần lúc khởi động từ attribute trong các assembly được nạp.</summary>
/// <remarks>Thay cho bước biên dịch file khai báo model của nền thật: lỗi khai báo phải chặn khởi động, không đợi tới lúc chạy.</remarks>
public sealed class ModelRegistry
{
    private const int DefaultTextLength = 255;

    private static readonly FrozenSet<Type> ScalarTypes = new[]
    {
        typeof(string), typeof(int), typeof(long), typeof(decimal), typeof(double), typeof(bool), typeof(Guid),
        typeof(DateTimeOffset),
    }.ToFrozenSet();

    private readonly Dictionary<Type, EntityModel> _entities = [];
    private readonly Dictionary<Type, FacetModel> _facets = [];
    private readonly Dictionary<Type, CommandModel> _commands = [];
    private readonly Dictionary<Type, EventModel> _events = [];
    private readonly List<PreCheckModel> _preChecks = [];
    private readonly List<PostActionModel> _postActions = [];

    private ModelRegistry()
    {
    }

    public IReadOnlyCollection<EntityModel> Entities => _entities.Values;

    public IReadOnlyCollection<FacetModel> Facets => _facets.Values;

    public IReadOnlyCollection<CommandModel> Commands => _commands.Values;

    public IReadOnlyList<PreCheckModel> PreChecks => _preChecks;

    public IReadOnlyList<PostActionModel> PostActions => _postActions;

    /// <summary>Mỗi entity có facet: một bảng facet dùng chung.</summary>
    public IReadOnlyList<EntityFacetTable> FacetTables { get; private set; } = [];

    public static ModelRegistry Build(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return Build(assemblies.Distinct().SelectMany(a => a.GetTypes()));
    }

    public static ModelRegistry Build(IEnumerable<Type> modelTypes)
    {
        ArgumentNullException.ThrowIfNull(modelTypes);
        var registry = new ModelRegistry();
        var types = modelTypes.Distinct().ToArray();
        var blocks = new Dictionary<Type, BlockModel>();

        BlockModel BlockOf(Type modelType, Type attributeType)
        {
            var blockType = attributeType.GetGenericArguments()[0];
            if (!blocks.TryGetValue(blockType, out var block))
            {
                var declared = blockType.GetCustomAttribute<FunctionalBlockAttribute>()
                    ?? throw Invalid($"{modelType.Name} points to {blockType.Name}, which has no [FunctionalBlock].");
                block = new BlockModel(blockType, declared.Name, declared.Domain);
                blocks.Add(blockType, block);
            }

            return block;
        }

        foreach (var type in types)
        {
            if (Find(type, typeof(EntityAttribute<>)) is { } attr)
            {
                Require(type, typeof(Entity));
                var block = BlockOf(type, attr);
                registry._entities.Add(type, new EntityModel(type, block, TableName(block.Name, type.Name),
                    Properties(type, typeof(Entity), prefix: null)));
            }
        }

        foreach (var type in types)
        {
            if (Find(type, typeof(FacetAttribute<>)) is { } attr)
            {
                Require(type, typeof(FacetBase));
                var entityType = type.BaseType is { IsGenericType: true } baseType
                    && baseType.GetGenericTypeDefinition() == typeof(Facet<>)
                        ? baseType.GetGenericArguments()[0]
                        : throw Invalid($"Facet {type.Name} must derive directly from Facet<TEntity>.");
                var entity = registry._entities.GetValueOrDefault(entityType)
                    ?? throw Invalid($"Facet {type.Name} extends {entityType.Name}, which is not a registered entity.");
                registry._facets.Add(type, new FacetModel(type, BlockOf(type, attr), entity,
                    Properties(type, type.BaseType!, prefix: type.Name + "_")));
            }
        }

        registry.FacetTables = registry._facets.Values
            .GroupBy(f => f.Entity)
            .Select(g => new EntityFacetTable(g.Key, TableName(g.Key.Block.Name, g.Key.Name + "Facets"),
                g.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray()))
            .ToArray();

        var handlers = types
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces().Where(i => i.IsGenericType
                    && (i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
                        || i.GetGenericTypeDefinition() == typeof(ICompositeCommandHandler<,>)))
                .Select(i => (Command: i.GetGenericArguments()[0], Handler: t, Composite: i.GetGenericTypeDefinition() == typeof(ICompositeCommandHandler<,>))))
            .ToLookup(h => h.Command);

        foreach (var type in types)
        {
            var fbAttr = Find(type, typeof(CommandAttribute<>));
            var app = type.GetCustomAttribute<AppCommandAttribute>()?.App;
            if (fbAttr is null && app is null)
            {
                continue;
            }

            var response = type.GetInterfaces()
                .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>))?
                .GetGenericArguments()[0]
                ?? throw Invalid($"Command {type.Name} must implement ICommand<TResponse>.");
            var found = handlers[type].ToArray();
            if (found.Length != 1)
            {
                throw Invalid($"Command {type.Name} must have exactly one handler, found {found.Length}.");
            }

            if (found[0].Composite != (app is not null))
            {
                throw Invalid($"Command {type.Name}: app commands use ICompositeCommandHandler, FB commands use ICommandHandler.");
            }

            registry._commands.Add(type, new CommandModel(type, response, fbAttr is null ? null : BlockOf(type, fbAttr),
                app, found[0].Handler, Properties(type, typeof(object), prefix: null, persisted: false)));
        }

        foreach (var type in types)
        {
            if (Find(type, typeof(PreCheckAttribute<>)) is { } attr)
            {
                var block = BlockOf(type, attr);
                var command = registry.AttachedCommand(type, typeof(IPreCheck<>));
                // Pre-check đọc bằng Query trong transaction của command, nên phải cùng domain (K8).
                if (command.Block is null || command.Block.Domain != block.Domain)
                {
                    throw Invalid($"Pre-check {type.Name} ({block.Domain}) must be in the domain of {command.Name}.");
                }

                registry._preChecks.Add(new PreCheckModel(type, block, command));
            }

            if (Find(type, typeof(PostActionAttribute<>)) is { } postAttr)
            {
                var command = registry.AttachedCommand(type, typeof(IPostAction<,>));
                var optional = (bool)postAttr.GetProperty(nameof(PostActionAttribute<object>.Optional))!
                    .GetValue(type.GetCustomAttribute(postAttr))!;
                registry._postActions.Add(new PostActionModel(type, BlockOf(type, postAttr), command, optional));
            }

            if (Find(type, typeof(EventAttribute<>)) is { } eventAttr)
            {
                Require(type, typeof(IFoundationEvent));
                var version = type.GetCustomAttribute<EventVersionAttribute>()?.Version ?? 0;
                if (version < 1)
                {
                    throw Invalid($"Event {type.Name} needs [EventVersion(n)] with n >= 1 (K6).");
                }

                registry._events.Add(type, new EventModel(type, BlockOf(type, eventAttr), version));
            }
        }

        return registry;
    }

    public EntityModel? EntityOf(Type type) => _entities.GetValueOrDefault(type);

    public FacetModel? FacetOf(Type type) => _facets.GetValueOrDefault(type);

    /// <summary>Domain của một entity hoặc facet.</summary>
    public ModelDomain DomainOf(Type modelType) =>
        EntityOf(modelType)?.Block.Domain
        ?? FacetOf(modelType)?.Block.Domain
        ?? throw new ModelMisuseException($"{modelType.Name} is not a registered entity or facet.");

    /// <summary>Tên bảng SQL của một entity hoặc facet.</summary>
    public string TableOf(Type modelType) =>
        EntityOf(modelType)?.TableName
        ?? (FacetOf(modelType) is { } facet ? FacetTables.Single(t => t.Entity == facet.Entity).TableName : null)
        ?? throw new ModelMisuseException($"{modelType.Name} is not a registered entity or facet.");

    public CommandModel CommandOf(Type type) =>
        _commands.GetValueOrDefault(type) ?? throw new ModelMisuseException($"{type.Name} is not a registered command.");

    public EventModel EventOf(Type type) =>
        _events.GetValueOrDefault(type) ?? throw new ModelMisuseException($"{type.Name} is not a registered event.");

    /// <summary>
    /// Tên bảng kiểu nền thật: tên cắt còn 20 ký tự kèm hậu tố số ổn định. Nhiều bảng cùng tiền tố là chuyện thường,
    /// nên SQL chẩn đoán phải tra bảng <c>Foundation_ModelTable</c>, không đoán tên.
    /// </summary>
    internal static string TableName(string block, string name)
    {
        var hash = 2166136261u;
        foreach (var c in block + "." + name)
        {
            hash = unchecked((hash ^ c) * 16777619u);
        }

        return (name.Length > 20 ? name[..20] : name) + "_" + (hash % 1_000_000_000u).ToString(CultureInfo.InvariantCulture);
    }

    private CommandModel AttachedCommand(Type type, Type contract)
    {
        var commandType = type.GetInterfaces()
            .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == contract)?
            .GetGenericArguments()[0]
            ?? throw Invalid($"{type.Name} must implement {contract.Name}.");
        return _commands.GetValueOrDefault(commandType)
            ?? throw Invalid($"{type.Name} is attached to {commandType.Name}, which is not a registered command.");
    }

    private static Type? Find(Type type, Type genericAttribute) =>
        type.GetCustomAttributes(inherit: false)
            .Select(a => a.GetType())
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == genericAttribute);

    private static void Require(Type type, Type baseType)
    {
        if (!baseType.IsAssignableFrom(type) || type.IsAbstract)
        {
            throw Invalid($"{type.Name} must be a concrete {baseType.Name}.");
        }
    }

    private static List<PropertyModel> Properties(Type type, Type stopAt, string? prefix, bool persisted = true)
    {
        var result = new List<PropertyModel>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Chỉ thuộc tính khai ở chính lớp model; cột hệ thống (Id, NId, IsDeleted…) do nền quản lý riêng.
            if (p.DeclaringType is null || (persisted && !p.DeclaringType.IsSubclassOf(stopAt)))
            {
                continue;
            }

            if (persisted)
            {
                var clr = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (!ScalarTypes.Contains(clr) || p.SetMethod is not { IsPublic: true })
                {
                    throw Invalid($"{type.Name}.{p.Name}: persisted properties must be public read/write scalars.");
                }
            }

            result.Add(new PropertyModel(p, (prefix ?? string.Empty) + p.Name, p.IsDefined(typeof(MandatoryAttribute)),
                p.GetCustomAttribute<TextLengthAttribute>()?.Length ?? DefaultTextLength));
        }

        return result;
    }

    private static InvalidOperationException Invalid(string message) => new("Model error: " + message);
}
