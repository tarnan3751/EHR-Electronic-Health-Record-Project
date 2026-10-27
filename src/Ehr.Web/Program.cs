using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Domain.Audit;
using Ehr.Domain.Authorization;
using Ehr.Domain.QuickTexts;
using Ehr.Web;
using Ehr.Web.Api.Desktop;
using Ehr.Web.Api.Sync;
using Ehr.Web.Email;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// One binary runs on both servers. These two settings say which server this is and which database it uses.
var site = builder.Configuration.GetValue<Site?>("Ehr:Site")
    ?? throw new InvalidOperationException("Set Ehr:Site to OnPrem or Cloud.");
var connectionString = builder.Configuration.GetConnectionString("Ehr")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Ehr. See \"Running the apps\" in README.md.");

builder.Services.AddEhrData(connectionString);
builder.Services.AddScoped<QuickTextSaver>();

// Accounts, sign-in and the authorization policies (Ehr.Domain). The audit log names which server recorded an event.
builder.Services.AddEhrAccounts(builder.Configuration);
builder.Services.AddSingleton(new AuditSource(site == Site.OnPrem ? AuditSources.OnPrem : AuditSources.Cloud));

// The portal's emails: through the mail server in Ehr:Smtp when there is one, otherwise into the log, for development.
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.Section));
if (builder.Configuration[$"{SmtpOptions.Section}:Host"] is { Length: > 0 })
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
}

// The keys that protect form tokens live in the database, so tokens survive a restart and both servers share the
// keys: a form the cloud app renders is saved by the on-prem app. Only on-prem creates them; the cloud app reads them
// from the replica.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Ehr")
    .PersistKeysToDbContext<EhrDbContext>();
if (site == Site.OnPrem)
{
    // The first admin account, when there are none. Only the primary can write it.
    builder.Services.AddHostedService<AdminBootstrap>();
}
else
{
    dataProtection.DisableAutomaticKeyGeneration();

    // The cloud app saves nothing itself; it forwards those requests to the on-prem app.
    builder.Services.AddHttpForwarder();
    builder.Services.AddSingleton(OnPremApp.FromConfiguration(builder.Configuration));
    builder.Services.AddSingleton<LastSaveCookie>();
}

builder.Services.AddRazorPages(options =>
{
    // Authorization attaches to whole areas, never handler by handler (AGENTS.md). The Account folders, where people
    // sign in and register, are the only anonymous pages. The database's row-level security is the second layer.
    options.Conventions.AuthorizeAreaFolder("Clinical", "/", EhrPolicies.Staff);
    options.Conventions.AuthorizeAreaFolder("Clinical", "/Patients", EhrPolicies.Clinician);
    options.Conventions.AuthorizeAreaFolder("Clinical", "/Admin", EhrPolicies.Admin);
    options.Conventions.AllowAnonymousToAreaFolder("Clinical", "/Account");
    options.Conventions.AuthorizeAreaFolder("Portal", "/", EhrPolicies.Patient);
    options.Conventions.AllowAnonymousToAreaFolder("Portal", "/Account");
})
    .AddMvcOptions(options => options.Filters.Add<DatabaseTransactionFilter>())
    // Validation runs on the server only, so pages don't need the jQuery validation attributes.
    .AddViewOptions(options => options.HtmlHelperOptions.ClientValidationEnabled = false);

var app = builder.Build();

// Routing first, so the cloud app's forwarding can tell page requests from static files. Forwarded requests are
// authenticated and authorized by the on-prem app; everything served here is authenticated below, and the signed-in
// account reaches the database through UserContextMiddleware before any handler runs.
app.UseRouting();
if (site == Site.Cloud)
{
    app.UseMiddleware<CloudForwardingMiddleware>();
}

app.UseAuthentication();
app.UseMiddleware<UserContextMiddleware>();
app.UseAuthorization();

// Static files are the same for everyone, signed in or not.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();
// The desktop app signs in at /api/desktop and sends the session token it gets with every sync request. Both APIs
// take the Desktop policy: a clinician's session token, and nothing else (Api/Desktop, Api/Sync).
app.MapDesktopApi();
app.MapQuickTextSync().RequireAuthorization(EhrPolicies.Desktop);

app.Run();
