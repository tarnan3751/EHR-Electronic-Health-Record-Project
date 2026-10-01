using Ehr.Data;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ehr.Web;

// Runs each page handler in one database transaction, committed if the handler succeeds. UserContextInterceptor
// (Ehr.Data) makes setting the user context its first statement.
public sealed class DatabaseTransactionFilter(EhrDbContext db) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(context.HttpContext.RequestAborted);

        var executed = await next();

        // Not cancellable: once the handler has finished, its work is kept even if the browser has gone.
        if (executed.Exception is null || executed.ExceptionHandled)
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
    }
}
