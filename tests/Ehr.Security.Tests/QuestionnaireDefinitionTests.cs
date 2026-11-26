using System.Text.Json;
using Ehr.Data.Reference;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 13e: the PHQ-9 and GAD-7 as the database holds them (db/reference/36-questionnaires.sql),
// held to a copy typed here from their authors' published forms and the sources of their scoring, so a slip in the
// reference data fails. docs/expansion/reference-data.md ("Pre-visit questionnaires") cites each source.
public class QuestionnaireDefinitionTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Attribution = "Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from "
        + "Pfizer Inc. No permission required to reproduce, translate, display or distribute.";

    // LOINC 2.83's notice for both (Loinc.csv, EXTERNAL_COPYRIGHT_NOTICE), which LOINC's license asks to be carried.
    const string LoincNotice = "Copyright © Pfizer Inc. All rights reserved. " + Attribution;

    // The column heads of both forms and their scores, LOINC answer list LL358-3.
    static readonly string[] Frequency = ["LA6568-5 Not at all 0", "LA6569-3 Several days 1", "LA6570-1 More than half the days 2", "LA6571-9 Nearly every day 3"];

    // The PHQ-9's difficulty boxes, LOINC answer list LL359-1, not scored.
    static readonly string[] Difficulty = ["LA6572-7 Not difficult at all", "LA6573-5 Somewhat difficult", "LA6575-0 Very difficult", "LA6574-3 Extremely difficult"];

    // PHQ-9_English.pdf, page 1, in LOINC panel 44249-1's order: code, text, answer list, and scored, optional, alert.
    static readonly string[] PhqQuestions =
    [
        "44250-9 | Little interest or pleasure in doing things | frequency | scored",
        "44255-8 | Feeling down, depressed, or hopeless | frequency | scored",
        "44259-0 | Trouble falling or staying asleep, or sleeping too much | frequency | scored",
        "44254-1 | Feeling tired or having little energy | frequency | scored",
        "44251-7 | Poor appetite or overeating | frequency | scored",
        "44258-2 | Feeling bad about yourself — or that you are a failure or have let yourself or your family down | frequency | scored",
        "44252-5 | Trouble concentrating on things, such as reading the newspaper or watching television | frequency | scored",
        "44253-3 | Moving or speaking so slowly that other people could have noticed? Or the opposite — being so fidgety or restless that you have been moving around a lot more than usual | frequency | scored",
        "44260-8 | Thoughts that you would be better off dead or of hurting yourself in some way | frequency | scored alert help=self-harm",
        "69722-7 | If you checked off any problems, how difficult have these problems made it for you to do your work, take care of things at home, or get along with other people? | difficulty | optional",
    ];

    // GAD-7_English.pdf, page 1, in LOINC panel 69737-5's order.
    static readonly string[] GadQuestions =
    [
        "69725-0 | Feeling nervous, anxious or on edge | frequency | scored",
        "68509-9 | Not being able to stop or control worrying | frequency | scored",
        "69733-4 | Worrying too much about different things | frequency | scored",
        "69734-2 | Trouble relaxing | frequency | scored",
        "69735-9 | Being so restless that it is hard to sit still | frequency | scored",
        "69689-8 | Becoming easily annoyed or irritable | frequency | scored",
        "69736-7 | Feeling afraid as if something awful might happen | frequency | scored",
    ];

    // Each questionnaire as one list of lines: what it is, then its questions, answers, total, bands and follow-up.
    static IEnumerable<string> Published(string code)
    {
        if (code == "44249-1")
        {
            yield return "title Patient Health Questionnaire-9 (PHQ-9)";
            yield return "prompt Over the last 2 weeks, how often have you been bothered by any of the following problems?";
            foreach (var question in PhqQuestions)
            {
                yield return "item " + question;
            }

            foreach (var answer in Frequency)
            {
                yield return "frequency " + answer;
            }

            foreach (var answer in Difficulty)
            {
                yield return "difficulty " + answer;
            }

            // Instruction manual, page 3, Table 1; Kroenke et al. 2001, Table 2; manual, page 2.
            yield return "total 44261-6 27";
            yield return "band 0 4 Minimal";
            yield return "band 5 9 Mild";
            yield return "band 10 14 Moderate";
            yield return "band 15 19 Moderately severe";
            yield return "band 20 27 Severe";
            yield return "followUpAt 10";
        }
        else
        {
            yield return "title GAD-7";
            yield return "prompt Over the last 2 weeks, how often have you been bothered by the following problems?";
            foreach (var question in GadQuestions)
            {
                yield return "item " + question;
            }

            foreach (var answer in Frequency)
            {
                yield return "frequency " + answer;
            }

            // Instruction manual, page 3, Table 1 and page 6; below 5 the clinic's own words (no source read names it).
            yield return "total 70274-6 21";
            yield return "band 0 4 Below mild";
            yield return "band 5 9 Mild";
            yield return "band 10 14 Moderate";
            yield return "band 15 21 Severe";
            yield return "followUpAt 10";
        }

        yield return "attribution " + Attribution;
        yield return "copyright " + LoincNotice;
    }

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_questionnaires_are_worded_and_scored_as_their_authors_published_them()
    {
        var held = await HeldAsync();

        Assert.Equal(["44249-1", "69737-5"], held.Keys.Order(StringComparer.Ordinal));
        foreach (var (code, lines) in held)
        {
            Assert.Equal(Published(code), lines);
        }
    }

    // Every total from 0 to the most falls in exactly one band, and the GAD-7 is never called "Minimal": no source read
    // names its band below 5.
    [Fact]
    public async Task Every_total_has_one_band_and_the_gad_7_has_no_band_called_minimal()
    {
        foreach (var (code, lines) in await HeldAsync())
        {
            var most = int.Parse(lines.Single(line => line.StartsWith("total ", StringComparison.Ordinal)).Split(' ')[2]);
            var bands = lines.Where(line => line.StartsWith("band ", StringComparison.Ordinal)).Select(line => line.Split(' ', 4))
                .Select(parts => (From: int.Parse(parts[1]), To: int.Parse(parts[2]), Band: parts[3])).ToList();
            for (var total = 0; total <= most; total++)
            {
                Assert.Single(bands, band => band.From <= total && total <= band.To);
            }

            Assert.DoesNotContain(bands, band => band.From < 0 || band.To > most);
            if (code == "69737-5")
            {
                Assert.DoesNotContain(bands, band => band.Band.Contains("inimal", StringComparison.Ordinal));
            }
        }
    }

    // An alert question names a notice the portal words (QuestionnaireHelp), or the patient would be told nothing about
    // getting help.
    [Fact]
    public async Task Every_alert_question_names_a_notice_the_portal_knows()
    {
        foreach (var (code, lines) in await HeldAsync())
        {
            foreach (var alert in lines.Where(line => line.StartsWith("item ", StringComparison.Ordinal) && line.Contains(" alert", StringComparison.Ordinal)))
            {
                var help = alert.Split(' ').FirstOrDefault(mark => mark.StartsWith("help=", StringComparison.Ordinal))?["help=".Length..];
                Assert.True(help is not null && QuestionnaireHelp.All.Contains(help), $"{code}: an alert question names no notice the portal knows: {alert}");
            }
        }
    }

    // The rows as the app's role reads them (reference data: grants only), each turned into the lines above.
    async Task<Dictionary<string, List<string>>> HeldAsync()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT code, title, definition::text, copyright FROM ehr.questionnaires", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Cancel))
        {
            using var definition = JsonDocument.Parse(reader.GetString(2));
            var root = definition.RootElement;
            var lines = new List<string> { "title " + reader.GetString(1), "prompt " + root.GetProperty("prompt").GetString() };
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                var marks = string.Join(' ', new[] { "scored", "optional", "alert" }.Where(mark => item.TryGetProperty(mark, out var value) && value.GetBoolean())
                    .Concat(item.TryGetProperty("help", out var help) ? [$"help={help.GetString()}"] : []));
                lines.Add($"item {item.GetProperty("code").GetString()} | {item.GetProperty("text").GetString()} | {item.GetProperty("answers").GetString()} | {marks}");
            }

            foreach (var list in root.GetProperty("answerLists").EnumerateObject())
            {
                lines.AddRange(list.Value.EnumerateArray().Select(answer =>
                    $"{list.Name} {answer.GetProperty("code").GetString()} {answer.GetProperty("text").GetString()}"
                    + (answer.TryGetProperty("score", out var score) ? $" {score.GetInt32()}" : "")));
            }

            var total = root.GetProperty("total");
            lines.Add($"total {total.GetProperty("code").GetString()} {total.GetProperty("max").GetInt32()}");
            lines.AddRange(root.GetProperty("bands").EnumerateArray()
                .Select(band => $"band {band.GetProperty("from").GetInt32()} {band.GetProperty("to").GetInt32()} {band.GetProperty("band").GetString()}"));
            lines.Add($"followUpAt {root.GetProperty("followUpAt").GetInt32()}");
            lines.Add("attribution " + root.GetProperty("attribution").GetString());
            lines.Add("copyright " + reader.GetString(3));
            held[reader.GetString(0)] = lines;
        }

        return held;
    }
}
