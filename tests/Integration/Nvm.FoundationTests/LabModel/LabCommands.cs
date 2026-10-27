using Nvm.Foundation;

namespace Nvm.FoundationTests.LabModel;

[Command<LabMasterBlock>]
public sealed class CreateLabMaterial : ICommand<CreatedResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? UnitOfMeasure { get; init; }
}

public sealed class CreateLabMaterialHandler : ICommandHandler<CreateLabMaterial, CreatedResponse>
{
    public CreatedResponse Handle(CreateLabMaterial command, IPlatform platform)
    {
        var material = platform.Create<LabMaterial>();
        material.NId = command.NId!;
        material.UnitOfMeasure = command.UnitOfMeasure;
        platform.Submit(material);
        return new CreatedResponse { Id = material.Id };
    }
}

[Command<LabOperationBlock>]
public sealed class CreateLabOrder : ICommand<CreatedResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    public decimal? Quantity { get; init; }
}

public sealed class CreateLabOrderHandler : ICommandHandler<CreateLabOrder, CreatedResponse>
{
    public CreatedResponse Handle(CreateLabOrder command, IPlatform platform)
    {
        var response = new CreatedResponse();

        // Master data thuộc domain khác: đọc bằng ProjectionQuery, ngoài transaction.
        if (!platform.ProjectionQuery<LabMaterial>().Any(m => m.NId == command.MaterialNId))
        {
            response.SetError(2001, $"Material {command.MaterialNId} does not exist.");
            return response;
        }

        var order = platform.Create<LabOrder>();
        order.NId = command.NId!;
        order.MaterialNId = command.MaterialNId;
        order.Quantity = command.Quantity;
        platform.Submit(order);
        platform.RaiseEventOnCommit(new LabOrderCreated(order.Id, order.NId));
        response.Id = order.Id;
        return response;
    }
}

[Command<LabOperationBlock>]
public sealed class DeleteLabOrder : ICommand<LabResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

public sealed class DeleteLabOrderHandler : ICommandHandler<DeleteLabOrder, LabResponse>
{
    public LabResponse Handle(DeleteLabOrder command, IPlatform platform)
    {
        var order = platform.Query<LabOrder>().Single(o => o.NId == command.NId);
        platform.Delete(order);
        return new LabResponse();
    }
}

[Command<LabOperationBlock>]
public sealed class AddLabOrderNote : ICommand<LabResponse>
{
    [Mandatory]
    public string? OrderNId { get; init; }

    public string? Text { get; init; }

    public bool Fail { get; init; }
}

public sealed class AddLabOrderNoteHandler : ICommandHandler<AddLabOrderNote, LabResponse>
{
    public LabResponse Handle(AddLabOrderNote command, IPlatform platform)
    {
        var note = platform.Create<LabOrderNote>();
        note.NId = command.OrderNId + "-note";
        note.OrderNId = command.OrderNId;
        note.Text = command.Text;
        platform.Submit(note);

        var response = new LabResponse();
        if (command.Fail)
        {
            response.SetError(3001, "The note was rejected.");
        }

        return response;
    }
}

/// <summary>Chuỗi CallCommand trong FB: tạo order rồi thêm note, cùng domain nên cùng một transaction.</summary>
[Command<LabOperationBlock>]
public sealed class CreateLabOrderWithNote : ICommand<LabResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    public bool FailNote { get; init; }

    public bool IgnoreChildFailure { get; init; }
}

public sealed class CreateLabOrderWithNoteHandler : ICommandHandler<CreateLabOrderWithNote, LabResponse>
{
    public LabResponse Handle(CreateLabOrderWithNote command, IPlatform platform)
    {
        var response = new LabResponse();
        var order = platform.CallCommand<CreateLabOrder, CreatedResponse>(
            new CreateLabOrder { NId = command.NId, MaterialNId = command.MaterialNId, Quantity = 1 });
        if (!order.Succeeded)
        {
            response.SetError(order.Error!.ErrorCode, order.Error.ErrorMessage);
            return response;
        }

        var note = platform.CallCommand<AddLabOrderNote, LabResponse>(
            new AddLabOrderNote { OrderNId = command.NId, Text = "first note", Fail = command.FailNote });
        if (!note.Succeeded && !command.IgnoreChildFailure)
        {
            platform.Tracer.Write(TraceLevel.Error, "Note failed: " + note.Error!.ErrorMessage);
            response.SetError(note.Error.ErrorCode, "There was an error creating the order. See the trace.");
        }

        return response;
    }
}

/// <summary>CallCommand sang domain khác: command con có transaction riêng và commit ngay.</summary>
[Command<LabOperationBlock>]
public sealed class CreateLabOrderWithMaterial : ICommand<LabResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    public bool FailAfterMaterial { get; init; }
}

public sealed class CreateLabOrderWithMaterialHandler : ICommandHandler<CreateLabOrderWithMaterial, LabResponse>
{
    public LabResponse Handle(CreateLabOrderWithMaterial command, IPlatform platform)
    {
        var response = new LabResponse();
        var material = platform.CallCommand<CreateLabMaterial, CreatedResponse>(
            new CreateLabMaterial { NId = command.MaterialNId, UnitOfMeasure = "pcs" });
        if (!material.Succeeded || command.FailAfterMaterial)
        {
            response.SetError(6001, "Stopped after the material step.");
        }

        return response;
    }
}

[Command<LabOperationBlock>]
public sealed class SetLabOrderSupplier : ICommand<LabResponse>
{
    [Mandatory]
    public string? OrderNId { get; init; }

    [Mandatory]
    public string? SupplierCode { get; init; }
}

public sealed class SetLabOrderSupplierHandler : ICommandHandler<SetLabOrderSupplier, LabResponse>
{
    public LabResponse Handle(SetLabOrderSupplier command, IPlatform platform)
    {
        var response = new LabResponse();
        if (!command.SupplierCode!.StartsWith("SUP-", StringComparison.Ordinal))
        {
            response.SetError(4001, $"Supplier {command.SupplierCode} is unknown.");
            return response;
        }

        var order = platform.Query<LabOrder>().Single(o => o.NId == command.OrderNId);
        var supplier = platform.Query<LabOrderSupplier>().SingleOrDefault(f => f.EntityId == order.Id)
            ?? platform.Create<LabOrderSupplier>();
        supplier.EntityId = order.Id;
        supplier.SupplierCode = command.SupplierCode;
        platform.Submit(supplier);
        return response;
    }
}

/// <summary>Đếm order cùng NId trong transaction (Query) và ngoài transaction (ProjectionQuery) sau khi vừa Submit.</summary>
[Command<LabOperationBlock>]
public sealed class CreateAndCountLabOrders : ICommand<CountResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

public sealed class CreateAndCountLabOrdersHandler : ICommandHandler<CreateAndCountLabOrders, CountResponse>
{
    public CountResponse Handle(CreateAndCountLabOrders command, IPlatform platform)
    {
        var order = platform.Create<LabOrder>();
        order.NId = command.NId!;
        order.MaterialNId = "any";
        platform.Submit(order);
        return new CountResponse
        {
            InTransaction = platform.Query<LabOrder>().Count(o => o.NId == command.NId),
            Projection = platform.ProjectionQuery<LabOrder>().Count(o => o.NId == command.NId),
        };
    }
}

/// <summary>Dùng nhầm model: Query entity master data trong command vận hành.</summary>
[Command<LabOperationBlock>]
public sealed class ReadMaterialWithQuery : ICommand<LabResponse>
{
    public string? MaterialNId { get; init; }
}

public sealed class ReadMaterialWithQueryHandler : ICommandHandler<ReadMaterialWithQuery, LabResponse>
{
    public LabResponse Handle(ReadMaterialWithQuery command, IPlatform platform)
    {
        // Cố ý sai để kiểm chốt chặn lúc chạy; lúc build NVM004 bắt đúng dòng này.
#pragma warning disable NVM004
        _ = platform.Query<LabMaterial>().Any(m => m.NId == command.MaterialNId);
#pragma warning restore NVM004
        return new LabResponse();
    }
}
