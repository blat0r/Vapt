using Vapt.Core.Tweaks;

namespace Vapt.Core.Debloat;

/// <summary>O que o "Debloat recomendado" vai fazer neste PC (calculado na hora).</summary>
public sealed record DebloatPlan(
    IReadOnlyList<InstalledBloat> Apps,
    IReadOnlyList<Tweak> Ads,
    IReadOnlyList<Tweak> Privacy,
    IReadOnlyList<Tweak> Ai,
    IReadOnlyList<BloatTask> Tasks,
    IReadOnlyList<BloatService> Services)
{
    public int Total => Apps.Count + Ads.Count + Privacy.Count + Ai.Count + Tasks.Count + Services.Count;
}

/// <summary>
/// "Debloat recomendado", como o modo padrão do Win11Debloat: só o que é marcado como seguro
/// e reversível. Fica de fora tudo que tem "Atenção" (localização, remover o Recall,
/// telemetria total, serviços de segundo plano), que continua a um clique nas abas.
/// </summary>
public static class DebloatPreset
{
    public static async Task<DebloatPlan> PlanAsync(CancellationToken cancellation = default)
    {
        var admin = Elevation.IsAdministrator;
        var apps = (await DebloatEngine.DetectAsync(cancellation))
            .Where(a => a.App.Recommended && a.App.Selected && (a.PackageFullName is not null || admin))
            .ToList();

        async Task<List<Tweak>> Pending(TweakGroup group)
        {
            var list = new List<Tweak>();
            foreach (var t in TweakCatalog.Available.Where(t => t.Group == group && t.Risk == TweakRisk.Safe && (!t.NeedsAdmin || admin)))
            {
                if (!await TweakEngine.IsAppliedAsync(t))
                {
                    list.Add(t);
                }
            }

            return list;
        }

        var tasks = new List<BloatTask>();
        var services = new List<BloatService>();
        if (admin)
        {
            var states = (await DebloatEngine.TaskStatesAsync(cancellation)).ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
            tasks = DebloatCatalog.Tasks.Where(t => states.TryGetValue(t.Path, out var s) && s.Exists && s.Enabled).ToList();
            services = DebloatCatalog.Services
                .Where(s => s.Kind is ServiceKind.Telemetry or ServiceKind.AI && !s.Caution)
                .Where(s => TweakEngine.ServiceStart(s.Name) is int start && start < s.Target)
                .ToList();
        }

        return new DebloatPlan(apps, await Pending(TweakGroup.Ads), await Pending(TweakGroup.Privacy), await Pending(TweakGroup.AI), tasks, services);
    }

    /// <summary>Aplica o plano item por item; um erro não para o resto.</summary>
    public static async Task<(int Done, List<string> Failed)> ApplyAsync(DebloatPlan plan, IProgress<string> progress, CancellationToken cancellation = default)
    {
        var done = 0;
        var failed = new List<string>();

        async Task Step(string name, Func<Task> action)
        {
            cancellation.ThrowIfCancellationRequested();
            progress.Report(name);
            try
            {
                await action();
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Debloat recomendado: {name}: {ex.Message}");
                failed.Add(name);
            }
        }

        foreach (var t in plan.Ads.Concat(plan.Privacy).Concat(plan.Ai))
        {
            await Step(t.Name, () => TweakEngine.ApplyAsync(t));
        }

        foreach (var task in plan.Tasks)
        {
            await Step(task.Title, () => DebloatEngine.SetTaskEnabledAsync(task, enabled: false));
        }

        foreach (var s in plan.Services)
        {
            await Step(s.Title, () => TweakEngine.SetServiceAsync(s.Name, s.Target));
        }

        if (plan.Apps.Count > 0)
        {
            progress.Report("Removendo apps…");
            var (removed, appFailed) = await DebloatEngine.RemoveAsync(plan.Apps, progress, cancellation);
            done += removed;
            failed.AddRange(appFailed);
        }

        Log.Info($"Debloat recomendado: {done} itens, {failed.Count} falhas.");
        return (done, failed);
    }
}
