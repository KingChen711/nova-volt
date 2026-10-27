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

/// <summary>App và POM của nó. Extension app có <see cref="BaseApp"/>; command của nó chạy dưới base app.</summary>
public sealed record AppModel(
    Type Type,
    string Name,
    AppModel? BaseApp,
    IReadOnlySet<Type> Objects,
    IReadOnlySet<Type> Commands,
    IReadOnlySet<Type> ReadingFunctions)
{
    public bool IsExtension => BaseApp is not null;
}

/// <summary>Reading model ánh xạ vào view <c>dbo.&lt;Name&gt;</c>.</summary>
public sealed record ReadingModelModel(Type Type, BlockModel Block)
{
    public string ViewName => Type.Name;
}

public sealed record ReadingFunctionModel(Type Type, string App, Type ParametersType, Type RowType)
{
    public string Name => Type.Name;
}

public sealed record EventSubscriberModel(Type Type, Type EventType, BlockModel Block, string Subscription, bool Predefined, string WorkerRole);

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
    private readonly Dictionary<string, AppModel> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, ReadingModelModel> _readingModels = [];
    private readonly Dictionary<Type, ReadingFunctionModel> _readingFunctions = [];
    private readonly List<EventSubscriberModel> _eventSubscribers = [];

    private ModelRegistry()
    {
    }

    public IReadOnlyCollection<EntityModel> Entities => _entities.Values;

    public IReadOnlyCollection<FacetModel> Facets => _facets.Values;

    public IReadOnlyCollection<CommandModel> Commands => _commands.Values;

    public IReadOnlyList<PreCheckModel> PreChecks => _preChecks;

    public IReadOnlyList<PostActionModel> PostActions => _postActions;

    public IReadOnlyCollection<AppModel> Apps => _apps.Values;

    public IReadOnlyCollection<ReadingModelModel> ReadingModels => _readingModels.Values;

    public IReadOnlyCollection<EventModel> Events => _events.Values;

    public IReadOnlyList<EventSubscriberModel> EventSubscribers => _eventSubscribers;

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
        // Model riêng của Foundation (configuration key, state machine, signal rule, timer) luôn có mặt.
        var types = modelTypes.Concat(typeof(ModelRegistry).Assembly.GetTypes()).Distinct().ToArray();
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

            if (Find(type, typeof(ReadingModelAttribute<>)) is { } readingAttr)
            {
                registry._readingModels.Add(type, new ReadingModelModel(type, BlockOf(type, readingAttr)));
            }
        }

        registry.BuildApps(types);

        foreach (var type in types)
        {
            if (Find(type, typeof(EventSubscriberAttribute<>)) is { } handlerAttr)
            {
                var eventType = type.GetInterfaces()
                    .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventSubscriber<>))?
                    .GetGenericArguments()[0]
                    ?? throw Invalid($"Event handler {type.Name} must implement IEventSubscriber<TEvent>.");
                _ = registry.EventOf(eventType);
                var declared = type.GetCustomAttribute(handlerAttr)!;
                object? Read(string name) => handlerAttr.GetProperty(name)!.GetValue(declared);
                registry._eventSubscribers.Add(new EventSubscriberModel(type, eventType, BlockOf(type, handlerAttr),
                    (string)Read(nameof(EventSubscriberAttribute<object>.Subscription))!,
                    (bool)Read(nameof(EventSubscriberAttribute<object>.Predefined))!,
                    (string)Read(nameof(EventSubscriberAttribute<object>.WorkerRole))!));
            }
        }

        var duplicate = registry._eventSubscribers.GroupBy(h => h.Subscription, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw Invalid($"Subscription {duplicate.Key} is declared by more than one handler.");
        }

        return registry;
    }

    public AppModel AppOf(string name) =>
        _apps.GetValueOrDefault(name) ?? throw new KeyNotFoundException($"App {name} is not part of the model.");

    public bool HasApp(string name) => _apps.ContainsKey(name);

    public EventModel? EventByName(string name) =>
        _events.Values.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    public ReadingModelModel? ReadingModelOf(Type type) => _readingModels.GetValueOrDefault(type);

    /// <summary>Tên mà <paramref name="app"/> công bố cho command, hoặc null nếu app không công bố nó.</summary>
    /// <remarks>Command của FB: import vào POM. Composite: của chính app, hoặc của extension app gắn vào nó (có tiền tố).</remarks>
    public string? PublicNameIn(string app, CommandModel command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var model = AppOf(app);
        if (command.App is null)
        {
            return model.Commands.Contains(command.Type) ? command.Name : null;
        }

        if (string.Equals(command.App, app, StringComparison.Ordinal) && !model.IsExtension)
        {
            return command.Name;
        }

        var owner = _apps.GetValueOrDefault(command.App);
        return owner?.BaseApp == model ? owner.Name + "_" + command.Name : null;
    }

    /// <summary>Command theo tên công bố trên app (POST tới OData của app).</summary>
    public CommandModel? CommandByPublicName(string app, string name) =>
        _commands.Values.FirstOrDefault(c => string.Equals(PublicNameIn(app, c), name, StringComparison.Ordinal));

    /// <summary>Reading function theo tên công bố trên app; cùng luật tiền tố như command.</summary>
    public ReadingFunctionModel? ReadingFunctionByPublicName(string app, string name)
    {
        var model = AppOf(app);
        return _readingFunctions.Values.FirstOrDefault(f =>
            (string.Equals(f.App, app, StringComparison.Ordinal) && !model.IsExtension
                && string.Equals(f.Name, name, StringComparison.Ordinal))
            || (_apps.GetValueOrDefault(f.App)?.BaseApp == model
                && string.Equals(f.App + "_" + f.Name, name, StringComparison.Ordinal)));
    }

    /// <summary>Entity set mà app công bố: entity, facet hoặc reading model đã import.</summary>
    public Type? EntitySetIn(string app, string name) =>
        AppOf(app).Objects.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private void BuildApps(Type[] types)
    {
        var byType = new Dictionary<Type, AppModel>();

        AppModel Create(Type type, AppAttribute attribute)
        {
            if (byType.TryGetValue(type, out var existing))
            {
                return existing;
            }

            if (!typeof(AppDefinition).IsAssignableFrom(type) || type.IsAbstract)
            {
                throw Invalid($"App {attribute.Name} must be a concrete AppDefinition.");
            }

            AppModel? baseApp = null;
            if (attribute.BaseApp is { } baseType)
            {
                var baseAttribute = baseType.GetCustomAttribute<AppAttribute>()
                    ?? throw Invalid($"App {attribute.Name} extends {baseType.Name}, which has no [App].");
                baseApp = Create(baseType, baseAttribute);
                if (baseApp.IsExtension)
                {
                    // Extension app phẳng: không gắn vào extension app khác.
                    throw Invalid($"Extension app {attribute.Name} cannot extend extension app {baseApp.Name}.");
                }
            }

            var pom = new PomBuilder();
            ((AppDefinition)Activator.CreateInstance(type)!).Build(pom);
            var unknownObject = pom.Objects.FirstOrDefault(t =>
                !_entities.ContainsKey(t) && !_facets.ContainsKey(t) && !_readingModels.ContainsKey(t));
            if (unknownObject is not null)
            {
                throw Invalid($"App {attribute.Name} imports {unknownObject.Name}, which is not an entity, facet or reading model.");
            }

            var unknownCommand = pom.Commands.FirstOrDefault(t => !_commands.TryGetValue(t, out var c) || c.IsComposite);
            if (unknownCommand is not null)
            {
                throw Invalid($"App {attribute.Name} imports {unknownCommand.Name}, which is not a functional block command.");
            }

            var app = new AppModel(type, attribute.Name, baseApp, pom.Objects, pom.Commands, pom.ReadingFunctions);
            if (!_apps.TryAdd(app.Name, app))
            {
                throw Invalid($"App name {app.Name} is declared twice.");
            }

            byType.Add(type, app);
            return app;
        }

        foreach (var type in types)
        {
            if (type.GetCustomAttribute<AppAttribute>() is { } attribute)
            {
                Create(type, attribute);
            }
        }

        var orphan = _commands.Values.FirstOrDefault(c => c.IsComposite && !_apps.ContainsKey(c.App!));
        if (orphan is not null)
        {
            throw Invalid($"Command {orphan.Name} belongs to app {orphan.App}, which is not declared.");
        }

        foreach (var type in types)
        {
            if (type.GetCustomAttribute<ReadingFunctionAttribute>() is not { } attribute)
            {
                continue;
            }

            var contract = type.GetInterfaces()
                .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadingFunction<,>))
                ?? throw Invalid($"Reading function {type.Name} must implement IReadingFunction<TParameters, TRow>.");
            if (!_apps.ContainsKey(attribute.App))
            {
                throw Invalid($"Reading function {type.Name} belongs to app {attribute.App}, which is not declared.");
            }

            _readingFunctions.Add(type, new ReadingFunctionModel(type, attribute.App, contract.GetGenericArguments()[0],
                contract.GetGenericArguments()[1]));
        }
    }

    public EntityModel? EntityOf(Type type) => _entities.GetValueOrDefault(type);

    public FacetModel? FacetOf(Type type) => _facets.GetValueOrDefault(type);

    /// <summary>Domain của một entity hoặc facet.</summary>
    public ModelDomain DomainOf(Type modelType) =>
        EntityOf(modelType)?.Block.Domain
        ?? FacetOf(modelType)?.Block.Domain
        ?? ReadingModelOf(modelType)?.Block.Domain
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
