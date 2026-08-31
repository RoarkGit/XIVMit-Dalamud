using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace XIVMit.Api;

/// <summary>
/// Thin read-only client for the public xivmit.app endpoints. Every call this plugin makes is
/// unauthenticated: GET /api/plans/:code, /api/fights/:id and /api/jobs/:job are all public.
/// </summary>
public sealed class XivMitApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string BaseUrl = "https://xivmit.app/";

    private readonly HttpClient http;

    public XivMitApi()
    {
        http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XIVMit-Dalamud/0.1");
    }

    public void Dispose() => http.Dispose();

    /// <summary>Fetch a plan by its share code (e.g. "UMAD-4C3ME5"). Case-insensitive server-side.</summary>
    public async Task<Plan> GetPlanAsync(string code, CancellationToken ct = default)
    {
        var plan = await GetAsync<Plan>($"api/plans/{Uri.EscapeDataString(code.Trim().ToUpperInvariant())}", ct);
        // The server keys the plan by URL, not by a body field, so backfill it for display.
        if (string.IsNullOrEmpty(plan.Code)) plan.Code = code.Trim().ToUpperInvariant();
        return plan;
    }

    public Task<Fight> GetFightAsync(string fightId, CancellationToken ct = default) =>
        GetAsync<Fight>($"api/fights/{Uri.EscapeDataString(fightId)}", ct);

    /// <summary>Abilities for one job abbreviation (e.g. "WAR"). Server injects the `job` field.</summary>
    public Task<List<Ability>> GetJobAsync(string job, CancellationToken ct = default) =>
        GetAsync<List<Ability>>($"api/jobs/{Uri.EscapeDataString(job.ToLowerInvariant())}", ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await http.GetAsync(path, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new XivMitApiException(
                $"{(int)res.StatusCode} {res.ReasonPhrase} from /{path}" +
                (string.IsNullOrWhiteSpace(body) ? "" : $": {Truncate(body, 200)}"));
        }

        var value = await res.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
        return value ?? throw new XivMitApiException($"Empty response body from /{path}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}

public sealed class XivMitApiException(string message) : Exception(message);
