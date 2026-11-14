# Vapt

Por **blat0r**.

Deixa o PC rápido como um *vapt vupt*: limpeza, otimizações, apps que abrem com o Windows, debloat, mapa do disco no estilo SpaceSniffer e pendrive no estilo Rufus. Interface Fluent do Windows 11 (WinUI 3, Mica), leve e nativa.

## Páginas

| Página | O que faz |
|---|---|
| **Início (Check-up)** | Analisa o PC ao abrir e dá uma **nota de 0 a 100** (`Core/HealthCheck.cs`): lixo, bloatware, inicialização cheia, privacidade, Recall, disco cheio, hibernação, Sensor de Armazenamento e tempo sem reiniciar. Cada ponto tem **Corrigir** ou leva à página certa; **Vapt vupt: corrigir tudo** resolve só o que é seguro e reversível. CPU/RAM/disco ao vivo. |
| **Debloat** | Agrupado por objetivo, como o privacy.sexy e o Win11Debloat: **Visão geral** com o "Debloat recomendado" (só o seguro e reversível, de uma vez), **Apps** (catálogo do [Win11Debloat](https://github.com/Raphire/Win11Debloat), ver `NOTICE.md`), **Anúncios** (Iniciar, pesquisa, tela de bloqueio, Configurações, Edge), **Privacidade** (ajustes + serviços e tarefas de telemetria), **IA e Recall** (Recall, Click to Do, Copilot, IA do Edge/Chrome/Paint/Bloco de Notas) e **Serviços** em segundo plano. |
| **Limpeza** | Mede cada categoria antes e deixa escolher. Só cache e temporários; itens com efeito colateral (miniaturas, shaders) vêm desmarcados. Arquivo em uso é pulado. DISM `/StartComponentCleanup` para a WinSxS. |
| **Otimizações** | Como o Windows funciona: interface, sistema e espaço, jogos. Cada valor original vai para `backup.json` antes de mudar; **Desfazer tudo** volta exatamente ao que era. Ponto de restauração antes da primeira mudança de sistema. |
| **Inicialização** | Liga/desliga pelo mesmo lugar do Gerenciador de Tarefas (`StartupApproved`), sem apagar nada. |
| **Aplicativos** | No estilo Revo Uninstaller / Bulk Crap Uninstaller: depois do desinstalador, procura **sobras** (pastas em Arquivos de Programas/ProgramData/AppData, atalhos do Iniciar, chaves `Software\Editora\Programa`), que vão para a Lixeira ou ganham backup `.reg` antes de sair. **Desinstalar vários** de uma vez (silencioso quando o programa permite ou é MSI), detecta **entradas quebradas** (programa apagado na mão) e aba **Atualizações** pelo winget, como o UniGetUI. |
| **Duplicados** | Acha arquivos idênticos pelo conteúdo (tamanho → começo → SHA-256), mantém a original e manda as cópias para a Lixeira. |
| **Reparar** | SFC, DISM RestoreHealth, refazer cache de ícones, reiniciar o Explorador, limpar cache de DNS, redefinir a rede, trocar o DNS (Cloudflare, Google, Quad9, AdGuard) e plano de energia (inclusive criar o Desempenho Máximo). |
| **Espaço em disco** | Mapa em blocos aninhados (treemap *squarified*) que aparece na hora e cresce durante a varredura. Duplo clique amplia, voltar/avançar (também nos botões do mouse), filtro, legenda por tipo, maiores arquivos, excluir para a Lixeira. |
| **Pendrive** | Formatar (FAT32/exFAT/NTFS, MBR/GPT, cluster, rótulo) e criar pendrive de boot: ISO do Windows (copia e divide o `install.wim`, opções de TPM/conta local/BitLocker) ou imagem Linux/IMG gravada byte a byte com verificação. |

O que o Vapt **não** faz, de propósito: limpar registro, apagar Prefetch, "liberar RAM", desligar o Defender, remover componentes do sistema. A pesquisa por trás disso está em [reports/Otimizadores de PC para o Vapt.md](reports/Otimizadores%20de%20PC%20para%20o%20Vapt.md).

## Detalhes técnicos

- **Varredura do disco** (`Core/Disk/DiskScanner.cs`): `FindFirstFileExW` com `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`, uma thread por núcleo, cada pasta publicada de uma vez (a interface lê enquanto a varredura escreve). Não segue junções/links; arquivos só na nuvem contam 0. ~500 mil arquivos em ~9 s sem administrador.
- **Mapa** (`Controls/TreemapView.cs`): Win2D, desenhado num bitmap em cache; passar o mouse só redesenha o contorno. Zoom animado a partir do bloco clicado.
- **Filtro** (estilo SpaceSniffer): `*.mp4`, `>1gb`, `<30dias`, `>2anos`, `|*.tmp` (exclui), termos separados por `;`.
- **FAT32 acima de 32 GB** (`Core/Usb/Fat32Formatter.cs`): formatador próprio pela especificação da Microsoft (fatgen103), escrito no volume travado (`FSCTL_LOCK_VOLUME`). Código próprio, sem trechos do Rufus (GPL).
- **Pendrive seguro**: só lista discos USB/SD/MMC que não são de boot nem de sistema; antes de apagar, relê a lista e confere `UniqueId` + tamanho; confirmação com caixa de seleção. Gravação Raw com o disco **offline** para o Windows não montar nada no meio.
- **Administrador**: o Vapt abre como usuário comum (sem UAC). Páginas que precisam mostram **Reiniciar como administrador**, que reabre na mesma página.

## Instalador

```powershell
.\scripts\pack.ps1          # testes + build Release autocontido + verificação + instalador
```

Sai em `artifacts\releases`:

- `Vapt.App-win-Setup.exe`: instala para a sua conta em `%LOCALAPPDATA%\Vapt.App`, sem pedir administrador, com atalho na área de trabalho e no menu Iniciar.
- `Vapt.App-win.msi`: instala para o computador todo (Arquivos de Programas).

O app vai **autocontido** (.NET 10 e Windows App Runtime juntos), então funciona em qualquer Windows 11 sem baixar nada. Desinstalar pelo Windows remove o app, mas **mantém** `%LOCALAPPDATA%\Vapt` (backup das otimizações e dos registros apagados), para dar para desfazer depois. Os arquivos não são assinados: em outro PC o SmartScreen avisa na primeira vez ("Mais informações" → "Executar assim mesmo").

Requer o `vpk` (Velopack): `dotnet tool install -g vpk`.

## Compilar e rodar

Requer o .NET 10 SDK e o Windows App Runtime 2.6 (já vem no Windows 11 atualizado).

```powershell
dotnet build Vapt.slnx
dotnet test tests\Vapt.Core.Tests
.\scripts\run.ps1                          # compila e abre
.\scripts\run.ps1 -- --page disk --scan C:\Users   # abre direto numa página
```

Compile sempre a **solução** (`Vapt.slnx`): compilar só o projeto do app gera em outra pasta (`bin\Debug` em vez de `bin\x64\Debug`).

Argumentos: `--page <home|cleanup|tweaks|startup|apps|disk|usb|settings>`, `--scan <pasta>`, `--smoke` (monta todas as páginas sem janela e sai com o número de falhas).

O ícone é gerado por `scripts/make-icon.ps1` (degradê preto → roxo com um raio branco).

Dados ficam em `%LOCALAPPDATA%\Vapt` (`settings.json`, `backup.json` com os valores originais das otimizações, `logs\`).

## Licença

[GPL-3.0-or-later](LICENSE) © 2026 blat0r. O catálogo de apps do Debloat vem do Win11Debloat (MIT); veja [NOTICE.md](NOTICE.md).
