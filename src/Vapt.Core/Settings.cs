using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapt.Core;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Preferências do Vapt em %LOCALAPPDATA%\Vapt\settings.json.</summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Criar ponto de restauração antes de aplicar otimizações.</summary>
    public bool RestorePointBeforeTweaks { get; set; } = true;

    /// <summary>Apagar no mapa do disco manda para a Lixeira (em vez de apagar de vez).</summary>
    public bool DeleteToRecycleBin { get; set; } = true;

    public string? LastScanPath { get; set; }

    /// <summary>Procura e baixa versões novas no GitHub sozinho.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>O que aparece no overlay de FPS.</summary>
    public Perf.OverlayOptions Overlay { get; set; } = new();

    /// <summary>Liga o overlay sozinho quando o Vapt abre.</summary>
    public bool OverlayAtStart { get; set; }

    /// <summary>Pede administrador (UAC) toda vez que o Vapt abre.</summary>
    public bool AlwaysAdmin { get; set; }

    /// <summary>Já tentou instalar o PawnIO sozinho (só tenta uma vez; depois fica o botão em Sensores).</summary>
    public bool PawnIoAutoTried { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"settings.json ilegível, usando o padrão: {ex.Message}");
        }

        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Não foi possível salvar as configurações.", ex);
        }
    }
}
