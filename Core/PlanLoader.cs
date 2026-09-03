using System.Net;
using System.Net.Http;
using Dalamud.Plugin.Services;
using XIVMit.Api;

namespace XIVMit.Core;

public enum LoadStatus { Idle, Loading, Loaded, Failed }

/// <summary>
/// Fetches a plan plus everything it depends on (its fight, and the ability list for every job on
/// the roster) and resolves them into a <see cref="PlanContext"/>. All network work happens off
/// the framework thread; only the final assignment of <see cref="Context"/> is observed by it.
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
            Plan plan;
            try
            {
                plan = await api.GetPlanAsync(code, ct);
            }
            // cts.Cancel() means a newer Load() superseded this one - leave state to that one.
            // Guarded on ct specifically: HttpClient's own 15s timeout throws a subclass of
            // OperationCanceledException too, on its own internal token, and without this guard
            // a genuine timeout got silently swallowed as if it were a supersession - the UI just
            // sat on "Loading..." forever with no error shown at all.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Fail(Friendly(ex, "plan"), ex, code);
                return;
            }

            try
            {
                var fight = await api.GetFightAsync(plan.FightId, ct);

                // One request per distinct job on the roster, in parallel. These are cached
                // hard server-side (max-age=3600), so repeat loads are cheap.
                var jobs = plan.Players
                    .Select(p => p.Job)
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var fetched = await Task.WhenAll(jobs.Select(async j =>
                    (Job: j, Abilities: await api.GetJobAsync(j, ct))));

                ct.ThrowIfCancellationRequested();

                var byJob = fetched.ToDictionary(
                    x => x.Job, x => x.Abilities, StringComparer.OrdinalIgnoreCase);

                var ctx = PlanContext.Build(plan, fight, byJob);

                // Published on the framework thread on purpose - everything downstream of Loaded
                // (the tracker's lookup tables, the windows' reads of Context) gets touched by
                // the framework and draw threads, and handing it over straight from this
                // background task would race with them.
                await framework.RunOnFrameworkThread(() =>
                {
                    Context = ctx;
                    Status = LoadStatus.Loaded;
                    Loaded?.Invoke(ctx);
                });

                log.Info($"XIVMit loaded plan {plan.Code} ({fight.Name}): {ctx.Mits.Count} mits.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Superseded - see the identical guard above.
            }
            catch (Exception ex)
            {
                Fail(Friendly(ex, "fight or ability data"), ex, code);
            }
        }, ct);
    }

    private void Fail(string friendly, Exception ex, string code)
    {
        Status = LoadStatus.Failed;
        Error = friendly;
        // Full technical detail (status, path, response snippet) goes to the log only - never
        // shown in the UI, which is exactly what this method exists to avoid.
        log.Error(ex, $"XIVMit failed to load plan {code}.");
    }

    /// <summary>
    /// Maps an exception from a plan/fight/job fetch to something worth showing a player, rather
    /// than the raw "404 Not Found from /api/plans/UMAD-XXXXX: {...}" the exception itself
    /// carries. <paramref name="what"/> names the resource being fetched, for the fallback and
    /// not-found phrasing.
    /// </summary>
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
