using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;

namespace Ehr.Web.Api.Dictation;

// The on-prem app's route to the dictation service (src/Ehr.Dictation/sidecar), from Ehr:Dictation:Url, with the key
// it takes in Ehr:Dictation:ApiKey; both come from the environment, never the repo. Without them the rest of the app
// runs and dictation answers 503. Every call carries the key and the signed-in clinician's ID, which the service binds
// each session to, and nothing of the browser's request: no cookie, no header.
public sealed class DictationSidecar(string? url, string? apiKey, HttpMessageInvoker client, TimeSpan timeout)
{
    public const string OwnerHeader = "X-Dictation-Owner";

    // The cloud app's forwarder waits 30 seconds for an answer, and then marks the on-prem app unreachable for
    // everyone, so no dictation call may take longer than this.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public string? Url { get; } = url?.TrimEnd('/');

    public string? ApiKey { get; } = apiKey;

    public HttpMessageInvoker Client { get; } = client;

    // How long a call may take, answer included.
    public TimeSpan Timeout { get; } = timeout;

    [MemberNotNullWhen(true, nameof(Url), nameof(ApiKey))]
    public bool IsConfigured => Url is { Length: > 0 } && ApiKey is { Length: > 0 };

    public static DictationSidecar FromConfiguration(IConfiguration configuration)
    {
        // A short connect timeout, so a service that's down fails a call in seconds. No redirects and no cookies: the
        // service is called with exactly what is set here.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        return new DictationSidecar(
            configuration["Ehr:Dictation:Url"], configuration["Ehr:Dictation:ApiKey"], new HttpMessageInvoker(handler), DefaultTimeout);
    }

    // A call for the owner's session; path is relative to the service's address, such as "v1/sessions".
    public HttpRequestMessage Request(HttpMethod method, string path, Guid owner, HttpContent? content)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Set Ehr:Dictation:Url and Ehr:Dictation:ApiKey.");
        }

        var request = new HttpRequestMessage(method, $"{Url}/{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Headers.Add(OwnerHeader, owner.ToString());
        return request;
    }
}
