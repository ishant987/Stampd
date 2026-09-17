using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Infrastructure.DependencyInjection;

namespace Stampd.Infrastructure.MySql;

/// <summary>
/// Wires the Stampd DbContext onto MySQL so the CRM and Stampd share one database service.
/// </summary>
public static class StampdMySqlServiceCollectionExtensions
{
    public static IServiceCollection AddStampdMySql(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddStampdDbContext(options =>
        {
            options.UseMySQL(connectionString);
        });
    }
}
