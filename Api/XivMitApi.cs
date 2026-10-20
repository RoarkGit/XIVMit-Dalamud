using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace XIVMit.Api;

/// <summary>Read-only client for xivmit.app's public endpoints.</summary>
public sealed class XivMitApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient http = new()
    {
        BaseAddress = new Uri("https://xivmit.app/"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    public XivMitApi() => http.DefaultRequestHeaders.UserAgent.ParseAdd("XIVMit-Dalamud/0.1");

    public void Dispose() => http.Dispose();

    public async Task<Plan> GetPlanAsync(string code, CancellationToken ct = default)
    {
        code = code.Trim().ToUpperInvariant();
        var plan = await GetAsync<Plan>($"api/plans/{Uri.EscapeDataString(code)}", ct);
        // The code is only in the URL, not the response body.
        if (string.IsNullOrEmpty(plan.Code)) plan.Code = code;
        return plan;
    }

    public Task<Fight> GetFightAsync(string fightId, CancellationToken ct = default) =>
        GetAsync<Fight>($"api/fights/{Uri.EscapeDataString(fightId)}", ct);

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
                (string.IsNullOrWhiteSpace(body) ? "" : $": {Truncate(body, 200)}"),
                res.StatusCode);
        }

        var value = await res.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
        return value ?? throw new XivMitApiException($"Empty response body from /{path}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}

/// <summary>The message is for the log. PlanLoader turns this into the text the UI shows.</summary>
public sealed class XivMitApiException(string message, HttpStatusCode? statusCode = null)
    : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
