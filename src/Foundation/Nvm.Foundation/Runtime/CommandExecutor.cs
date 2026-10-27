using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nvm.Foundation.Runtime;

/// <summary>Kết quả trả cho người gọi qua service layer: mã HTTP và thân JSON như nền thật trả.</summary>
public sealed record CommandResult(int StatusCode, JsonObject Body, CommandResponse? Response, Guid CorrelationId)
{
    public TResponse As<TResponse>()
        where TResponse : CommandResponse =>
        Response as TResponse ?? throw new InvalidOperationException($"No {typeof(TResponse).Name} in this result.");
}

/// <summary>Pre-check chặn command. Chỉ đi lên tới service layer hoặc composite; transaction của command đã rollback.</summary>
internal sealed class PreCheckRejectedException(string preCheck, ExecutionError error)
    : Exception($"Pre-check '{preCheck}' rejected the command: {error.ErrorMessage}")
{
    public string PreCheck { get; } = preCheck;

    public ExecutionError Error { get; } = error;
}

/// <summary>Service layer và pipeline: kiểm model → pre-check (theo app) → handler → post-action → commit → phát event.</summary>
public sealed class CommandExecutor
{
    /// <summary>Lỗi kiểm model ở service layer: thiếu tham số bắt buộc, handler không chạy.</summary>
    public const string ValidationErrorCode = "6400";

    private static readonly JsonSerializerOptions BodyJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly SolutionConfiguration _solution;
    private readonly ITraceSink _traces;
    private readonly IEventPublisher _events;

    public CommandExecutor(
        string connectionString,
        ModelRegistry registry,
        SolutionConfiguration solution,
        TimeProvider time,
        ITraceSink traces,
        IEventPublisher events)
    {
        _connectionString = connectionString;
        Registry = registry;
        _solution = solution;
        Time = time;
        _traces = traces;
        _events = events;
    }

    public ModelRegistry Registry { get; }

    public TimeProvider Time { get; }

    /// <summary>Gọi command qua một app, như POST tới command trên OData của app đó.</summary>
    public CommandResult Execute(string appName, object command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var call = CallInfo.Root();
        var model = Registry.CommandOf(command.GetType());
        var app = _solution.App(appName);
        var tracer = new CallTracer(_traces, Time, call, model.FullName);
        using var activity = CallTracer.Source.StartActivity(model.FullName);
        activity?.SetTag("nvm.correlation_id", call.CorrelationId);
        tracer.Platform(TraceLevel.Information, $"Received {model.FullName} via app {appName}");

        var published = string.Equals(model.App, appName, StringComparison.Ordinal) || app.Commands.Contains(model.Type);
        if (!published)
        {
            tracer.Platform(TraceLevel.Warning, $"App {appName} does not publish {model.Name}");
            return ServiceError(404, "CommandNotPublished", $"Command '{model.Name}' is not published by app '{appName}'.", call);
        }

        if (MissingMandatory(model, command) is { } missing)
        {
            tracer.Platform(TraceLevel.Warning, $"Model validation failed: {missing} is required");
            return ServiceError(400, ValidationErrorCode, $"Model validation failed: '{missing}' is required.", call);
        }

        using var scope = new ExecutionScope(_connectionString, Registry, Time);
        try
        {
            var response = model.IsComposite
                ? RunComposite(model, command, app, scope, tracer)
                : RunInNewTransaction(model, command, app, scope, tracer);
            var body = JsonSerializer.SerializeToNode(response, response.GetType(), BodyJson)!.AsObject();
            return new CommandResult(response.Succeeded ? 200 : 400, body, response, call.CorrelationId);
        }
        catch (PreCheckRejectedException e)
        {
            var body = new JsonObject
            {
                ["Succeeded"] = false,
                ["Error"] = new JsonObject { ["ErrorCode"] = e.Error.ErrorCode, ["ErrorMessage"] = e.Message },
                ["ExecutionDetail"] = new JsonObject
                {
                    ["PreCheckDetails"] = new JsonArray(new JsonObject
                    {
                        ["PreCheck"] = e.PreCheck,
                        ["ErrorCode"] = e.Error.ErrorCode,
                        ["ErrorMessage"] = e.Error.ErrorMessage,
                    }),
                },
            };
            return new CommandResult(400, body, null, call.CorrelationId);
        }
    }

    /// <summary><c>CallCommand</c> trong handler: cùng domain thì chung transaction, khác domain thì transaction riêng.</summary>
    internal CommandResponse CallFromHandler(object command, DomainTransaction transaction, ExecutionScope scope, CallInfo parent)
    {
        var model = Registry.CommandOf(command.GetType());
        if (model.IsComposite)
        {
            throw new ModelMisuseException($"{model.Name} is an app command; FB handlers cannot call it.");
        }

        var tracer = new CallTracer(_traces, Time, parent.Child(), model.FullName);
        if (MissingMandatory(model, command) is { } missing)
        {
            return Failure(model, int.Parse(ValidationErrorCode, CultureInfo.InvariantCulture), $"Model validation failed: '{missing}' is required.");
        }

        // Không qua app nên không có pre-check: pre-check bật theo app (giả định, ghi trong oef-mapping).
        var response = model.Block!.Domain == transaction.Domain
            ? RunInTransaction(model, command, app: null, transaction, scope, tracer)
            : RunInNewTransaction(model, command, app: null, scope, tracer);

        if (!response.Succeeded && _solution.ChildFailureDoomsTransaction)
        {
            transaction.Doom(response.Error!);
        }

        return response;
    }

    /// <summary><c>CallCommand</c> trong composite: transaction riêng, chạy pre-check mà app của composite đã bật.</summary>
    internal CommandResponse CallFromComposite(object command, AppConfiguration app, ExecutionScope scope, CallInfo parent)
    {
        var model = Registry.CommandOf(command.GetType());
        if (model.IsComposite)
        {
            throw new ModelMisuseException($"{model.Name} is an app command; composites call FB commands.");
        }

        var tracer = new CallTracer(_traces, Time, parent.Child(), model.FullName);
        if (MissingMandatory(model, command) is { } missing)
        {
            return Failure(model, int.Parse(ValidationErrorCode, CultureInfo.InvariantCulture), $"Model validation failed: '{missing}' is required.");
        }

        try
        {
            return RunInNewTransaction(model, command, app, scope, tracer);
        }
        catch (PreCheckRejectedException e)
        {
            return Failure(model, e.Error.ErrorCode, e.Message);
        }
    }

    private CommandResponse RunComposite(CommandModel model, object command, AppConfiguration app, ExecutionScope scope, CallTracer tracer)
    {
        tracer.Platform(TraceLevel.Information, $"Composite {model.FullName} started");
        CommandResponse response;
        try
        {
            response = Invoke(typeof(ICompositeCommandHandler<,>), "Handle", model, command, new CompositePlatform(this, app, scope, tracer));
        }
        catch (Exception e) when (e is not PreCheckRejectedException)
        {
            tracer.Platform(TraceLevel.Error, $"Composite handler failed: {e.GetType().Name}: {e.Message}");
            response = Failure(model, e.HResult, e.Message);
        }

        // Không có transaction bao ngoài: các lời gọi con đã commit thì vẫn còn.
        tracer.Platform(TraceLevel.Information, $"Composite {model.FullName} finished, Succeeded = {response.Succeeded}");
        return response;
    }

    private CommandResponse RunInNewTransaction(CommandModel model, object command, AppConfiguration? app, ExecutionScope scope, CallTracer tracer)
    {
        using var transaction = new DomainTransaction(_connectionString, Registry, Time, model.Block!.Domain);
        tracer.Platform(TraceLevel.Verbose, $"Transaction opened ({transaction.Domain})");
        CommandResponse response;
        try
        {
            response = RunInTransaction(model, command, app, transaction, scope, tracer);
        }
        catch (PreCheckRejectedException)
        {
            transaction.Rollback();
            tracer.Platform(TraceLevel.Information, "Transaction rolled back");
            throw;
        }

        if (!response.Succeeded || transaction.DoomedBy is not null)
        {
            transaction.Rollback();
            tracer.Platform(TraceLevel.Information, "Transaction rolled back");
            return response.Succeeded
                ? Failure(model, transaction.DoomedBy!.ErrorCode, transaction.DoomedBy.ErrorMessage)
                : response;
        }

        IReadOnlyList<PublishedEvent> events;
        try
        {
            tracer.Platform(TraceLevel.Verbose, "Commit started");
            events = transaction.Commit();
            tracer.Platform(TraceLevel.Information, "Commit completed");
        }
#pragma warning disable CA1031 // mọi lỗi lúc commit đều kết thúc bằng rollback và một dòng trace, không lọt ra ngoài
        catch (Exception e)
#pragma warning restore CA1031
        {
            tracer.Platform(TraceLevel.Error, $"Commit failed: {e.GetType().Name}: {e.Message}");
            transaction.Rollback();
            tracer.Platform(TraceLevel.Error, "Transaction rolled back");
            return _solution.CommitFailureReporting == CommitFailureReporting.Failure
                ? Failure(model, e.HResult, "The changes could not be saved. See the trace for details.")
                : response;
        }

        if (events.Count > 0)
        {
            _events.Publish(events);
            tracer.Platform(TraceLevel.Verbose, $"Published {events.Count} event(s)");
        }

        return response;
    }

    private CommandResponse RunInTransaction(
        CommandModel model,
        object command,
        AppConfiguration? app,
        DomainTransaction transaction,
        ExecutionScope scope,
        CallTracer tracer)
    {
        using var activity = tracer.Call.ParentId is null ? null : CallTracer.Source.StartActivity(model.FullName);
        tracer.Platform(TraceLevel.Information, $"Command {model.FullName} started");

        if (app is not null)
        {
            foreach (var preCheck in Registry.PreChecks.Where(p => p.Command == model && app.EnabledPreChecks.Contains(p.Name)))
            {
                var readOnly = new ReadOnlyPlatform(this, transaction, scope, tracer);
                PreCheckResponse result;
                try
                {
                    var instance = Activator.CreateInstance(preCheck.Type)!;
                    result = (PreCheckResponse)typeof(IPreCheck<>).MakeGenericType(model.Type).GetMethod("Check")!
                        .Invoke(instance, BindingFlags.DoNotWrapExceptions, null, [command, readOnly], CultureInfo.InvariantCulture)!;
                }
#pragma warning disable CA1031 // pre-check ném lỗi cũng là chặn command
                catch (Exception e)
#pragma warning restore CA1031
                {
                    result = new PreCheckResponse();
                    result.SetError(e.HResult, e.Message);
                }

                if (!result.Succeeded)
                {
                    tracer.Platform(TraceLevel.Warning, $"Pre-check {preCheck.Name} rejected {model.Name}: {result.Error!.ErrorMessage}");
                    throw new PreCheckRejectedException(preCheck.Name, result.Error);
                }

                tracer.Platform(TraceLevel.Verbose, $"Pre-check {preCheck.Name} passed");
            }
        }

        var platform = new Platform(this, transaction, scope, tracer);
        CommandResponse response;
        try
        {
            response = Invoke(typeof(ICommandHandler<,>), "Handle", model, command, platform);
        }
#pragma warning disable CA1031 // handler ném lỗi thì command thất bại, như nền thật; lý do nằm ở trace
        catch (Exception e)
#pragma warning restore CA1031
        {
            tracer.Platform(TraceLevel.Error, $"Handler failed: {e.GetType().Name}: {e.Message}");
            response = Failure(model, e.HResult, e.Message);
        }

        if (response.Succeeded)
        {
            foreach (var postAction in Registry.PostActions.Where(p => p.Command == model))
            {
                if (postAction.Optional && _solution.DisabledPostActions.Contains(postAction.Name))
                {
                    tracer.Platform(TraceLevel.Verbose, $"Post-action {postAction.Name} disabled");
                    continue;
                }

                try
                {
                    var instance = Activator.CreateInstance(postAction.Type)!;
                    response = (CommandResponse)typeof(IPostAction<,>).MakeGenericType(model.Type, model.ResponseType)
                        .GetMethod("Execute")!
                        .Invoke(instance, BindingFlags.DoNotWrapExceptions, null, [command, response, platform], CultureInfo.InvariantCulture)!;
                }
#pragma warning disable CA1031 // post-action ném lỗi thì cả command thất bại
                catch (Exception e)
#pragma warning restore CA1031
                {
                    response = Failure(model, e.HResult, e.Message);
                }

                if (!response.Succeeded)
                {
                    tracer.Platform(TraceLevel.Warning, $"Post-action {postAction.Name} failed: {response.Error!.ErrorMessage}");
                    break;
                }
            }
        }

        tracer.Platform(response.Succeeded ? TraceLevel.Information : TraceLevel.Warning,
            $"Command {model.FullName} finished, Succeeded = {response.Succeeded}");
        return response;
    }

    private static CommandResponse Invoke(Type contract, string method, CommandModel model, object command, object platform)
    {
        var handler = Activator.CreateInstance(model.HandlerType)!;
        return (CommandResponse)contract.MakeGenericType(model.Type, model.ResponseType).GetMethod(method)!
            .Invoke(handler, BindingFlags.DoNotWrapExceptions, null, [command, platform], CultureInfo.InvariantCulture)!;
    }

    private static CommandResponse Failure(CommandModel model, int code, string message)
    {
        var response = (CommandResponse)Activator.CreateInstance(model.ResponseType)!;
        response.SetError(code, message);
        return response;
    }

    private static string? MissingMandatory(CommandModel model, object command) =>
        model.Parameters.FirstOrDefault(p => p.Mandatory && p.Property.GetValue(command) is null or "")?.Name;

    private static CommandResult ServiceError(int status, string code, string message, CallInfo call) =>
        new(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, null, call.CorrelationId);
}
