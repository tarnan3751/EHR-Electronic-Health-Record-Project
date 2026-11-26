using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Api.Desktop;

// The desktop app's updates (the expansion plan's step 20, Tahseen's 15a; proposed FR-70): each site serves them, and
// can refuse to sync with a build older than a minimum. The app checks the servers it was built for, in order, and
// installs only what is signed with the key built into it, for the version announced (tauri-plugin-updater's
// requireSignedVersion): this side signs nothing and is trusted with nothing but saying what there is.
//
// Updates, GET /api/desktop/updates/{platform}/{version}, anonymous like the sign-in, so a copy signed out or wiped
// can update too: from latest.json in the folder Ehr:Desktop:UpdatesFolder names, in Tauri's static form, each
// platform's url a file name in that folder ("darwin-aarch64": { "signature": "…", "url":
// "SwiftChart.app.tar.gz" }), answered with that platform's entry alone and its url on the site that was asked, so
// the clinic's server and the cloud app each hand out their own copy; 204 with no folder, no release, or nothing for
// the platform. The app decides whether the release is newer than itself, and never takes an older one. The files,
// GET /api/desktop/updates/files/{name}: any file directly in the folder, by its name alone.
//
// The minimum version, Ehr:Desktop:MinimumVersion (none when empty): a request to sync or to the desktop's data
// (DesktopVersionFilter), from a build that says it is older in Ehr-Desktop-Version, or says nothing, as builds from
// before step 20 don't, is refused with 426, the minimum in Ehr-Desktop-Minimum, and the reason in words. Sign-in,
// the lock's check of the password, sign-out and the updates themselves never are, so an old build's lock opens, its
// offline charts stay readable, and it can fetch the update that lets it sync again. 426, not 401, which every build
// takes as its token refused, nor 403, which an older build takes as a recording refused for good.
public sealed class DesktopUpdateOptions
{
    public const string Section = "Ehr:Desktop";

    public string? MinimumVersion { get; set; }

    public string? UpdatesFolder { get; set; }

    // The minimum as a version with three parts, or null when there is none.
    public Version? Minimum => DesktopUpdates.ParseVersion(MinimumVersion);
}

public sealed class DesktopVersionFilter(IOptionsMonitor<DesktopUpdateOptions> options) : IEndpointFilter
{
    public const string VersionHeader = "Ehr-Desktop-Version";

    public const string MinimumHeader = "Ehr-Desktop-Minimum";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (options.CurrentValue.Minimum is { } minimum
            && !(DesktopUpdates.ParseVersion(context.HttpContext.Request.Headers[VersionHeader]) >= minimum))
        {
            context.HttpContext.Response.Headers[MinimumHeader] = minimum.ToString(3);
            return Results.Json(
                new Refusal($"Update needed: the clinic's server syncs only with SwiftChart {minimum.ToString(3)} or later. What you save waits on this computer, and the offline charts can still be read."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }

        return await next(context);
    }
}

public static class DesktopUpdates
{
    const string Manifest = "latest.json";

    public static void MapDesktopUpdates(this RouteGroupBuilder desktop)
    {
        desktop.MapGet("/updates/files/{name}", UpdateFile).AllowAnonymous();
        desktop.MapGet("/updates/{platform}/{version}", Release).AllowAnonymous();
    }

    // "0.2" and "0.2.0" are the same version; anything else that isn't one is none.
    public static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Trim(), out var version) ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0)) : null;

    static async Task<IResult> Release(string platform, HttpRequest request, IOptionsMonitor<DesktopUpdateOptions> options, CancellationToken cancellationToken)
    {
        if (options.CurrentValue.UpdatesFolder is not { Length: > 0 } folder || !System.IO.File.Exists(Path.Combine(folder, Manifest)))
        {
            return Results.NoContent();
        }

        var release = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(folder, Manifest), cancellationToken))!.AsObject();
        if (release["platforms"]?[platform] is not JsonObject entry || entry["url"]?.GetValue<string>() is not { } name || !IsFileName(name))
        {
            return Results.NoContent();
        }

        var answer = new JsonObject
        {
            ["version"] = release["version"]?.DeepClone(),
            ["notes"] = release["notes"]?.DeepClone(),
            ["pub_date"] = release["pub_date"]?.DeepClone(),
            ["platforms"] = new JsonObject
            {
                [platform] = new JsonObject
                {
                    ["signature"] = entry["signature"]?.DeepClone(),
                    ["url"] = $"{request.Scheme}://{request.Host}{request.PathBase}/api/desktop/updates/files/{Uri.EscapeDataString(name)}",
                },
            },
        };
        return Results.Text(answer.ToJsonString(), "application/json");
    }

    static IResult UpdateFile(string name, IOptionsMonitor<DesktopUpdateOptions> options) =>
        options.CurrentValue.UpdatesFolder is { Length: > 0 } folder && IsFileName(name) && name != Manifest
            && System.IO.File.Exists(Path.Combine(folder, name))
            ? Results.File(Path.Combine(Path.GetFullPath(folder), name), "application/octet-stream", enableRangeProcessing: true)
            : Results.NotFound();

    // A plain name of a file in the folder: no folder in it, nothing hidden.
    static bool IsFileName(string name) =>
        name.Length > 0 && name == Path.GetFileName(name) && !name.StartsWith('.') && name.IndexOfAny(['/', '\\']) < 0;
}
