using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ehr.Web.Tests;

// Exposes the real in-memory app (the on-site one, or the off-site one a test names) to Chromium over loopback HTTPS. No
// alternate endpoints or production auth bypass.
// The normal test fixture's synthetic accounts and owner-checked deterministic model stub are used unchanged. A test of
// passkeys (step 19) gives the app it makes for the host's own address, once that is known: a passkey is tied to the
// address the browser sees.
sealed class ClinicalBrowserHost : IAsyncDisposable
{
    readonly WebApplication host;
    HttpClient client = null!;
    readonly X509Certificate2 certificate;
    readonly RSA key;
    public string Url { get; private set; } = "";

    ClinicalBrowserHost()
    {
        key = RSA.Create(2048);
        using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Loaded again from its PFX: on Windows, SChannel can't serve TLS with the ephemeral key CreateSelfSigned gives,
        // and Chromium then sees the connection closed. Macs and Linux accept either.
        certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        host = builder.Build();
        host.Run(ForwardAsync);
    }

    public static Task<ClinicalBrowserHost> StartAsync(WebApps apps, WebApplicationFactory<Program>? app = null) =>
        StartAsync(_ => app ?? apps.OnPrem);

    // The app to serve, given the address the browser will reach it at.
    public static async Task<ClinicalBrowserHost> StartAsync(Func<string, WebApplicationFactory<Program>> appAt)
    {
        var server = new ClinicalBrowserHost();
        await server.host.StartAsync();
        var address = server.host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.Url = address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        server.client = appAt(server.Url).CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = Browser.Base, AllowAutoRedirect = false, HandleCookies = false });
        return server;
    }

    async Task ForwardAsync(HttpContext context)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
        if (context.Request.ContentLength > 0)
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            request.Content = new ByteArrayContent(bytes.ToArray());
        }
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        using var response = await client.SendAsync(request);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers)) context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("transfer-encoding");
        // "Not modified" and "no content" have no body, and Kestrel refuses a write to one: the request would fail, and
        // an image the browser was only checking again (the app bar's logo, on the second page it's on) would break.
        if (response.StatusCode is not (System.Net.HttpStatusCode.NotModified or System.Net.HttpStatusCode.NoContent))
        {
            await response.Content.CopyToAsync(context.Response.Body);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        await host.DisposeAsync();
        client.Dispose();
        certificate.Dispose();
        key.Dispose();
    }
}
