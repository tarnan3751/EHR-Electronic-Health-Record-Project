using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ehr.Web.Tests;

// The stub's sessions that listen to a visit (the expansion plan's step 16), answered as the service answers them: started
// with ?mode=ambient&speakers=N and called with DictationSidecar.ModeHeader every time (a call of the other kind finds no
// session); audio taken in order with no transcript until stop, and how long nobody has spoken (QuietSeconds); at stop, a
// made-up conversation of two or three voices (or one more, ExtraVoice), awaiting the clinician's word on who each is; a
// mapping that leaves a voice out, doesn't name one clinician and one patient, or names more people, all but "unsure",
// than the consent did, refused with 422 speakers_incomplete; and then the note's
// fields drafted from it by who said what: Subjective from the patient's words, with the warning that they are the
// patient's own, and Objective, Assessment and Plan from the clinician's, each source with its speaker. Once the voices
// are named, every segment says who said it, whether or not the fields could be sorted: the app stages nothing else
// (DictationApi.TrustedResult), and the service must send the same.
public sealed partial class DictationSidecarStub
{
    // How long nobody has spoken, as the service measures it, in every audio answer.
    public double QuietSeconds { get; set; }

    // One voice more than the consent said would speak, as telling voices apart can hand over a fragment of someone's: the
    // clinician can only name it "unsure" ("I can't tell") under a consent for two. Read as a session starts.
    public bool ExtraVoice { get; set; }

    // The conversation, a turn at a time: who spoke (the voice the service told apart) and what they said.
    static readonly (int Group, string Text)[] Turns =
    [
        (1, "What brings you in today?"),
        (2, "I have had a cough for three days."),
        (3, "She has been coughing at night."),
        (1, "Your chest is clear."),
        (1, "Likely a viral cough."),
        (1, "Fluids and rest."),
        (4, "Mm-hm."),
    ];

    HttpResponseMessage StartAmbient(string owner, string query)
    {
        var speakers = query.Contains("speakers=3", StringComparison.Ordinal) ? 3 : 2;
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        sessions[id] = new StubSession(owner) { Ambient = true, Speakers = speakers, Voices = speakers + (ExtraVoice ? 1 : 0) };
        return Json(HttpStatusCode.Created, new { sessionId = id, mode = "ambient", maxSeconds = 1200, maxChunkBytes = 1024 * 1024 });
    }

    HttpResponseMessage AmbientAnswer(string sessionId, StubSession session, string[] path, string method, string query, byte[] body)
    {
        switch (path, method)
        {
            case ([_, _, _, "audio"], "POST"):
                var seq = int.Parse(query.Split('=')[1], CultureInfo.InvariantCulture);
                if (session.Stopped)
                {
                    return Error(HttpStatusCode.Conflict, "stopped");
                }

                if (seq != session.Chunks)
                {
                    return Error(HttpStatusCode.Conflict, "out_of_order", new { expectedSeq = session.Chunks });
                }

                session.Chunks++;
                return Json(HttpStatusCode.OK, new
                {
                    receivedSeq = seq,
                    audioSeconds = session.Chunks * 5.0,
                    quietSeconds = QuietSeconds,
                    transcript = new { text = "", final = false, segments = Array.Empty<object>() },
                });

            case ([_, _, _, "stop"], "POST"):
                session.Stopped = true;
                return Json(HttpStatusCode.OK, Conversation(session, Sorting == StubSorting.Done ? StubSorting.Pending : Sorting));

            case ([_, _, _, "speakers"], "POST"):
                if (!session.Stopped || session.Roles is not null)
                {
                    return Error(HttpStatusCode.Conflict, session.Stopped ? "speakers_given" : "not_stopped");
                }

                var given = JsonDocument.Parse(body).RootElement.GetProperty("speakers").EnumerateObject()
                    .ToDictionary(voice => int.Parse(voice.Name, CultureInfo.InvariantCulture), voice => voice.Value.GetString()!);
                var roles = given.Values.ToList();
                if (!given.Keys.Order().SequenceEqual(Enumerable.Range(1, session.Voices))
                    || roles.Count(role => role == "clinician") != 1 || roles.Count(role => role == "patient") != 1
                    || roles.Count(role => role != "unsure") > session.Speakers)
                {
                    return Error(HttpStatusCode.UnprocessableEntity, "speakers_incomplete");
                }

                session.Roles = given;
                return Json(HttpStatusCode.OK, Conversation(session, Sorting == StubSorting.Done ? StubSorting.Pending : Sorting));

            case ([_, _, _, "result"], "POST"):
                return session.Stopped ? Json(HttpStatusCode.OK, Conversation(session, Sorting)) : Error(HttpStatusCode.Conflict, "not_stopped");

            case ([_, _, _], "DELETE"):
                sessions.TryRemove(sessionId, out _);
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            default:
                return Error(HttpStatusCode.NotFound, "not_found");
        }
    }

    // The conversation's turns for the voices the session expected, each a segment of the transcript with its voice, and
    // its role once the clinician has said who each voice is.
    static (string Text, List<(int Start, int End, int Group, string? Role)> Segments) Spoken(StubSession session)
    {
        var text = new StringBuilder();
        var segments = new List<(int, int, int, string?)>();
        foreach (var (group, words) in Turns.Where(turn => turn.Group <= session.Voices))
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            var start = text.Length;
            text.Append(words);
            segments.Add((start, text.Length, group, session.Roles?.GetValueOrDefault(group)));
        }

        return (text.ToString(), segments);
    }

    object Conversation(StubSession session, StubSorting sorting)
    {
        var (text, spoken) = Spoken(session);
        var transcript = new
        {
            text,
            final = true,
            segments = spoken.Select((segment, index) => new
            {
                start = segment.Start,
                end = segment.End,
                startMs = index * 4000,
                endMs = index * 4000 + 3500,
                final = true,
                unclear = (string?)null,
                speakerGroup = segment.Group,
                speaker = segment.Role,
            }),
        };
        var provenance = new
        {
            speech = new { repository = "google/medasr", commit = "1111111", sha256 = new string('a', 64) },
            language = new { repository = "IFM/K2-Horizon-7B-GGUF", commit = "2222222", file = "K2-Horizon-7B-Q4_K_M.gguf", sha256 = new string('b', 64) },
            llamaServer = "b11669",
            prompt = "conversation-prompt-1",
            schema = "note-schema-1",
            structuring = "conversation-structuring-1",
            code = "sha256:" + new string('c', 64),
            speaker = new { repository = "speechbrain/spkrec-ecapa-voxceleb", commit = "3333333", sha256 = new string('d', 64) },
        };
        var speakers = Enumerable.Range(1, session.Voices).Select(group => new { group, seconds = 3.5 * spoken.Count(s => s.Group == group) });
        if (session.Roles is null)
        {
            return new { transcript, sorting = "awaiting_speakers", fields = (object?)null, notCarriedOver = Array.Empty<object>(), provenance, speakers };
        }

        if (sorting == StubSorting.Pending)
        {
            return new { transcript, sorting = "pending", fields = (object?)null, notCarriedOver = Array.Empty<object>(), provenance };
        }

        if (sorting is StubSorting.Timeout or StubSorting.Unavailable)
        {
            return new { transcript, sorting = "unavailable", reason = "model_timeout", fields = (object?)null, notCarriedOver = Array.Empty<object>(), provenance };
        }

        // Who said it decides where it may go: the first thing the patient said, then the clinician's last three.
        var patient = spoken.First(segment => segment.Role == "patient");
        var clinician = spoken.Where(segment => segment.Role == "clinician").TakeLast(3).ToList();
        object Field((int Start, int End, int Group, string? Role) segment, params string[] warnings) => new
        {
            status = "pending",
            value = text[segment.Start..segment.End],
            sources = new[] { new { start = segment.Start, end = segment.End, speaker = segment.Role } },
            warnings = warnings.Select(code => new { code, start = segment.Start, end = segment.End }),
        };
        object Empty() => new { status = "empty", reasons = new[] { "not_dictated" } };
        return new
        {
            transcript,
            sorting = "done",
            fields = (object?)new
            {
                subjective = Field(patient, "patients_own_words"),
                objective = clinician.Count > 0 ? Field(clinician[0]) : Empty(),
                assessment = clinician.Count > 1 ? Field(clinician[1]) : Empty(),
                plan = clinician.Count > 2 ? Field(clinician[2]) : Empty(),
            },
            notCarriedOver = Array.Empty<object>(),
            provenance,
        };
    }
}
