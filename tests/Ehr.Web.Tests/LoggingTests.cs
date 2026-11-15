using System.Collections.Concurrent;
using System.Net;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Search terms and dates of birth travel in the query string, so the log lines that print an address with its query, the
// host's, the redirect after sign-in and YARP's, are off on both servers (appsettings.json), while warnings and errors,
// the apps' own included, stay on.
public class LoggingTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Request_addresses_stay_out_of_the_log_on_both_servers_and_warnings_stay_in()
    {
        foreach (var app in new[] { apps.OnPrem, apps.Cloud })
        {
            Assert.False(Logs(app, "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information));
            Assert.False(Logs(app, "Microsoft.AspNetCore.Mvc.Infrastructure.LocalRedirectResultExecutor", LogLevel.Information));
            Assert.False(Logs(app, "Microsoft.AspNetCore.Mvc.Infrastructure.RedirectResultExecutor", LogLevel.Information));
            Assert.False(Logs(app, "Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.Information));

            Assert.True(Logs(app, "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning));
            Assert.True(Logs(app, "Microsoft.AspNetCore.Mvc.Infrastructure.LocalRedirectResultExecutor", LogLevel.Warning));
            Assert.True(Logs(app, "Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.Warning));
            Assert.True(Logs(app, "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.Error));
            Assert.True(Logs(app, "Ehr.Web.CloudAudit.CloudAuditShipper", LogLevel.Warning));
            Assert.True(Logs(app, "Ehr.Web.Forwarding.CloudForwardingMiddleware", LogLevel.Warning));
            // Everything else as before, from Information.
            Assert.True(Logs(app, "Ehr.Domain.Accounts.AdminBootstrap", LogLevel.Information));
        }
    }

    // A search's whole round, as a browser makes it, with every log line both servers write captured: signed out and
    // back to the search after signing in, results as the clinician types, a chart through the reason it asks for, a
    // change to the panel on-site, and the same off-site, where the change is forwarded. None carries the criteria.
    [Fact]
    public async Task A_search_and_its_charts_leave_no_criteria_in_the_log()
    {
        var family = "Logless" + Guid.NewGuid().ToString("N")[..6];
        const string born = "1977-07-07";
        var patient = await apps.InsertPatientAsync("Ada", family, new DateOnly(1977, 7, 7));
        var clinician = await apps.CreateStaffAsync("Dr. Quiet", StaffRoles.Clinician);
        var logs = new CapturedLogs();
        await using var onPrem = apps.OnPrem.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs)));
        await using var cloud = apps.Cloud.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs)));
        var search = $"/patients?name={family}&birthdate={born}";
        var chart = $"/patients/{patient}?name={family}&birthdate={born}";
        var jar = new CookieContainer();
        var client = Client(onPrem, jar);

        using (var signIn = await client.GetAsync(search, Cancel))
        using (var code = await PostFormAsync(client, signIn.RequestMessage!.RequestUri!.PathAndQuery, signIn,
            ("Input.Email", clinician.Email), ("Input.Password", clinician.Password)))
        using (var results = await PostFormAsync(client, code.RequestMessage!.RequestUri!.PathAndQuery, code, ("Code", Totp.Code(clinician.AuthenticatorKey))))
        {
            Assert.Equal(search, results.RequestMessage!.RequestUri!.PathAndQuery);
        }

        using (var typed = new HttpRequestMessage(HttpMethod.Get, $"/patients?handler=Results&name={family}&birthdate={born}"))
        {
            typed.Headers.Add("HX-Request", "true");
            using var response = await client.SendAsync(typed, Cancel);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var reason = await PostFormAsync(client, $"/patients/{patient}/reason?name={family}&birthdate={born}", chart, ("Reason", "Covering the night shift")))
        using (var assigned = await PostFormAsync(client, $"/patients/{patient}?name={family}&birthdate={born}&handler=Assign", reason))
        {
            Assert.Equal(chart, assigned.RequestMessage!.RequestUri!.PathAndQuery);
        }

        var offSite = Client(cloud, jar);
        await offSite.GetStringAsync(search, Cancel);
        using (var unassigned = await PostFormAsync(offSite, $"/patients/{patient}?name={family}&birthdate={born}&handler=Unassign", chart))
        {
            Assert.Equal(chart, unassigned.RequestMessage!.RequestUri!.PathAndQuery);
        }

        Assert.NotEmpty(logs.Messages);
        var leaked = logs.Messages.Where(message => message.Contains(family, StringComparison.Ordinal) || message.Contains(born, StringComparison.Ordinal)).ToList();
        Assert.True(leaked.Count == 0, "These log lines carry the search's criteria:\n" + string.Join("\n", leaked));
    }

    static bool Logs(WebApplicationFactory<Program> app, string category, LogLevel level) =>
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category).IsEnabled(level);

    // Every line the apps' logging configuration lets through, with its level and category.
    sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Messages);

        public void Dispose()
        {
        }

        sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
        }
    }
}
