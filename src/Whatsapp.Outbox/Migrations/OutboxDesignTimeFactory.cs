using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whatsapp.Outbox;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the API. The connection string is only
/// used to pick the provider; nothing connects to it.
/// </summary>
internal sealed class OutboxDesignTimeFactory : IDesignTimeDbContextFactory<OutboxDbContext>
{
    public OutboxDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OutboxDbContext>()
            .UseNpgsql("Host=localhost;Database=design-time-only;Username=design;Password=design-time-only")
            .Options);
}
