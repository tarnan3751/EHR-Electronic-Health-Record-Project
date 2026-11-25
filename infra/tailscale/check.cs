// Checks the Tailscale access rules (policy.hujson, beside this file) for syntax and consistency, with no Tailscale
// account and no network. CI runs it on every push:
//
//   dotnet run infra/tailscale/check.cs           exits with 1, naming each problem, when one is found
//
// What it checks: the file parses as HuJSON (JSON with comments and trailing commas); it has exactly the sections this
// project uses (groups, tagOwners, grants, tests), each in the shape below; every group, tag and user it names is
// defined; every port is a single TCP or UDP port; nothing is a wildcard; and every opening a grant makes (a source, a
// tagged destination and a port) is accepted by some test from that source.
// What it doesn't: that Tailscale accepts the file (its own schema may differ from this one), that the tests pass under
// Tailscale's own evaluation, that a deny in the tests is really denied, or that devices are tagged as the rules assume.
// The rules are untested against a network until the owner applies them to a tailnet (policy.hujson's header).

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

var path = Path.Combine(Paths.Here(), "policy.hujson");
var problems = new List<string>();
JsonDocument document;
try
{
    document = JsonDocument.Parse(File.ReadAllText(path),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
}
catch (JsonException e)
{
    Console.Error.WriteLine($"{path}: not HuJSON: {e.Message}");
    return 1;
}

var root = document.RootElement;
string[] sections = ["groups", "tagOwners", "grants", "tests"];
foreach (var property in root.EnumerateObject().Where(p => !sections.Contains(p.Name)))
{
    problems.Add($"\"{property.Name}\" is a section this project doesn't use");
}

foreach (var section in sections.Where(s => !root.TryGetProperty(s, out _)))
{
    problems.Add($"\"{section}\" is missing");
}

if (problems.Count == 0)
{
    var groups = new Dictionary<string, List<string>>();
    foreach (var group in root.GetProperty("groups").EnumerateObject())
    {
        Require(Patterns.Group().IsMatch(group.Name), $"group \"{group.Name}\" isn't named group:<lowercase>");
        var members = Strings(group.Value, $"group {group.Name}");
        Require(members.Count > 0, $"group {group.Name} has no members");
        members.ForEach(m => Require(Patterns.Email().IsMatch(m), $"group {group.Name}: \"{m}\" isn't a user's address"));
        groups[group.Name] = members;
    }

    var tags = new HashSet<string>();
    foreach (var tag in root.GetProperty("tagOwners").EnumerateObject())
    {
        Require(Patterns.Tag().IsMatch(tag.Name), $"tag \"{tag.Name}\" isn't named tag:<lowercase>");
        var owners = Strings(tag.Value, $"tag {tag.Name}");
        Require(owners.Count > 0, $"tag {tag.Name} has no owner");
        owners.ForEach(o => Require(groups.ContainsKey(o), $"tag {tag.Name}: owner \"{o}\" isn't a defined group"));
        tags.Add(tag.Name);
    }

    // Each opening a grant makes: who from, which tagged destination, which protocol and port.
    var openings = new List<(string Source, string Destination, string Port)>();
    var number = 0;
    foreach (var grant in Objects(root.GetProperty("grants"), "grants"))
    {
        number++;
        var where = $"grant {number}";
        Keys(grant, where, required: ["src", "dst", "ip"], optional: []);
        var sources = Strings(grant.GetProperty("src"), $"{where} src");
        var destinations = Strings(grant.GetProperty("dst"), $"{where} dst");
        var ports = Strings(grant.GetProperty("ip"), $"{where} ip");
        Require(sources.Count > 0 && destinations.Count > 0 && ports.Count > 0, $"{where} has an empty src, dst or ip");
        sources.ForEach(s => Require(groups.ContainsKey(s) || tags.Contains(s), $"{where}: source \"{s}\" isn't a defined group or tag"));
        destinations.ForEach(d => Require(tags.Contains(d), $"{where}: destination \"{d}\" isn't a defined tag"));
        ports.ForEach(p => Require(Patterns.Port().Match(p) is { Success: true } m && Port(m.Groups[2].Value),
            $"{where}: \"{p}\" isn't tcp:<port> or udp:<port>"));
        openings.AddRange(from s in sources from d in destinations from p in ports select (s, d, p));
    }

    var accepted = new List<(string Source, string Destination, string Port)>();
    number = 0;
    foreach (var test in Objects(root.GetProperty("tests"), "tests"))
    {
        number++;
        var where = $"test {number}";
        Keys(test, where, required: ["src", "proto"], optional: ["accept", "deny"]);
        Require(test.TryGetProperty("accept", out _) || test.TryGetProperty("deny", out _), $"{where} has neither accept nor deny");
        var source = test.GetProperty("src").GetString() ?? "";
        Require(groups.ContainsKey(source) || tags.Contains(source) || groups.Values.Any(m => m.Contains(source)),
            $"{where}: src \"{source}\" isn't a defined group, tag or group member");
        var proto = test.GetProperty("proto").GetString() ?? "";
        Require(proto is "tcp" or "udp", $"{where}: proto \"{proto}\" isn't tcp or udp");
        foreach (var kind in new[] { "accept", "deny" })
        {
            if (!test.TryGetProperty(kind, out var list))
            {
                continue;
            }

            foreach (var entry in Strings(list, $"{where} {kind}"))
            {
                var match = Patterns.Destination().Match(entry);
                Require(match.Success && tags.Contains(match.Groups[1].Value) && Port(match.Groups[2].Value),
                    $"{where} {kind}: \"{entry}\" isn't <defined tag>:<port>");
                if (match.Success && kind == "accept")
                {
                    accepted.Add((source, match.Groups[1].Value, $"{proto}:{match.Groups[2].Value}"));
                }
            }
        }
    }

    // A test from a user counts for the groups it is in.
    foreach (var opening in openings)
    {
        Require(accepted.Any(a => a.Destination == opening.Destination && a.Port == opening.Port
                && (a.Source == opening.Source || (groups.TryGetValue(opening.Source, out var members) && members.Contains(a.Source)))),
            $"no test accepts {opening.Source} to {opening.Destination} on {opening.Port}");
    }

    Console.WriteLine($"{groups.Count} group(s), {tags.Count} tag(s), {openings.Count} opening(s) from the grants, {number} test(s).");
}

foreach (var problem in problems)
{
    Console.Error.WriteLine($"{path}: {problem}");
}

Console.WriteLine(problems.Count == 0
    ? "The Tailscale policy file's syntax and references check out. That is all this proves: it hasn't been applied to a tailnet."
    : $"{problems.Count} problem(s) in the Tailscale policy file.");
return problems.Count == 0 ? 0 : 1;

void Require(bool condition, string problem)
{
    if (!condition)
    {
        problems.Add(problem);
    }
}

bool Port(string text) => int.TryParse(text, out var port) && port is >= 1 and <= 65535 && text == port.ToString();

List<string> Strings(JsonElement element, string where)
{
    if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
    {
        problems.Add($"{where} isn't a list of strings");
        return [];
    }

    var values = element.EnumerateArray().Select(e => e.GetString()!).ToList();
    values.Where(v => v.Contains('*')).ToList().ForEach(v => problems.Add($"{where}: \"{v}\" is a wildcard"));
    return values;
}

List<JsonElement> Objects(JsonElement element, string where)
{
    if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object))
    {
        problems.Add($"{where} isn't a list of objects");
        return [];
    }

    return [.. element.EnumerateArray()];
}

void Keys(JsonElement element, string where, string[] required, string[] optional)
{
    var names = element.EnumerateObject().Select(p => p.Name).ToList();
    foreach (var name in names.Where(n => !required.Contains(n) && !optional.Contains(n)))
    {
        problems.Add($"{where} has \"{name}\", which this project doesn't use");
    }

    foreach (var name in required.Where(r => !names.Contains(r)))
    {
        problems.Add($"{where} has no \"{name}\"");
    }
}

static class Paths
{
    public static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

static partial class Patterns
{
    [GeneratedRegex("^group:[a-z0-9-]+$")]
    public static partial Regex Group();

    [GeneratedRegex("^tag:[a-z0-9-]+$")]
    public static partial Regex Tag();

    [GeneratedRegex(@"^[^@\s*]+@[^@\s*]+\.[a-z]+$")]
    public static partial Regex Email();

    [GeneratedRegex(@"^(tcp|udp):([0-9]+)$")]
    public static partial Regex Port();

    [GeneratedRegex(@"^(tag:[a-z0-9-]+):([0-9]+)$")]
    public static partial Regex Destination();
}
