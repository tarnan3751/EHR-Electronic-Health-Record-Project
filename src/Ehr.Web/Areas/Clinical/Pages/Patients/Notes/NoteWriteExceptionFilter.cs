using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// Runs AFTER the page transaction filter has rolled back its failed transaction. Never turn a database exception
// into a successful handler result inside that transaction: PostgreSQL has already aborted it.
public sealed class NoteWriteExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.ActionDescriptor is not PageActionDescriptor page
            || !page.ViewEnginePath.StartsWith("/Patients/Notes/", StringComparison.Ordinal))
        {
            return;
        }

        if (context.Exception is not DbUpdateConcurrencyException
            && context.Exception is not DbUpdateException
            {
                InnerException: PostgresException
                { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation }
            })
        {
            return;
        }

        const string message = "The saved draft changed, was signed, or is no longer eligible for signing. Your displayed work is still here. Open the chart in another tab and compare before retrying.";
        context.Result = context.HttpContext.Request.Headers.ContainsKey("HX-Request")
            ? new JsonResult(new { errors = new[] { message } }) { StatusCode = 409 }
            : new ContentResult { Content = message, StatusCode = 409 };
        context.ExceptionHandled = true;
    }
}
