using Microsoft.Win32;

namespace Vapt.Core.Tweaks;

public enum TweakRisk
{
    Safe,
    Moderate,
}

public enum TweakGroup
{
    Privacy,
    Ads,
    AI,
    Interface,
    Performance,
    Gaming,
}

/// <summary>Um valor de registro que a otimização escreve. Value nulo = apagar o valor.</summary>
public sealed record RegOp(string Path, string Name, object? Value, RegistryValueKind Kind = RegistryValueKind.DWord)
{
    public bool IsMachine => Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);

    public RegistryKey Root => IsMachine ? Registry.LocalMachine : Registry.CurrentUser;

    public string SubKey => Path[(Path.IndexOf('\\') + 1)..];

    /// <summary>Chave única no diário de desfazer.</summary>
    public string Id => $"{Path}\\{Name}";
}

public sealed class Tweak
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required TweakGroup Group { get; init; }

    public required string Glyph { get; init; }

    public TweakRisk Risk { get; init; }

    public IReadOnlyList<RegOp> Registry { get; init; } = [];

    /// <summary>Serviços desligados (Start=4) ao aplicar; o tipo anterior vai para o diário.</summary>
    public IReadOnlyList<string> DisableServices { get; init; } = [];

    /// <summary>Otimizações feitas por comando (ex.: powercfg), com teste de estado próprio.</summary>
    public Func<Task<bool>>? IsAppliedCustom { get; init; }

    public Func<Task<bool>>? ApplyCustom { get; init; }

    public Func<Task<bool>>? RevertCustom { get; init; }

    /// <summary>Precisa reiniciar o Explorador (true) ou o PC (texto) para valer.</summary>
    public bool RestartExplorer { get; init; }

    public bool RequiresReboot { get; init; }

    /// <summary>Build mínimo do Windows em que a otimização existe (22000 = só Windows 11).</summary>
    public int MinBuild { get; init; }

    public bool AvailableHere => SystemInfo.Build >= MinBuild;

    /// <summary>Para otimizações por comando: se precisam de administrador (padrão: sim).</summary>
    public bool CustomNeedsAdmin { get; init; } = true;

    public bool NeedsAdmin => Registry.Any(r => r.IsMachine) || DisableServices.Count > 0 || (ApplyCustom is not null && CustomNeedsAdmin);
}
