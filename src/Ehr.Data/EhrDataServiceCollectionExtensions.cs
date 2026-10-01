using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ehr.Data;

public static class EhrDataServiceCollectionExtensions
{
    // The apps' database access: an EhrDbContext and a UserContext per request, with every transaction starting
    // by setting the user context. The connection string names the runtime role: ehr_app or ehr_read.
    public static IServiceCollection AddEhrData(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<UserContext>();
        services.AddDbContext<EhrDbContext>((provider, options) =>
        {
            EhrDbContext.Configure(options, connectionString);
            options.AddInterceptors(new UserContextInterceptor(provider.GetRequiredService<UserContext>()));
        });
        return services;
    }
}
