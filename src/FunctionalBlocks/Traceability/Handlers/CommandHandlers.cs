using Nvm.Kernel.Commands;
using Nvm.Traceability.Commands;

namespace Nvm.Traceability.Handlers;

public sealed class SerializeUnitHandler(TraceabilityCommandProcessor processor)
    : ICommandHandler<SerializeUnitCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(SerializeUnitCommand command, CancellationToken cancellationToken) =>
        processor.SerializeAsync(command, cancellationToken);
}

public sealed class StartStepHandler(TraceabilityCommandProcessor processor)
    : ICommandHandler<StartStepCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(StartStepCommand command, CancellationToken cancellationToken) =>
        processor.StartAsync(command, cancellationToken);
}

public sealed class CompleteStepHandler(TraceabilityCommandProcessor processor)
    : ICommandHandler<CompleteStepCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(CompleteStepCommand command, CancellationToken cancellationToken) =>
        processor.CompleteAsync(command, cancellationToken);
}

public sealed class RecordMeasurementHandler(TraceabilityCommandProcessor processor)
    : ICommandHandler<RecordMeasurementCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(RecordMeasurementCommand command, CancellationToken cancellationToken) =>
        processor.RecordMeasurementAsync(command, cancellationToken);
}
