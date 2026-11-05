using Fudis.Payments.Infra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fudis.Payments.Infra;

/// <summary>Used by `dotnet ef` so migrations can be created without booting the whole application.</summary>
public class FudisDbContextFactory : IDesignTimeDbContextFactory<FudisDbContext>
{
    public FudisDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FudisDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FudisPayments;Trusted_Connection=True;MultipleActiveResultSets=True")
            .Options;

        return new FudisDbContext(options);
    }
}
