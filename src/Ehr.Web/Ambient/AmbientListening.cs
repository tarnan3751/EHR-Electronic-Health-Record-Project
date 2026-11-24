namespace Ehr.Web.Ambient;

// Whether the clinic listens to visits (the expansion plan's step 16, UC 23): Ehr:Ambient:Enabled, off unless set to
// true. Off by default: the drafts a visit gives are thin, and recording a patient's visit for so little is a cost
// without the benefit; switching it on is the clinic owner's decision, after reading what they are worth (docs/UC23.md).
// Off, nothing offers listening, and the Listen page and every route under the dictation API's /ambient answer 404 before
// anything else, so no consent can be written. The reviews of visits recorded while it was on still open and can be
// finished, and their transcripts are still deleted on time (AmbientTranscriptPurgeService). Both apps read it; anything
// but true or false refuses to start.
public sealed record AmbientListening(bool Enabled)
{
    public const string Setting = "Ehr:Ambient:Enabled";

    public static AmbientListening From(IConfiguration configuration) =>
        configuration[Setting] switch
        {
            null => new(false),
            var value when bool.TryParse(value, out var enabled) => new(enabled),
            _ => throw new InvalidOperationException($"Set {Setting} to true or false, or leave it out for false."),
        };
}
