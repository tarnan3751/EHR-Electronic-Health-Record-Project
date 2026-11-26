using System.Text.Json;

namespace Ehr.Domain.Forms;

// A questionnaire's definition as the pages read it (the expansion plan's step 13e): the shape
// db/reference/36-questionnaires.sql describes, from the copy a request keeps of it, so a form is always shown with the
// wording it was asked and answered with. The database scores answers against the same copy
// (ehr.questionnaire_score); nothing here works out a score.
public sealed record QuestionnaireDefinition(
    string Prompt,
    IReadOnlyList<QuestionnaireItem> Items,
    IReadOnlyDictionary<string, IReadOnlyList<QuestionnaireAnswer>> AnswerLists,
    int? Max,
    IReadOnlyList<QuestionnaireBand> Bands,
    int? FollowUpAt,
    string? Attribution)
{
    public IReadOnlyList<QuestionnaireAnswer> AnswersTo(QuestionnaireItem item) => AnswerLists.GetValueOrDefault(item.Answers) ?? [];

    // The questions whose answers need review, each with its number on the form, from 1: an alert question answered with
    // an answer that scores more than 0, the database's rule (ehr.questionnaire_score). Answers maps each question's code
    // to the code of the answer chosen.
    public IReadOnlyList<(int Number, QuestionnaireItem Item)> NeedingReview(IReadOnlyDictionary<string, string> answers) =>
        [.. Items.Select((item, place) => (Number: place + 1, Item: item))
            .Where(question => question.Item.Alert && answers.TryGetValue(question.Item.Code, out var code)
                && AnswersTo(question.Item).Any(answer => answer.Code == code && answer.Score > 0))];

    public static QuestionnaireDefinition Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static int? Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
        static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        static IEnumerable<JsonElement> Each(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

        var lists = new Dictionary<string, IReadOnlyList<QuestionnaireAnswer>>(StringComparer.Ordinal);
        if (root.TryGetProperty("answerLists", out var answerLists) && answerLists.ValueKind == JsonValueKind.Object)
        {
            foreach (var list in answerLists.EnumerateObject())
            {
                lists[list.Name] = [.. list.Value.EnumerateArray().Select(answer => new QuestionnaireAnswer(Text(answer, "code") ?? "", Text(answer, "text") ?? "", Number(answer, "score")))];
            }
        }

        return new QuestionnaireDefinition(
            Text(root, "prompt") ?? "",
            [.. Each(root, "items").Select(item => new QuestionnaireItem(
                Text(item, "code") ?? "", Text(item, "text") ?? "", Text(item, "answers") ?? "", Flag(item, "scored"), Flag(item, "optional"), Flag(item, "alert"),
                Text(item, "help")))],
            lists,
            root.TryGetProperty("total", out var total) ? Number(total, "max") : null,
            [.. Each(root, "bands").Select(band => new QuestionnaireBand(Number(band, "from") ?? 0, Number(band, "to") ?? 0, Text(band, "band") ?? ""))],
            Number(root, "followUpAt"),
            Text(root, "attribution"));
    }
}

// Help, on an alert question: the notice the portal shows with an answer to it that needs review (QuestionnaireHelp).
public sealed record QuestionnaireItem(string Code, string Text, string Answers, bool Scored, bool Optional, bool Alert, string? Help);

public sealed record QuestionnaireAnswer(string Code, string Text, int? Score);

public sealed record QuestionnaireBand(int From, int To, string Band);
