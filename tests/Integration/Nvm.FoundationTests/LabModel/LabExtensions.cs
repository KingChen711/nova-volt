using Nvm.Foundation;

namespace Nvm.FoundationTests.LabModel;

// Phần "của team": quy tắc gắn vào CreateLabOrder của block nhà cung cấp mà không sửa handler của nó.

/// <summary>Chỉ đọc; bật ở LabApp, không bật ở OtherApp.</summary>
[PreCheck<LabExtensionBlock>]
public sealed class RejectNegativeQuantity : IPreCheck<CreateLabOrder>
{
    public PreCheckResponse Check(CreateLabOrder command, IReadOnlyPlatform platform)
    {
        var response = new PreCheckResponse();
        if (command.Quantity < 0)
        {
            response.SetError(1001, "Quantity must not be negative.");
        }

        return response;
    }
}

/// <summary>Tùy chọn: ghi facet đếm; cấu hình solution tắt được.</summary>
[PostAction<LabExtensionBlock>(Optional = true)]
public sealed class CountOrderCreation : IPostAction<CreateLabOrder, CreatedResponse>
{
    public CreatedResponse Execute(CreateLabOrder command, CreatedResponse response, IPlatform platform)
    {
        var counters = platform.Create<LabOrderCounters>();
        counters.EntityId = response.Id;
        counters.CreatedCount = 1;
        platform.Submit(counters);
        return response;
    }
}

/// <summary>Bắt buộc: chặn order có NId bắt đầu bằng POISON sau khi handler đã ghi.</summary>
[PostAction<LabExtensionBlock>]
public sealed class RejectPoisonOrder : IPostAction<CreateLabOrder, CreatedResponse>
{
    public CreatedResponse Execute(CreateLabOrder command, CreatedResponse response, IPlatform platform)
    {
        if (command.NId!.StartsWith("POISON", StringComparison.Ordinal))
        {
            response.SetError(5001, "Poison orders are not allowed.");
        }

        return response;
    }
}

/// <summary>Composite của app: tạo order rồi gán nhà cung cấp, hai transaction riêng.</summary>
[AppCommand(LabApp.Name)]
public sealed class RegisterLabOrderWithSupplier : ICommand<LabResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public string? SupplierCode { get; init; }
}

public sealed class RegisterLabOrderWithSupplierHandler : ICompositeCommandHandler<RegisterLabOrderWithSupplier, LabResponse>
{
    public LabResponse Handle(RegisterLabOrderWithSupplier command, ICompositePlatform platform)
    {
        var response = new LabResponse();
        var order = platform.CallCommand<CreateLabOrder, CreatedResponse>(
            new CreateLabOrder { NId = command.NId, MaterialNId = command.MaterialNId, Quantity = 1 });
        if (!order.Succeeded)
        {
            response.SetError(order.Error!.ErrorCode, "The order could not be created.");
            return response;
        }

        var supplier = platform.CallCommand<SetLabOrderSupplier, LabResponse>(
            new SetLabOrderSupplier { OrderNId = command.NId, SupplierCode = command.SupplierCode });
        if (!supplier.Succeeded)
        {
            response.SetError(supplier.Error!.ErrorCode, "There was an error updating the order supplier: " + supplier.Error.ErrorMessage);
        }

        return response;
    }
}
