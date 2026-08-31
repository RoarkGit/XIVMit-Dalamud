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
            try
            {
                var plan = await api.GetPlanAsync(code, ct);
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

                // Publish on the framework thread. Everything downstream of Loaded (the
                // tracker's lookup tables, the windows' reads of Context) is touched by the
                // framework and draw threads, so handing it over from this background task
                // directly would race with them.
                await framework.RunOnFrameworkThread(() =>
                {
                    Context = ctx;
                    Status = LoadStatus.Loaded;
                    Loaded?.Invoke(ctx);
                });

                log.Info($"XIVMit loaded plan {plan.Code} ({fight.Name}): {ctx.Mits.Count} mits.");
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer load; leave state to that one.
            }
            catch (Exception ex)
            {
                Status = LoadStatus.Failed;
                Error = ex.Message;
                log.Error(ex, $"XIVMit failed to load plan {code}.");
            }
        }, ct);
    }
}
