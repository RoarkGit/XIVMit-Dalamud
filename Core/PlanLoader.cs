using System.Net;
using System.Net.Http;
using Dalamud.Plugin.Services;
using XIVMit.Api;

namespace XIVMit.Core;

public enum LoadStatus { Idle, Loading, Loaded, Failed }

/// <summary>
/// Fetches a plan, its fight, and the abilities for every job on its roster, then builds a
/// <see cref="PlanContext"/> off the framework thread.
/// </summary>
public sealed class PlanLoader : IDisposable
{
    private readonly XivMitApi api;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private CancellationTokenSource? cts;

    public LoadStatus Status { get; private set; } = LoadStatus.Idle;
    public string? Error { get; private set; }
    public PlanContext? Context { get; private set; }

    public event Action<PlanContext?>? Loaded;

    public PlanLoader(XivMitApi api, IFramework framework, IPluginLog log)
    {
        this.api = api;
        this.framework = framework;
        this.log = log;
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }

    public void Clear()
    {
        cts?.Cancel();
        Context = null;
        Status = LoadStatus.Idle;
        Error = null;
        Loaded?.Invoke(null);
    }

    public void Load(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        Status = LoadStatus.Loading;
        Error = null;

        _ = Task.Run(async () =>
        {
            var what = "plan";
            try
            {
                var plan = await api.GetPlanAsync(code, ct);

                what = "fight or ability data";
                var fight = await api.GetFightAsync(plan.FightId, ct);

                var jobs = plan.Players
                    .Select(p => p.Job)
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var fetched = await Task.WhenAll(jobs.Select(async j =>
                    (Job: j, Abilities: await api.GetJobAsync(j, ct))));

                ct.ThrowIfCancellationRequested();

                var byJob = fetched.ToDictionary(x => x.Job, x => x.Abilities, StringComparer.OrdinalIgnoreCase);
                var ctx = PlanContext.Build(plan, fight, byJob);

                // Publish on the framework thread; Loaded handlers and the windows read this there.
                await framework.RunOnFrameworkThread(() =>
                {
                    Context = ctx;
                    Status = LoadStatus.Loaded;
                    Loaded?.Invoke(ctx);
                });

                log.Info($"XIVMit loaded plan {plan.Code} ({fight.Name}): {ctx.Mits.Count} mits.");
            }
            // Only our own token means a newer Load() took over. HttpClient's timeout also throws
            // OperationCanceledException, and that one should surface as an error.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Status = LoadStatus.Failed;
                Error = Friendly(ex, what);
                log.Error(ex, $"XIVMit failed to load plan {code}.");
            }
        }, ct);
    }

    private static string Friendly(Exception ex, string what) => ex switch
    {
        XivMitApiException { StatusCode: HttpStatusCode.NotFound } when what == "plan" =>
            "No plan found with that code. Double-check it against the URL on xivmit.app.",
        XivMitApiException { StatusCode: HttpStatusCode.NotFound } =>
            $"Couldn't find the {what} this plan needs - it may reference removed content.",
        HttpRequestException =>
            "Couldn't reach xivmit.app. Check your connection and try again.",
        TaskCanceledException =>
            "Timed out reaching xivmit.app. Try again in a moment.",
        XivMitApiException =>
            "xivmit.app had trouble responding. Try again in a moment.",
        _ => $"Something went wrong loading the plan's {what}. Try again in a moment.",
    };
}
