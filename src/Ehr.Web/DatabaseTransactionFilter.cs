using Ehr.Data;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ehr.Web;

// Runs each page handler in one database transaction, committed if the handler succeeds. UserContextInterceptor
// (Ehr.Data) makes setting the user context its first statement. After a request that can change data, the response
// reports how far the primary's WAL has got, so the cloud app can tell when its replica has the change.
public sealed class DatabaseTransactionFilter(EhrDbContext db) : IAsyncPageFilter
{
    public const string WalPositionHeader = "Ehr-Wal-Position";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(context.HttpContext.RequestAborted);

        var executed = await next();

        // Not cancellable: once the handler has finished, its work is kept even if the browser has gone.
        if (executed.Exception is null || executed.ExceptionHandled)
        {
            await transaction.CommitAsync(CancellationToken.None);

            var method = context.HttpContext.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            {
                context.HttpContext.Response.Headers[WalPositionHeader] =
                    await WalPosition.OfPrimaryAsync(db, CancellationToken.None);
            }
        }
    }
}
