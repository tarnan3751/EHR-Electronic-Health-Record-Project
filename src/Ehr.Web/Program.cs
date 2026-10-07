using Ehr.Data;
using Ehr.Web;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// One binary runs on both servers. These two settings say which server this is and which database it uses.
var site = builder.Configuration.GetValue<Site?>("Ehr:Site")
    ?? throw new InvalidOperationException("Set Ehr:Site to OnPrem or Cloud.");
var connectionString = builder.Configuration.GetConnectionString("Ehr")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Ehr. See \"Running the apps\" in README.md.");

builder.Services.AddEhrData(connectionString);

// The keys that protect form tokens live in the database, so tokens survive a restart and both servers share the
// keys: a form the cloud app renders is saved by the on-prem app. Only on-prem creates them; the cloud app reads them
// from the replica.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Ehr")
    .PersistKeysToDbContext<EhrDbContext>();
if (site == Site.Cloud)
{
    dataProtection.DisableAutomaticKeyGeneration();

    // The cloud app saves nothing itself; it forwards those requests to the on-prem app.
    builder.Services.AddHttpForwarder();
    builder.Services.AddSingleton(OnPremApp.FromConfiguration(builder.Configuration));
    builder.Services.AddSingleton<LastSaveCookie>();
}

builder.Services.AddRazorPages()
    .AddMvcOptions(options => options.Filters.Add<DatabaseTransactionFilter>())
    // Validation runs on the server only, so pages don't need the jQuery validation attributes.
    .AddViewOptions(options => options.HtmlHelperOptions.ClientValidationEnabled = false);

var app = builder.Build();

// Routing first, so the cloud app's forwarding can tell page requests from static files.
app.UseRouting();
if (site == Site.Cloud)
{
    app.UseMiddleware<CloudForwardingMiddleware>();
}

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
