using System.Text.Json;

namespace Vapt.Core.Debloat;

public enum AppGroup
{
    Microsoft,
    Sponsored,
    Oem,
}

/// <summary>App pré-instalado que pode sair (catálogo do Win11Debloat, ver NOTICE.md).</summary>
public sealed record BloatApp(
    string Id,
    string Name,
    string Description,
    /// <summary>"safe" no Win11Debloat: sem efeito colateral conhecido.</summary>
    bool Recommended,
    /// <summary>Marcado por padrão.</summary>
    bool Selected,
    string Method,
    AppGroup Group);

/// <summary>Serviço que pode ir para Manual ou Desativado sem quebrar o Windows.</summary>
public sealed record BloatService(
    string Name,
    string Title,
    string Description,
    /// <summary>3 = Manual (abre só quando algo precisa), 4 = Desativado.</summary>
    int Target,
    /// <summary>Tipo de início padrão do Windows, para desfazer sem diário.</summary>
    int Default,
    bool Caution = false,
    ServiceKind Kind = ServiceKind.Background);

public enum ServiceKind
{
    /// <summary>Coleta de dados (aparece na aba Privacidade).</summary>
    Telemetry,
    /// <summary>Base dos recursos de IA (aba IA e Recall).</summary>
    AI,
    /// <summary>Recursos em segundo plano que pouca gente usa (aba Serviços).</summary>
    Background,
}

/// <summary>Tarefa agendada de coleta de dados.</summary>
public sealed record BloatTask(string Path, string Title, string Description);

public static class DebloatCatalog
{
    private static readonly Lazy<IReadOnlyList<BloatApp>> AppsLazy = new(LoadApps);

    public static IReadOnlyList<BloatApp> Apps => AppsLazy.Value;

    private static IReadOnlyList<BloatApp> LoadApps()
    {
        using var stream = typeof(DebloatCatalog).Assembly.GetManifestResourceStream("Vapt.Debloat.apps.json")
            ?? throw new InvalidOperationException("Catálogo de apps não embutido.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(e => new BloatApp(
            e.GetProperty("id").GetString()!,
            e.GetProperty("name").GetString() ?? e.GetProperty("id").GetString()!,
            e.GetProperty("description").GetString() ?? "",
            e.GetProperty("recommended").GetBoolean(),
            e.GetProperty("selected").GetBoolean(),
            e.GetProperty("method").GetString() ?? "Appx",
            e.GetProperty("group").GetString() switch
            {
                "oem" => AppGroup.Oem,
                "sponsored" => AppGroup.Sponsored,
                _ => AppGroup.Microsoft,
            })).ToList();
    }

    public static string GroupName(AppGroup g) => g switch
    {
        AppGroup.Microsoft => "Apps da Microsoft",
        AppGroup.Sponsored => "Apps patrocinados e jogos",
        _ => "Apps do fabricante do PC",
    };

    /// <summary>
    /// Serviços. Lista curta de propósito: o WinUtil já cortou a dele para 5 depois de relatos
    /// de quebra. Nada de Spooler sem aviso, WSearch, SysMain, BITS, Windows Update ou Xbox.
    /// </summary>
    public static IReadOnlyList<BloatService> Services { get; } =
    [
        new("DiagTrack", "Telemetria (Experiências do Usuário Conectado)", "Envia dados de uso para a Microsoft. Desligar não afeta nada que você usa.", 4, 2, Kind: ServiceKind.Telemetry),
        new("dmwappushservice", "Roteamento de mensagens WAP push", "Usado pela telemetria e por gerenciamento corporativo de dispositivos.", 4, 3, Kind: ServiceKind.Telemetry),
        new("MapsBroker", "Gerenciador de mapas baixados", "Só o app Mapas usa. Em Manual ele abre quando o app pedir.", 3, 2),
        new("RetailDemo", "Modo de demonstração de loja", "Serviço das máquinas expostas em lojas.", 4, 3),
        new("WSAIFabricSvc", "Serviço de IA do Windows", "Base dos recursos de IA (Copilot, Recall, Click to Do). Em Manual só abre se algo usar.", 3, 2, Kind: ServiceKind.AI),
        new("TrkWks", "Rastreamento de links distribuídos", "Conserta atalhos para arquivos movidos em redes corporativas.", 3, 2),
        new("Fax", "Fax", "Envio e recebimento de fax pelo PC.", 4, 3),
        new("WpcMonSvc", "Controle dos Pais", "Limites de tempo e filtros da Família Microsoft. Deixe se usa o Family Safety.", 3, 3, Caution: true),
        new("Spooler", "Spooler de impressão", "Só desligue se você não usa impressora nem \"Imprimir em PDF\".", 4, 2, Caution: true),
        new("RemoteRegistry", "Registro remoto", "Deixa outro PC mexer no seu registro pela rede. Já vem desativado; garante que fique.", 4, 4),
    ];

    /// <summary>Tarefas de coleta de dados (programa de experiência do usuário, avaliação de compatibilidade…).</summary>
    public static IReadOnlyList<BloatTask> Tasks { get; } =
    [
        new(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Avaliador de compatibilidade", "Varre os programas e envia o inventário para a Microsoft. Pesa no disco quando roda."),
        new(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater", "Atualizador de dados de programas", "Coleta dados de uso de programas para o programa de experiência do usuário."),
        new(@"\Microsoft\Windows\Application Experience\StartupAppTask", "Análise de apps da inicialização", "Mede o impacto dos apps de inicialização e envia telemetria."),
        new(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", "Programa de experiência do usuário", "Junta e envia dados de uso do Windows."),
        new(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", "Telemetria de USB", "Coleta estatísticas de uso das portas USB."),
        new(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Coleta de diagnóstico de disco", "Envia informações dos discos para a Microsoft."),
        new(@"\Microsoft\Windows\Feedback\Siuf\DmClient", "Pedidos de feedback", "Busca as perguntas \"o que achou do Windows?\"."),
        new(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload", "Pedidos de feedback (cenários)", "Baixa cenários de pesquisa de feedback."),
        new(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting", "Envio de relatórios de erro", "Envia relatórios de travamento na fila. Os relatórios continuam salvos no PC."),
        new(@"\Microsoft\Windows\Autochk\Proxy", "Coleta do Autochk", "Coleta dados da verificação de disco para o programa de experiência do usuário."),
    ];
}
