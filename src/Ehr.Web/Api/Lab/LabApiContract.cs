using System.Text.Json;
using System.Text.Json.Serialization;

#if LAB_SIMULATOR
namespace Ehr.Lab.Simulator;
#else
namespace Ehr.Web.Api.Lab;
#endif

// The JSON of the lab's door (the expansion plan's step 12, UC 20): what the on-prem app hands a lab, and what a lab
// posts back (LabApi). This one file is also compiled into the simulated lab (src/Ehr.Lab.Simulator, which defines
// LAB_SIMULATOR to give it that project's namespace), so the two sides can't drift apart. Internal in both, so a
// project that sees both, as the web tests do, finds no clash. Nothing here but what crosses the wire: an order
// carries no patient's ID, name, MRN, birth date or sex.

// GET /api/lab/orders: where the next ask starts (a transaction ID, as text), whether more was waiting than one answer
// holds, and the orders.
internal sealed record LabOrders(string Watermark, bool More, IReadOnlyList<LabOrder> Orders);

// One lab order waiting for results: its ID, to file against; when it was signed, by the clinic's server's clock, in
// UTC; the tests still waiting for a result; and the codes of the patient's current diagnoses that aren't sensitive
// (ICD-10-CM), so values can fit the patient.
internal sealed record LabOrder(Guid Id, DateTimeOffset SignedAt, IReadOnlyList<LabTest> Tests, IReadOnlyList<string> Conditions);

// A test: its LOINC code, the unit its result is reported in, the clinic's reference range (either end may be
// missing) and how many decimals the clinic records it to.
internal sealed record LabTest(string Code, string? Unit, decimal? Low, decimal? High, int Decimals);

// POST /api/lab/orders/{id}/results: when the sample was collected, and a value for each test, in its unit.
internal sealed record LabResultsPost(DateTimeOffset CollectedAt, IReadOnlyList<LabResult> Results);

internal sealed record LabResult(string Code, decimal Value, string Unit);

// Every refusal: a fixed word for programs and a fixed sentence for people, never anything about a patient.
internal sealed record LabRefusal(string Error, string Message);

internal static class LabJson
{
    // camelCase, as the web's JSON is, and strict: a field neither side knows is refused, not dropped; a required one
    // can't be left out or null; a field can't come twice; and a number is a number, not a string. Every time is read
    // with any offset, as the instant it names, and written in UTC with a Z.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new UtcTimes() },
    };

    sealed class UtcTimes : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime);
    }
}
