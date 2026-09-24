using Nvm.ProductionExecution.Commands;

namespace Nvm.ArchitectureTests;

public sealed class ProductionExecutionBoundaryTests
{
    [Fact]
    public void ProductionExecutionDoesNotDependOnStorageTransportOrAnotherBlock()
    {
        var references = NvmAssemblies.NamesReferencedBy(typeof(RecordDataCollectionCommand).Assembly);
        references.Where(name => name.StartsWith("Nvm.", StringComparison.Ordinal))
            .Except(["Nvm.Kernel", "Nvm.Contracts", "Nvm.Time"], StringComparer.Ordinal).ShouldBeEmpty();
        references.ShouldNotContain("Microsoft.Data.SqlClient");
        references.ShouldNotContain("Microsoft.EntityFrameworkCore");
        references.ShouldNotContain("Npgsql");
        references.ShouldNotContain("MassTransit");
        references.ShouldContain("Nvm.Kernel");
    }
}
