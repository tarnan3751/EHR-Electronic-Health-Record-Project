using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC8;

// Test Case ID: TC-UC8-11
// Purpose / scenario: The desktop app's updates (the expansion plan's step 20, Tahseen's 15a). Each site hands out the
//   release in the folder Ehr:Desktop:UpdatesFolder names, with nobody signed in, so a signed-out or wiped copy can
//   update too: only the entry for the platform asked about, its file's address on the site that was asked, and the
//   file itself; nothing for a platform with none, nothing with no folder, and no file outside the folder or the
//   release's own list, nor a link in the folder to a file outside it; a latest.json written wrong gives nothing, not an
//   error, nor one with a key twice (step 20's final review and its re-check).
// Type: Functional (successful flow)
// Requirements: proposed FR-70
// Use case: UC 8, Sync the desktop app
// Preconditions: a folder holding latest.json, for darwin-aarch64 and windows-x86_64, and the macOS file; the on-site
//   app and the cloud app each also run with Ehr:Desktop:UpdatesFolder naming it.
// Inputs and system conditions: GET /api/desktop/updates/{platform}/0.2.0 and /api/desktop/updates/files/{name}, with
//   no cookie and no token, on both sites, and on the on-site app with no folder.
// Test data: a release 0.2.1 with synthetic notes and signatures; a three-byte file.
// Steps:
//   1. Ask each site for darwin-aarch64's update, then fetch the file it names.
//   2. Ask for linux-x86_64's; fetch latest.json, a file not there, a name reaching out of the folder, and a link in the
//      folder to a file outside it.
//   3. Ask the app with no folder; then write latest.json wrong three ways and ask again each time.
// Expected result: Step 1: 200, version 0.2.1, the notes, darwin-aarch64's signature alone, and its file at
//   https://localhost/api/desktop/updates/files/SwiftChart.app.tar.gz, whose bytes come back. Step 2: 204, then 404
//   each time. Step 3: 204, and 204 each time.
// Creator: Tahseen Arnan
public sealed class TC_UC8_11_DesktopUpdates(WebApps apps) : IClassFixture<WebApps>, IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ehr-desktop-updates-").FullName;

    readonly string outside = Directory.CreateTempSubdirectory("ehr-desktop-outside-").FullName;

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
        Directory.Delete(outside, recursive: true);
    }

    [Fact]
    public async Task Each_site_hands_out_the_release_in_its_folder_from_its_own_address()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "latest.json"), """
            {
              "version": "0.2.1",
              "notes": "Synthetic release notes.",
              "pub_date": "2026-11-25T12:00:00Z",
              "platforms": {
                "darwin-aarch64": { "signature": "c3ludGhldGljIG1hY09T", "url": "SwiftChart.app.tar.gz" },
                "windows-x86_64": { "signature": "c3ludGhldGljIFdpbmRvd3M=", "url": "SwiftChart_0.2.1_x64-setup.exe" }
              }
            }
            """, Cancel);
        await File.WriteAllBytesAsync(Path.Combine(folder, "SwiftChart.app.tar.gz"), [1, 2, 3], Cancel);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "Synthetic: not for the update route.", Cancel);
        File.CreateSymbolicLink(Path.Combine(folder, "linked.tar.gz"), Path.Combine(outside, "secret.txt"));
        await using var onPrem = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("Ehr:Desktop:UpdatesFolder", folder));
        await using var cloud = apps.Cloud.WithWebHostBuilder(builder => builder.UseSetting("Ehr:Desktop:UpdatesFolder", folder));

        foreach (var site in new[] { onPrem, cloud })
        {
            using var client = CreateClient(site, handleCookies: false);

            // 1.
            using var update = await client.GetAsync("/api/desktop/updates/darwin-aarch64/0.2.0", Cancel);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var release = JsonNode.Parse(await update.Content.ReadAsStringAsync(Cancel))!;
            Assert.Equal("0.2.1", release["version"]!.GetValue<string>());
            Assert.Equal("Synthetic release notes.", release["notes"]!.GetValue<string>());
            var platforms = release["platforms"]!.AsObject();
            Assert.Equal(["darwin-aarch64"], platforms.Select(platform => platform.Key));
            Assert.Equal("c3ludGhldGljIG1hY09T", platforms["darwin-aarch64"]!["signature"]!.GetValue<string>());
            var url = platforms["darwin-aarch64"]!["url"]!.GetValue<string>();
            Assert.Equal("https://localhost/api/desktop/updates/files/SwiftChart.app.tar.gz", url);
            Assert.Equal([1, 2, 3], await client.GetByteArrayAsync(url, Cancel));

            // 2.
            using var none = await client.GetAsync("/api/desktop/updates/linux-x86_64/0.2.0", Cancel);
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
            foreach (var name in new[] { "latest.json", "SwiftChart_0.2.1_x64-setup.exe", "..%2Flatest.json", "%2E%2E%2Flatest.json", "linked.tar.gz" })
            {
                using var refused = await client.GetAsync($"/api/desktop/updates/files/{name}", Cancel);
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            }
        }

        // 3.
        using (var nothing = await CreateClient(apps.OnPrem, handleCookies: false).GetAsync("/api/desktop/updates/darwin-aarch64/0.2.0", Cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode);
        }

        foreach (var wrong in new[]
        {
            "{ \"version\": \"0.2.1\", \"platforms\": ", """{ "platforms": { "darwin-aarch64": { "url": 7 } } }""",
            """{ "version": "0.2.1", "version": "0.2.2", "platforms": {} }""",
        })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "latest.json"), wrong, Cancel);
            using var unreadable = await CreateClient(onPrem, handleCookies: false).GetAsync("/api/desktop/updates/darwin-aarch64/0.2.0", Cancel);
            Assert.Equal(HttpStatusCode.NoContent, unreadable.StatusCode);
        }
    }
}
