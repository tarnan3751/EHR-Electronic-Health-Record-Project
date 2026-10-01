using Ehr.Data;
using Ehr.Web;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// One binary runs on both servers. These two settings say which server this is and which database it uses.
var site = builder.Configuration.GetValue<Site?>("Ehr:Site")
    ?? throw new InvalidOperationException("Set Ehr:Site to OnPrem or Cloud.");
var connectionString = builder.Configuration.GetConnectionString("Ehr")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Ehr. See \"Running the apps\" in README.md.");

builder.Services.AddSingleton(new CurrentSite(site));
builder.Services.AddEhrData(connectionString);

// The keys that protect form tokens live in the database, so tokens survive a restart and both servers can share
// the keys. Only on-prem creates them; the cloud app reads them from the replica.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Ehr")
    .PersistKeysToDbContext<EhrDbContext>();
if (site == Site.Cloud)
{
    dataProtection.DisableAutomaticKeyGeneration();
}

builder.Services.AddRazorPages()
    .AddMvcOptions(options => options.Filters.Add<DatabaseTransactionFilter>())
    // Validation runs on the server only, so pages don't need the jQuery validation attributes.
    .AddViewOptions(options => options.HtmlHelperOptions.ClientValidationEnabled = false);

var app = builder.Build();

// The cloud app can't save yet, so every request that would change data is refused before it reaches a page.
// Forwarding these requests to the on-prem app will replace this.
if (site == Site.Cloud)
{
    app.Use((context, next) =>
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "GET, HEAD";
        return Task.CompletedTask;
    });
}

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
