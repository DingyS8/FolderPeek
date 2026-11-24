# FolderPeek

Utilitário de bandeja para Windows 10/11: **clique uma vez** numa pasta (ou atalho de pasta) da Área de Trabalho,
ou pare o mouse **1,5 s** em cima dela, e aparece um popup escuro, com cantos arredondados, listando o conteúdo —
sem roubar o foco. O gatilho é escolhido na bandeja ("Abrir preview com": clique, hover ou os dois).

- Clique duplo continua abrindo a pasta normalmente (o 2º clique fecha o preview); clicar de novo no mesmo
  ícone fecha; arrastar ícones e Ctrl/Shift+clique não abrem nada.

- Visual: acrílico do Windows 11 (desfoque real do fundo; sólido no Windows 10), cantos e sombra do sistema,
  grade de ícones grandes com miniaturas reais de imagens/vídeos/PDF, cor de destaque do Windows no hover,
  entrada com fade + deslize e caminho clicável no cabeçalho (Raiz › … › atual).
- Passe o mouse num item para ver nome completo, tamanho e data no rodapé.
- Clique em **arquivo** → abre e fecha o preview.
- Clique em **subpasta** → navega dentro do preview (botão ← volta). **Ctrl+clique** abre no Explorador.
- Botão ↗ no cabeçalho → abre a pasta atual no Explorador.
- O preview fecha sozinho (fade) quando o mouse sai do ícone **e** do popup. Há um "corredor" de tolerância
  entre os dois e 300 ms de carência.
- Bandeja: pausar (ou clique duplo no ícone), tempo de hover (0,5–3 s), iniciar com o Windows, logs, sair.

## Build

Requer .NET 10 SDK.

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\win-x64\FolderPeek.exe
```

Exe único autocontido (não precisa de runtime instalado na máquina de destino):

```powershell
dotnet publish -c Release --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Diagnóstico

| Comando | O que faz |
|---|---|
| `FolderPeek.exe --diag [atalhos.lnk...]` | Casa todos os ícones do desktop (UIA × IFolderView) sem mexer o mouse; grava `%LOCALAPPDATA%\FolderPeek\diag.txt` |
| `FolderPeek.exe --preview "C:\pasta"` | Mostra o popup no cursor por 15 s (teste visual) |
| `FolderPeek.exe --thumb "C:\foto.jpg" saida.png [px]` | Gera a miniatura exatamente como o popup e salva em PNG |

Log: `%LOCALAPPDATA%\FolderPeek\folderpeek.log` · Config: `%APPDATA%\FolderPeek\settings.json`
(`HoverDelayMs`, `Paused`, `MaxItems`).

## Como funciona

```
DispatcherTimer 50 ms ──► GetCursorPos + WindowFromPoint
                          └─ é SysListView32 ⊂ SHELLDLL_DefView ⊂ Progman/WorkerW?  (senão: nada)
                          └─ parado (raio ~10 px) por HoverDelayMs, sem botão pressionado?
                                 │
                                 ▼  Task.Run (thread MTA)
                    UI Automation FromPoint ──► ListItem: retângulo + rótulo
                    IShellWindows.FindWindowSW(SWC_DESKTOP) ──► IShellBrowser ──► IFolderView
                          └─ itens + GetItemPosition → casa por posição, confirma por nome
                          └─ PIDL → SHGetNameFromIDList(FILESYSPATH) → caminho completo
                          └─ pasta? ou .lnk (IShellLinkW) apontando para pasta?
                                 │
                                 ▼  thread de UI
                    PreviewWindow (WPF, WS_EX_NOACTIVATE) ← FolderLister (background)
```

Decisões:

- **Polling em vez de hook global de mouse** (`WH_MOUSE_LL`): um hook atrasa o mouse do sistema inteiro se
  a thread travar; o polling custa ~0% de CPU e nunca interfere em outras janelas.
- **Clique via hook `WH_MOUSE_LL` numa thread dedicada** que só repassa o evento (nunca bloqueia nem engole o
  clique) e só existe com o modo de clique ligado — polling perderia cliques rápidos.
- **Sem `AutomationElement.FromPoint`**: overlays transparentes de tela cheia (NVIDIA GeForce Overlay, etc.)
  ganham o hit-test do UIA. O hit-test é feito nos retângulos dos itens do próprio ListView do desktop.
- **UIA + IFolderView juntos**: o UIA sabe *se* o cursor está sobre um ícone (e o retângulo exato), mas não o
  caminho; o IFolderView dá o caminho, mas não faz hit-test. Nada é injetado no `explorer.exe` nem há leitura de
  memória de outro processo (evita alarmes de antivírus).
- **Per-Monitor V2 DPI** no manifest: cursor, retângulos e `SetWindowPos` tudo em pixels físicos.
- O popup respeita as opções do Explorer: itens ocultos, arquivos protegidos do SO, extensões de tipos conhecidos.
- Ícones via `SHGetFileInfo`, com cache por extensão; arquivos-placeholder do OneDrive nunca são lidos (não
  disparam download).

## Limitações conhecidas

- Rolagem com a roda depende de "Rolar janelas inativas ao passar o mouse" (ligado por padrão no Windows 10/11).
- Itens virtuais (Lixeira, Este Computador) e `.zip` não abrem preview.
- Emojis em nomes aparecem monocromáticos (limitação do WPF).
- Sem menu de contexto (botão direito) nos itens do preview.
