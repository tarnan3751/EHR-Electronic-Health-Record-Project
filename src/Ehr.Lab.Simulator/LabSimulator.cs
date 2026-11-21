using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ehr.Lab.Simulator;

// What the simulated lab does each time it wakes (the expansion plan's step 12): asks the clinic for the lab orders
// signed since it last asked (again at once while more are waiting), keeps each until its results are due
// (SimulatedResults), and posts them when they are. It keeps only that in memory. Started again, it asks from the
// beginning and is handed what is still waiting; and posting results already filed is answered as the first post was,
// so two labs at once, or one started again, file each result once. It talks HTTP only, as a lab's system would,
// through the client it is given, which carries the clinic's address and the lab's key.
//
// What the clinic answers decides what happens to an order: filed, it is done; refused for good (unknown, discontinued,
// acknowledged, a test it doesn't report, a wrong unit, a different result already on file), it is dropped; anything
// else (a collection time the clinic's clock hasn't reached, a refused key, too many requests, the clinic out of
// reach), it waits for the next round. The log names orders by ID only: a lab is told nothing else about them.
public sealed class LabSimulator(HttpClient client, LabSettings settings, Action<string> log, string since = "0")
{
    // The most times one round asks while the clinic says more is waiting.
    const int MaxPages = 20;

    string watermark = since;

    readonly Dictionary<Guid, LabPlan> pending = [];

    public async Task<LabRound> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var handed = await AskAsync(cancellationToken);
        var (filed, dropped) = await FileDueAsync(now, cancellationToken);
        return new LabRound(handed, filed, dropped, pending.Count);
    }

    async Task<int> AskAsync(CancellationToken cancellationToken)
    {
        var handed = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            LabOrders answer;
            try
            {
                using var response = await client.GetAsync($"/api/lab/orders?since={watermark}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    log($"The clinic didn't hand over orders ({(int)response.StatusCode} {await ErrorAsync(response, cancellationToken)}): asking again later.");
                    return handed;
                }

                answer = (await response.Content.ReadFromJsonAsync<LabOrders>(LabJson.Options, cancellationToken))!;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                log($"The clinic can't be asked for orders now ({e.GetType().Name}): asking again later.");
                return handed;
            }

            foreach (var order in answer.Orders)
            {
                var plan = SimulatedResults.For(order, settings);
                if (plan.Results.Count > 0 && pending.TryAdd(order.Id, plan))
                {
                    handed++;
                    log($"Order {order.Id}: {plan.Results.Count} {(plan.Results.Count == 1 ? "test" : "tests")}, results due {plan.Due:O}.");
                }
            }

            watermark = answer.Watermark;
            if (!answer.More)
            {
                break;
            }
        }

        return handed;
    }

    async Task<(int Filed, int Dropped)> FileDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (filed, dropped) = (0, 0);
        foreach (var plan in pending.Values.Where(plan => plan.Due <= now).OrderBy(plan => plan.Due).ToList())
        {
            HttpResponseMessage response;
            try
            {
                response = await client.PostAsJsonAsync(
                    $"/api/lab/orders/{plan.Order}/results", new LabResultsPost(plan.CollectedAt, plan.Results), LabJson.Options, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                log($"The clinic can't take results now ({e.GetType().Name}): trying again later.");
                break;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    pending.Remove(plan.Order);
                    filed++;
                    log($"Order {plan.Order}: results filed.");
                    continue;
                }

                var error = await ErrorAsync(response, cancellationToken);
                if (error == "in-the-future")
                {
                    log($"Order {plan.Order}: the clinic's clock is behind this lab's; trying again later.");
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                {
                    pending.Remove(plan.Order);
                    dropped++;
                    log($"Order {plan.Order}: the clinic takes no results for it from this lab ({error}); dropped.");
                    continue;
                }

                log($"The clinic didn't take results now ({(int)response.StatusCode} {error}): trying again later.");
                break;
            }
        }

        return (filed, dropped);
    }

    // The word of a refusal from the lab's door, or nothing when the answer isn't one.
    static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<LabRefusal>(LabJson.Options, cancellationToken))?.Error ?? "";
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return "";
        }
    }
}

// What one round did: orders newly handed over, orders filed and dropped, and how many still wait.
public sealed record LabRound(int Handed, int Filed, int Dropped, int Waiting);
