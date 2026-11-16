using Microsoft.Win32;

namespace Vapt.Core.Tweaks;

/// <summary>
/// Otimizações do Vapt. Critério: funcionam no Windows 11 24H2/25H2, são reversíveis e não
/// removem componentes do sistema. Ficaram de fora de propósito: desligar o Defender (o
/// Tamper Protection ignora), HPET/useplatformclock e "NetworkThrottlingIndex" (sem efeito
/// medido), desligar SysMain/Prefetch e remover pacotes do sistema (quebram a Pesquisa e a Loja).
/// </summary>
public static class TweakCatalog
{
    private const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Advanced = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string Edge = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";
    private const string Chrome = @"HKLM\SOFTWARE\Policies\Google\Chrome";

    /// <summary>As que existem nesta versão do Windows (no 10 somem as que são só do 11).</summary>
    public static IEnumerable<Tweak> Available => All.Where(t => t.AvailableHere);

    public static IReadOnlyList<Tweak> All { get; } =
    [
        // ── Privacidade ──────────────────────────────────────────────────
        new()
        {
            Id = "ads-id",
            Group = TweakGroup.Privacy,
            Name = "Desligar o ID de anúncio",
            Description = "Apps deixam de usar um identificador seu para anúncios personalizados.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)],
        },
        new()
        {
            Id = "tailored",
            Group = TweakGroup.Privacy,
            Name = "Desligar experiências personalizadas",
            Description = "A Microsoft para de usar seus dados de diagnóstico para dicas e anúncios.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)],
        },
        new()
        {
            Id = "suggestions",
            Group = TweakGroup.Ads,
            Name = "Tirar sugestões e propagandas do Windows",
            Description = "Sem apps sugeridos no Iniciar, dicas na tela de bloqueio, anúncios no Explorador e instalação silenciosa de apps patrocinados.",
            Glyph = "",
            Registry =
            [
                new(Cdm, "SubscribedContent-338387Enabled", 0),
                new(Cdm, "SubscribedContent-338388Enabled", 0),
                new(Cdm, "SubscribedContent-338389Enabled", 0),
                new(Cdm, "SubscribedContent-338393Enabled", 0),
                new(Cdm, "SubscribedContent-353694Enabled", 0),
                new(Cdm, "SubscribedContent-353696Enabled", 0),
                new(Cdm, "SystemPaneSuggestionsEnabled", 0),
                new(Cdm, "SilentInstalledAppsEnabled", 0),
                new(Cdm, "SoftLandingEnabled", 0),
                new(Cdm, "RotatingLockScreenOverlayEnabled", 0),
                new(Advanced, "ShowSyncProviderNotifications", 0),
                new(Advanced, "Start_IrisRecommendations", 0),
                new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
            ],
        },
        new()
        {
            Id = "bing-search",
            Group = TweakGroup.Ads,
            Name = "Tirar a Web/Bing da pesquisa do Iniciar",
            Description = "A pesquisa do Iniciar mostra só apps, arquivos e configurações do PC.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)],
            RestartExplorer = true,
        },
        new()
        {
            Id = "activity",
            Group = TweakGroup.Privacy,
            Name = "Desligar o histórico de atividades",
            Description = "O Windows para de registrar e enviar os apps e arquivos que você usa.",
            Glyph = "",
            Registry =
            [
                new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            ],
        },
        new()
        {
            Id = "telemetry",
            Group = TweakGroup.Privacy,
            Name = "Telemetria no mínimo",
            Description = "Só os dados de diagnóstico obrigatórios e o serviço de telemetria (DiagTrack) desligado. No Home e no Pro o mínimo possível é \"Obrigatório\".",
            Glyph = "",
            Risk = TweakRisk.Moderate,
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1)],
            DisableServices = ["DiagTrack", "dmwappushservice"],
        },
        new()
        {
            Id = "consumer-features",
            Group = TweakGroup.Ads,
            Name = "Bloquear apps patrocinados automáticos",
            Description = "Impede o Windows de instalar sozinho jogos e apps de parceiros (Candy Crush e afins) depois de atualizações.",
            Glyph = "",
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1)],
        },
        new()
        {
            Id = "delivery-p2p",
            Group = TweakGroup.Privacy,
            Name = "Não enviar atualizações para outros PCs",
            Description = "O Windows para de usar sua internet para mandar pedaços de atualizações a outros PCs. Você continua baixando normalmente.",
            Glyph = "",
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0)],
        },
        new()
        {
            Id = "inking",
            Group = TweakGroup.Privacy,
            Name = "Não coletar digitação e escrita à mão",
            Description = "O Windows para de enviar o que você digita e escreve para \"melhorar o reconhecimento\".",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                new(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                new(@"HKCU\Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                new(@"HKCU\Software\Microsoft\Input\TIPC", "Enabled", 0),
            ],
        },
        // ── IA e Recall ──────────────────────────────────────────────────
        new()
        {
            Id = "recall-snapshots",
            MinBuild = 22000,
            Group = TweakGroup.AI,
            Name = "Impedir o Recall de fotografar a tela",
            Description = "O Recall tira uma captura da tela a cada poucos segundos e guarda num banco de dados pesquisável (gasta disco e memória). Isto desliga o salvamento das capturas.",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
            ],
        },
        new()
        {
            Id = "recall-remove",
            MinBuild = 22000,
            Group = TweakGroup.AI,
            Name = "Remover o Recall do Windows",
            Description = "Política da Microsoft que tira o componente do Recall de vez (Windows 11 24H2 ou mais novo). Vale depois de reiniciar.",
            Glyph = "",
            Risk = TweakRisk.Moderate,
            RequiresReboot = true,
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0)],
        },
        new()
        {
            Id = "click-to-do",
            MinBuild = 22000,
            Group = TweakGroup.AI,
            Name = "Desligar o Click to Do",
            Description = "Recurso que analisa o que está na tela quando você aperta Win + clique.",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
                new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
            ],
        },
        new()
        {
            Id = "copilot",
            Group = TweakGroup.AI,
            Name = "Desligar o Copilot do Windows",
            Description = "Tira o Copilot da barra de tarefas e do atalho Win + C. Para desinstalar o app, use a aba Apps.",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                new(Advanced, "ShowCopilotButton", 0),
            ],
            RestartExplorer = true,
        },
        new()
        {
            Id = "ai-apps",
            MinBuild = 22000,
            Group = TweakGroup.AI,
            Name = "Tirar a IA do Paint e do Bloco de Notas",
            Description = "Some com o Cocreator, Preenchimento generativo e Criador de imagens do Paint e com a reescrita por IA do Bloco de Notas.",
            Glyph = "",
            Registry =
            [
                new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
                new(@"HKLM\SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1),
            ],
        },
        new()
        {
            Id = "edge-copilot",
            Group = TweakGroup.AI,
            Name = "Tirar a IA do Edge",
            Description = "Some com a barra lateral do Copilot, o Copilot lendo a página, a escrita assistida e a pesquisa no histórico por IA.",
            Glyph = "",
            Registry =
            [
                new(Edge, "HubsSidebarEnabled", 0),
                new(Edge, "CopilotPageContext", 0),
                new(Edge, "CopilotCDPPageContext", 0),
                new(Edge, "Microsoft365CopilotChatIconEnabled", 0),
                new(Edge, "EdgeHistoryAISearchEnabled", 0),
                new(Edge, "ComposeInlineEnabled", 0),
                new(Edge, "NewTabPageBingChatEnabled", 0),
            ],
        },
        new()
        {
            Id = "chrome-ai",
            Group = TweakGroup.AI,
            Name = "Tirar a IA do Chrome",
            Description = "Desliga Gemini, \"Me ajude a escrever\", modo IA e as sugestões por IA do Google Chrome (pelas políticas do próprio Chrome).",
            Glyph = "",
            Registry =
            [
                new(Chrome, "GeminiSettings", 1),
                new(Chrome, "AIModeSettings", 1),
                new(Chrome, "HelpMeWriteSettings", 2),
                new(Chrome, "HistorySearchSettings", 2),
                new(Chrome, "TabCompareSettings", 2),
                new(Chrome, "CreateThemesSettings", 2),
                new(Chrome, "GenAILocalFoundationalModelSettings", 1),
            ],
        },

        // ── Anúncios e sugestões (novos) ─────────────────────────────────
        new()
        {
            Id = "start-recommended",
            MinBuild = 22000,
            Group = TweakGroup.Ads,
            Name = "Tirar a seção \"Recomendados\" do Iniciar",
            Description = "Some a lista de arquivos recentes e apps sugeridos embaixo do menu Iniciar.",
            Glyph = "",
            Registry = [new(@"HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1)],
            RestartExplorer = true,
        },
        new()
        {
            Id = "search-highlights",
            Group = TweakGroup.Ads,
            Name = "Tirar os destaques da pesquisa",
            Description = "Sem as ilustrações de datas comemorativas e \"tendências\" na caixa de pesquisa.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0)],
        },
        new()
        {
            Id = "phone-link-start",
            MinBuild = 22000,
            Group = TweakGroup.Ads,
            Name = "Tirar o Vincular ao Celular do Iniciar",
            Description = "Esconde o painel do celular que aparece ao lado do menu Iniciar.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", "IsEnabled", 0)],
        },
        new()
        {
            Id = "settings-365-ads",
            MinBuild = 22000,
            Group = TweakGroup.Ads,
            Name = "Tirar propaganda do Microsoft 365 das Configurações",
            Description = "Sem os cartões oferecendo Microsoft 365, OneDrive e Game Pass na página da conta.",
            Glyph = "",
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableConsumerAccountStateContent", 1)],
        },
        new()
        {
            Id = "edge-ads",
            Group = TweakGroup.Ads,
            Name = "Tirar anúncios e compras do Edge",
            Description = "Sem assistente de compras, recompensas, notícias na nova guia, sugestões e insistência para ser o navegador padrão.",
            Glyph = "",
            Registry =
            [
                new(Edge, "EdgeShoppingAssistantEnabled", 0),
                new(Edge, "ShowMicrosoftRewards", 0),
                new(Edge, "ShowRecommendationsEnabled", 0),
                new(Edge, "SpotlightExperiencesAndRecommendationsEnabled", 0),
                new(Edge, "NewTabPageContentEnabled", 0),
                new(Edge, "HideFirstRunExperience", 1),
                new(Edge, "DefaultBrowserSettingsCampaignEnabled", 0),
                new(Edge, "WalletDonationEnabled", 0),
                new(Edge, "ShowAcrobatSubscriptionButton", 0),
            ],
        },
        new()
        {
            Id = "device-apps",
            Group = TweakGroup.Ads,
            Name = "Não instalar apps de fabricante ao conectar aparelhos",
            Description = "Ao plugar mouse, fone ou impressora, o Windows deixa de baixar sozinho o app do fabricante. Os drivers continuam vindo.",
            Glyph = "",
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork", 1)],
        },

        // ── Privacidade (novos) ──────────────────────────────────────────
        new()
        {
            Id = "feedback",
            Group = TweakGroup.Privacy,
            Name = "Nunca pedir feedback",
            Description = "O Windows para de perguntar \"o que você achou?\" e de mandar as respostas.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)],
        },
        new()
        {
            Id = "online-speech",
            Group = TweakGroup.Privacy,
            Name = "Desligar o reconhecimento de fala online",
            Description = "Sua voz não vai para a nuvem da Microsoft. O ditado offline continua funcionando.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0)],
        },
        new()
        {
            Id = "app-tracking",
            Group = TweakGroup.Privacy,
            Name = "Não rastrear os apps que você abre",
            Description = "O Windows para de registrar o uso de apps (também some a lista \"Mais usados\" do Iniciar).",
            Glyph = "",
            Registry = [new(Advanced, "Start_TrackProgs", 0)],
        },
        new()
        {
            Id = "location",
            Group = TweakGroup.Privacy,
            Name = "Desligar a localização",
            Description = "Apps e o Windows deixam de saber onde você está. Clima, mapas e \"Encontrar meu dispositivo\" param.",
            Glyph = "",
            Risk = TweakRisk.Moderate,
            Registry = [new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", RegistryValueKind.String)],
        },

        // ── Sistema (novos) ──────────────────────────────────────────────
        new()
        {
            Id = "startup-delay",
            Group = TweakGroup.Performance,
            Name = "Abrir os apps de inicialização sem esperar",
            Description = "O Windows segura os apps de inicialização por alguns segundos depois do login. Isto tira a espera (bom em SSD).",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "Startupdelayinmsec", 0),
                new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "WaitForIdleState", 0),
            ],
        },
        new()
        {
            Id = "no-auto-reboot",
            Group = TweakGroup.Performance,
            Name = "Não reiniciar sozinho com você usando",
            Description = "O Windows Update espera você reiniciar em vez de reiniciar o PC sozinho enquanto alguém está logado.",
            Glyph = "",
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1)],
        },

        // ── Interface ────────────────────────────────────────────────────
        new()
        {
            Id = "classic-menu",
            MinBuild = 22000,
            Group = TweakGroup.Interface,
            Name = "Menu de contexto clássico",
            Description = "O botão direito volta a mostrar todas as opções, sem o \"Mostrar mais opções\".",
            Glyph = "",
            CustomNeedsAdmin = false,
            RestartExplorer = true,
            IsAppliedCustom = () =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassicMenuKey + @"\InprocServer32");
                return Task.FromResult(key is not null);
            },
            ApplyCustom = () =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuKey + @"\InprocServer32");
                key.SetValue("", "");
                return Task.FromResult(true);
            },
            RevertCustom = () =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuKey, throwOnMissingSubKey: false);
                return Task.FromResult(true);
            },
        },
        new()
        {
            Id = "file-ext",
            Group = TweakGroup.Interface,
            Name = "Mostrar extensões de arquivo",
            Description = "Mostra .exe, .pdf, .jpg… no nome. Ajuda a perceber arquivos disfarçados.",
            Glyph = "",
            Registry = [new(Advanced, "HideFileExt", 0)],
            RestartExplorer = true,
        },
        new()
        {
            Id = "this-pc",
            Group = TweakGroup.Interface,
            Name = "Abrir o Explorador em \"Este Computador\"",
            Description = "Em vez da tela Início com arquivos recentes.",
            Glyph = "",
            Registry = [new(Advanced, "LaunchTo", 1)],
        },
        new()
        {
            Id = "taskbar-left",
            MinBuild = 22000,
            Group = TweakGroup.Interface,
            Name = "Barra de tarefas à esquerda",
            Description = "Ícones e Iniciar no canto, como no Windows 10.",
            Glyph = "",
            Registry = [new(Advanced, "TaskbarAl", 0)],
        },
        new()
        {
            Id = "end-task",
            MinBuild = 22000,
            Group = TweakGroup.Interface,
            Name = "\"Finalizar tarefa\" na barra de tarefas",
            Description = "Botão direito num app travado na barra mostra \"Finalizar tarefa\".",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1)],
        },
        new()
        {
            Id = "widgets",
            MinBuild = 22000,
            Group = TweakGroup.Ads,
            Name = "Desligar Widgets",
            Description = "Tira o painel de notícias e clima da barra (por política, sem desinstalar nada).",
            Glyph = "",
            Registry = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
            RestartExplorer = true,
        },

        // ── Desempenho ──────────────────────────────────────────────────
        new()
        {
            Id = "animations",
            Group = TweakGroup.Performance,
            Name = "Menos animações",
            Description = "Janelas abrem e minimizam na hora. Ajuda em PCs mais fracos.",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String),
                new(Advanced, "TaskbarAnimations", 0),
            ],
        },
        new()
        {
            Id = "storage-sense",
            Group = TweakGroup.Performance,
            Name = "Ligar o Sensor de Armazenamento",
            Description = "O próprio Windows apaga temporários e itens antigos da Lixeira quando o disco enche.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1)],
        },
        new()
        {
            Id = "hibernate",
            Group = TweakGroup.Performance,
            Name = "Desligar a hibernação",
            Description = "Apaga o hiberfil.sys (do tamanho de boa parte da sua RAM). Também desliga a Inicialização Rápida. Não use em notebook se você hiberna.",
            Glyph = "",
            Risk = TweakRisk.Moderate,
            IsAppliedCustom = () =>
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
                return Task.FromResult(key?.GetValue("HibernateEnabled") is 0);
            },
            ApplyCustom = async () => (await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/hibernate off")).Ok,
            RevertCustom = async () => (await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/hibernate on")).Ok,
        },
        new()
        {
            Id = "reserved-storage",
            Group = TweakGroup.Performance,
            Name = "Desligar o armazenamento reservado",
            Description = "Libera ~7 GB que o Windows guarda para atualizações. Se o disco estiver cheio, atualizações podem falhar.",
            Glyph = "",
            Risk = TweakRisk.Moderate,
            IsAppliedCustom = async () => (await ProcessRunner.PowerShellAsync("(Get-WindowsReservedStorageState -Online).ReservedStorageState")).Output.Trim() == "Disabled",
            ApplyCustom = async () => (await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "dism.exe"), "/Online /Set-ReservedStorageState /State:Disabled /NoRestart")).Ok,
            RevertCustom = async () => (await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "dism.exe"), "/Online /Set-ReservedStorageState /State:Enabled /NoRestart")).Ok,
        },

        // ── Jogos ────────────────────────────────────────────────────────
        new()
        {
            Id = "game-dvr",
            Group = TweakGroup.Gaming,
            Name = "Desligar a gravação em segundo plano",
            Description = "O Game DVR para de gravar os últimos minutos o tempo todo. A Game Bar continua funcionando.",
            Glyph = "",
            Registry =
            [
                new(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0),
                new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
            ],
        },
        new()
        {
            Id = "game-mode",
            Group = TweakGroup.Gaming,
            Name = "Modo de Jogo ligado",
            Description = "O Windows dá prioridade ao jogo e segura atualizações durante a partida.",
            Glyph = "",
            Registry = [new(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)],
        },
        new()
        {
            Id = "hags",
            Group = TweakGroup.Gaming,
            Name = "Agendamento de GPU por hardware",
            Description = "A placa de vídeo gerencia a própria memória; menos latência em jogos. Precisa de GPU recente e reiniciar.",
            Glyph = "",
            Risk = TweakRisk.Moderate,
            RequiresReboot = true,
            Registry = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)],
        },
    ];

    public static string GroupName(TweakGroup g) => g switch
    {
        TweakGroup.Privacy => "Privacidade",
        TweakGroup.Ads => "Anúncios e sugestões",
        TweakGroup.AI => "IA e Recall",
        TweakGroup.Interface => "Interface",
        TweakGroup.Performance => "Sistema e espaço",
        _ => "Jogos",
    };
}
