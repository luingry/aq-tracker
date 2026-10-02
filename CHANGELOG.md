# Changelog

## [0.29.1] - 2026-10-02

### Fixed

- The open agent list follows the compact widget while resizing, without requiring a window move to refresh its position.

## [0.29.0] - 2026-10-02

### Added

- Dragging the compact widget beyond its maximum size opens the detailed widget, preserving the compact size and consuming the remaining resize gesture.

## [0.28.2] - 2026-10-02

### Fixed

- Dragging the opacity slider no longer moves the application window; interactive controls retain their own mouse gestures.
- Removed the surrounding focus border from the opacity slider while preserving keyboard input.

## [0.28.1] - 2026-10-02

### Fixed

- Switches and the opacity slider thumb show a hand cursor on hover.

## [0.28.0] - 2026-10-02

### Added

- A themed opacity slider appears below the enabled floating-widget fade switch. Choose 10% to 90%, persisted across restarts, for both the compact widget and agent list; hover still restores full opacity. Existing preferences default to 50%.

## [0.27.2] - 2026-10-02

This release consolidates the unpublished changes since 0.25.1.

### Added

- Optional notification-area quota circles show all available quotas for enabled profiles and selected periods, independently of foreground activity. Icons use profile colors, contrasting numbers and descriptive tooltips; clicking restores the widget.
- Optional 50% opacity for the compact floating widget and agent list; hovering either surface restores both to full opacity.
- With Always on top disabled, the visible widget is selectable through Alt+Tab and the taskbar. Enabling it restores tool-window behavior.

### Changed

- Shared buttons, selections, hover states and update progress use grayscale highlights while preserving Codex and Claude colors. Enabled switches use a soft green background in both themes, including on hover.

### Fixed

- Widgets opened from notification icons return to the tray when focus leaves the application; internal popups and dialogs retain the reveal.
- The agent list follows Always on top across toggles, repositioning and reopening.
- Notification-area and floating-widget fade settings use consistent vertical spacing.
- Icon preview tests create their output directory on clean CI checkouts.

## [0.25.1] - 2026-10-02

### Changed

- New settings default to the dark theme with Codex blue (`#0080FF`) and Claude orange (`#D97757`), matching the current user preset. Existing saved theme and color choices remain preserved.

### Fixed

- Detailed widget controls consistently reveal over the entire visible surface, including empty header padding and the configuration button. Quota/layout refreshes and mode changes under a stationary pointer no longer clear hover; hidden controls release hit tests and Settings keeps them suppressed.

## [0.25.0] - 2026-10-02

### Added

- "Iniciar minimizado com o Windows" switch at the top of Settings, with per-user startup registration and a tray-only sign-in launch. The tray Show action restores the widget; new profile engagement resumes its automatic visibility.

### Fixed

- Settings saves now flush and replace files atomically, keep a complete recovery copy and recover invalid or missing primary JSON without resetting preferences. Unrecoverable or inaccessible files are preserved instead of silently loading defaults.
- All executable locations share one per-user instance lock, preventing simultaneous installed/development copies from overwriting preferences or racing Claude refresh-token rotation.
- WPF tests no longer execute the application's production startup. Their window, preferences and Claude credentials remain isolated from real user data.
- Silent upgrades preserve the live Windows startup choice, including when disabled in Settings after a previous installer enabled it.

## [0.24.2] - 2026-10-02

### Fixed

- Detailed view identifies Codex above its quota indicators, using its accent color and matching the Claude heading without a connection status.

## [0.24.1] - 2026-10-01

This release consolidates the unpublished changes since 0.21.0.

### Added

- Independent Claude profile with 5h/7d quota polling, OAuth PKCE sign-in, encrypted per-user DPAPI token storage, live session activity and unread completions.
- "Detalhes do consumo", a collapsed-by-default section below the Codex quota indicators, groups consumption totals and costs, coverage, ranking, the daily chart and per-chat details. A chevron indicates its expanded state.

### Changed

- Renamed the product and repository to **Agent Quota Tracker** (`aq-tracker`), with `AqTracker.exe`, updated projects, installer, documentation and brand assets. Upgrades retain the installer AppId and migrate the legacy installation and user data to the new folders. The release also provides the legacy installer asset name for existing updaters.
- Redesigned Settings into General, Codex, Claude and About sections, with switches, contextual Claude connection actions, borderless cards and eyedropper icons for color pickers. Settings opens at a stable, work-area-limited height and scrolls its content.
- Claude detail quotas now use circular remaining-percentage indicators with reset text on the right, matching Codex. Both profiles show 5h/7d inside the circle and "JANELA 5H"/"SEMANAL" above the reset date and time.
- Removed the "Dados ao vivo via Codex" status footer from the detail screen.

### Fixed

- Agent separators consistently identify Codex or Claude, including a single agent or unread completion. Selected compact quota windows remain visible side by side for both platforms, with widget size adapting to their count.
- Claude desktop agent rows open their specific chat through a validated session deep link, with a foreground fallback for CLI sessions and failed launches.
- Detailed layout callbacks no longer calculate screen coordinates after the window disconnects from its presentation source.

## [0.24.0] - 2026-10-01

### Added

- A collapsed-by-default "Detalhes do consumo" section below Codex detail quotas groups consumption totals and costs, coverage, ranking, daily usage chart and the per-chat details action. Its chevron points down when closed and up when expanded.

## [0.23.3] - 2026-10-01

### Fixed

- Codex and Claude color pickers use an eyedropper icon instead of a select chevron.
- Settings cards and the action footer no longer have solid wrapper borders.
- Claude detail quotas match Codex with circular remaining-percentage indicators and reset text on the right. Both platforms show 5h/7d inside each circle and the localized "JANELA 5H"/"SEMANAL" heading above the reset date and time.
- Detailed layout callbacks skip screen-coordinate calculations after the window disconnects from its presentation source.

## [0.23.2] - 2026-10-01

### Fixed

- Agent list separators always show the platform name (Codex or Claude), including a single active agent or unread completion and transitions from multiple platforms to one. Project ordering and row identity remain intact.

## [0.23.1] - 2026-10-01

### Fixed

- Simultaneous Codex and Claude agents are grouped by platform with the existing name-and-line separator. A single platform retains project grouping, row identity and unread completions.
- Compact quotas retain every selected 5h/7d window for each engaged platform, shown side by side without replacing another platform's weekly quota. Widget width and resize regions adapt to the actual indicator count while preserving the saved circle scale.

## [0.23.0] - 2026-10-01

### Changed

- The project is now **Agent Quota Tracker** (`aq-tracker`, formerly Codex Tracker), reflecting that it tracks both Codex and Claude. Solution, projects, namespaces, executable (`AqTracker.exe`), installer (`AqTracker-Setup-<version>.exe`), brand assets, README and the GitHub repository (`luingry/aq-tracker`) were renamed.
- Settings were redesigned: General, Codex, Claude and About cards with clear section titles and profile color markers; on/off preferences are switches aligned to the right; buttons follow a defined hierarchy (primary, secondary, ghost, danger and field pickers), and color pickers are left-aligned like other fields.
- The Claude section shows a single connection status pill and only the actions valid for that state: connect (or reconnect) when signed out, disconnect when signed in, and only cancel while a sign-in is pending. The duplicated "Connected" text was removed.

### Fixed

- Settings opens directly at its final height instead of starting tall and shrinking a few seconds later when a quota snapshot arrived. The fixed height is 572 DIP (30% above the previous 440 DIP), limited by the monitor work area, and the content scrolls.

### Migration

- Upgrades keep the installer AppId, move the app to `%LOCALAPPDATA%\Programs\Agent Quota Tracker`, close and remove the legacy `Codex Tracker` folder, shortcuts and autostart entry, and the app moves `%APPDATA%\CodexTracker` to `%APPDATA%\AqTracker` on first start (stray files in an `AqTracker` folder without `settings.json`, such as a dev or test run's quota history, do not block it). Releases also publish the setup as `CodexTracker-Setup-<version>.exe` so installs up to 0.22.x still find the update.

## [0.22.1] - 2026-10-01

### Fixed

- Claude agent rows now open the specific desktop chat using a validated host session deep link, preserving the id through activity and unread completion persistence. CLI sessions and failed launches fall back to bringing Claude to the foreground.

## [0.22.0] - 2026-10-01

### Added

- Dynamic Claude profile with independent colors, live interactive session activity, unread completions, desktop focus detection, and a compact quota detail block.
- Independent Claude OAuth sign-in with PKCE, local callback and manual code fallback, cancellation, automatic token refresh, and atomic CurrentUser DPAPI token storage.
- Claude 5h/7d usage polling with stale snapshot recovery and bounded exponential backoff. Engaged Codex and Claude profiles show their most restrictive permitted quotas side by side.
- Localized Claude settings, profile toggle, color selection, and regression coverage for engagement, sessions, OAuth, DPAPI, quota parsing and provider isolation.

### Fixed

- Agent row and unread persistence identities now include the provider, preventing a Claude session from consuming a Codex completion with the same id.

## [0.21.0] - 2026-09-08

### Added

- Added a persisted daily opening 7-day quota series, with a dashed initial line, translucent initial and between-series areas, and daily quota history tooltip details.

## [0.20.1] - 2026-09-08

### Fixed

- Daily quota history now plots and labels weekly quota remaining. Known readings continue across days without a quota snapshot while preserving their actual calendar spacing.

## [0.20.0] - 2026-09-05

### Added

- Added official Codex 5h and 7d quota indicators, compact display selection, and a detected-only 5h detail row while retaining the daily quota chart introduced in 0.19.0.

### Fixed

- Selected quota windows by the official `codex` bucket and window duration instead of assuming the weekly quota was always `primary`; accounts with reversed 5h/weekly slots now show and retain the correct weekly history.

## [0.19.0] - 2026-09-05

### Added

- Overlayed the current-month daily token bars with the weekly quota used at each day close, including a live current-day point, bounded smooth curve, percent axis, and quota-aware hover details.
- Retained official live quota readings locally and extracted historical weekly Codex readings from cached rollout JSONL so daily quota history survives restarts without modifying settings or source rollouts.

## [0.18.13] - 2026-08-21

### Fixed

- Stopped inferring that a completed chat was read from the Codex desktop's global unread-thread index. Returning Codex to the foreground while another chat is selected no longer clears the Tracker row; only explicit Tracker actions or a new execution in the same root chat clear it.

## [0.18.12] - 2026-08-21

### Fixed

- Preserved completed unread agent work when the Codex desktop is backgrounded or minimized; reconciliation from the Codex unread-thread index now removes a completion only while its window is visible, foregrounded, and not minimized.

## [0.18.11] - 2026-08-21

### Fixed

- Removed the unsolicited Tracker-only read-state notice from the agent list.

## [0.18.10] - 2026-08-21

### Fixed

- Reconciled unread completed root-chat work with the Codex desktop's local unread-thread state, so opening a pending chat directly in Codex removes it from the Tracker list.

### Changed

- Clarified that marking all completed work as read is local to Codex Tracker because the exposed Codex protocol has no safe, verifiable mark-all-read operation.

## [0.18.9] - 2026-08-21

### Fixed

- Agent rows now receive definitive Codex chat titles immediately after the app-server resolves them, without waiting for the next activity refresh.

## [0.18.8] - 2026-08-21

### Fixed

- Agent-list project labels now use locally verifiable Git roots, so transient working directories appear as `Sem projeto` while subagents inherit a valid parent project.

## [0.18.7] - 2026-08-21

### Fixed

- Preserved the identity, title, project grouping, and current-month usage of chats whose rollout JSONL is still open for writing.
- Clarified completed-agent affordances with a double-check mark-all action and a completion check beside the completed status while elapsed time remains right-aligned.

## [0.18.6] - 2026-08-21

### Added

- Added an overlay action in the agent list to mark all unread completed principal-agent work as read without opening individual chats.

### Changed

- Details by chat now lists chats by their most recent observed usage update, with deterministic fallbacks instead of token volume as the primary order.

## [0.18.5] - 2026-08-21

### Fixed

- Excluded Codex memory-maintenance rollouts from active-agent and unread completed-work lists while retaining normal sessions and subagents.

## [0.18.4] - 2026-08-20

### Changed

- Added category-share bars and aligned estimated cost-before-token values in the shared usage tooltips; ranking tooltips now open immediately on hover.

## [0.18.3] - 2026-08-20

### Changed

- Unified daily-usage and model-ranking tooltips with a localized structured layout for token categories, estimated costs, and totals.

## [0.18.2] - 2026-08-20

### Fixed

- Kept the chat-search clear control transparent in every interaction state and matched its icon contrast to the search affordance.

## [0.18.1] - 2026-08-20

### Fixed

- Replaced the chat-search magnifier and clear affordance with the tracker’s high-contrast, consistent vector icon treatment.

## [0.18.0] - 2026-08-20

### Added

- Details by chat now identifies projects only from locally verifiable Git roots, consolidates worktrees under their parent repository, and adds search affordances for search and clear.

## [0.17.2] - 2026-08-20

### Changed

- Refined Details by chat project headers to a transparent agent-list-style divider, restored individual chat cards, and removed the redundant total progress bar.

## [0.17.1] - 2026-08-20

### Fixed

- Chat search now filters projects and chats without materializing results until a project is manually expanded; project headers share a single compact list surface.

## [0.17.0] - 2026-08-20

### Changed

- Made Details by chat substantially more compact with collapsed lazy project groups, title/project search, and token-share bars while keeping cost estimates available to the analytics model without rendering them in the window.

## [0.16.0] - 2026-08-20

### Added

- The widget agent list now groups chats by their session project, with a subtle project separator and a literal `Sem projeto` group for sessions without a working directory.

## [0.15.0] - 2026-08-20

### Added

- Added a current-month Details by chat window below daily usage, grouping local chats by sanitized project name and showing mutually exclusive token categories with their honest estimated costs.

## [0.14.2] - 2026-08-20

### Added

- Hovering a ranking row or a daily-usage bar now shows mutually exclusive cache-read, input, output, and reasoning token categories, with the estimated cost beneath each value and a final total.

### Fixed

- Local token totals and estimated costs no longer count reasoning output twice; reasoning remains a separately visible subset of output in the breakdown.

## [0.14.1] - 2026-08-17

### Fixed

- GitHub release publication now distinguishes a missing release from service failures, retries transient GitHub API errors with bounded backoff, and treats already-removed old releases or tags as successful cleanup. If GitHub remains temporarily unavailable only while removing older releases, the valid current release remains published and the next release run retries the cleanup.

## [0.14.0] - 2026-08-17

### Added

- Settings now include a "Check for updates" action that queries the public GitHub release, and automatic checks run at most once per day. When a newer version is found, the update dialog only appears while the detailed view is open (or immediately if a manual check finds one), never interrupting the compact widget. Its "Update" button downloads the installer transparently with in-dialog progress, runs it silently, and relaunches the newly installed version; "Later" dismisses it until the next day's check.

## [0.13.3] - 2026-08-17

### Fixed

- Restarting work in a previously completed root chat now reuses its existing tracker row, clears the stale unread completion, and updates the active status and elapsed time from the new execution instead of showing a duplicate entry.

## [0.13.2] - 2026-08-16

### Fixed

- The idle tracker now hides after the Codex window is closed to the notification area: hidden or DWM-cloaked Codex HWNDs no longer count as foreground. Active work, unread completions, a visible foreground Codex window, direct tracker interaction, and the open agent popup retain their existing visibility behavior.

## [0.13.0] - 2026-08-14

### Added

- The agent list now shows active agents and unread completed principal-agent work together, keeping active rows first and preserving stable row identity across refreshes.

### Changed

- The active-agent count keeps priority while any work is running; the green completion check replaces it only after all active work finishes.

## [0.12.1] - 2026-08-14

### Fixed

- Codex foreground detection now recognizes the desktop app's real packaged `ChatGPT.exe` host, so reading the last completed agent never hides the widget while Codex remains focused.
- The completed-row check is now an overlay above elapsed time and no longer pushes the time below the reasoning line or changes the agent card structure.

## [0.12.0] - 2026-08-14

### Added

- The widget now follows Codex focus, hiding while Codex is minimized or in the background whenever no agent work is running and no unread completion remains.
- Completed principal-agent work is persisted as unread, forces the widget visible, and replaces the active-agent count with a green check on a light-green surface.
- Clicking the completion indicator lists unread principal-agent work; opening a completed chat marks that thread read, while subagent completions stay out of the list.
- Completed list rows show a green check above their elapsed time, while active work always takes precedence over every other visibility and indicator state.
- The reasoning glow now takes two seconds to cross an active agent row, followed by a reliable two-second pause without restarting on each activity refresh.

## [0.11.13] - 2026-08-14

### Changed

- All displayed numerical data now uses the same Source Sans 3 family as the weekly percentage, including reset timing, forecasts, costs, ranking, settings exchange rate, version, and chart labels/tooltips.

## [0.11.12] - 2026-08-14

### Changed

- The quota reset countdown now shows the local absolute reset date and time alongside the remaining duration, with localized Portuguese and English formatting.

## [0.11.11] - 2026-08-14

### Changed

- The compact active-agent activity effect is now a crisp, counterclockwise 10-percent white spinner arc on the indicator edge, at 50-percent opacity with fixed 1-DIP stroke and no blur.

## [0.11.10] - 2026-08-14

### Fixed

- The compact active-agent glow is now anchored to the indicator's inner edge: a transparent-centered radial band expands inward and brightens without scaling a central disk or rotating.

## [0.11.9] - 2026-08-14

### Fixed

- Local analytics now optionally reads the local Codex SQLite thread-model index in read-only mode to attribute snapshots that precede rollout model metadata. JSONL model changes remain temporal overrides; SQLite WAL updates invalidate the index, while transient database failures retain the last valid mapping.
- The compact active-agent indicator now uses a clipped internal circular glow that softly pulses in scale and opacity instead of rotating; it remains disabled when reduced motion is enabled.

## [0.11.8] - 2026-08-14

### Fixed

- Local usage analytics now reads `thread_settings_applied` model changes from rollout events, attributing only subsequent token deltas to the selected model while retaining earlier or model-provider-only snapshots as unknown. The ranking presents the remaining internal `unknown` bucket as a localized unregistered-model label without changing its tokens, cost state, or underlying key.

## [0.11.7] - 2026-08-14

### Fixed

- A lista de agents agora reconhece `turn_context` tanto no campo raiz quanto em `payload.type`, incluindo contextos anexados depois do cache inicial, para substituir metadados `unknown` pelo modelo e effort registrados. Rollouts ativos com mtime estagnado continuam visíveis por data local e crescimento incremental, enquanto entradas inativas do cache expiram.

## [0.11.6] - 2026-08-14

### Added

- Usage ranking rows now show each priced model's estimated API-equivalent cost below its tokens for the selected day, active quota week, or month. Unpriced models continue to show the localized no-tariff label.

## [0.11.5] - 2026-08-14

### Changed

- Rewrote the README in English with current product capabilities, privacy boundaries, supported languages, development steps, and stable detailed/agents screenshots.

## [0.11.4] - 2026-08-14

### Fixed

- Hover e ripple das linhas de agents agora são recortados por uma geometria arredondada dinâmica no contorno interno da lista; a sombra continua em uma camada externa sem clip, preservando a elevação sem escapes nos cantos.

## [0.11.3] - 2026-08-14

### Fixed

- O wrapper da lista de agents não reserva mais padding vertical externo nem gaps entre itens; os mesmos 8 DIP superior e inferior agora pertencem à superfície interativa de cada linha, para que hover e ripple cubram todo o respiro.
- O spinner do contador de agents foi reduzido para aproximadamente um quarto do arco anterior, com blur ampliado e opacidade visual de 42%, preservando o giro anti-horário e a duração.

## [0.11.2] - 2026-08-14

### Fixed

- O histórico local passa a ser carregado em segundo plano também no widget compacto e a cada ciclo periódico de cinco minutos; ao abrir o modo detalhado, o último resultado já carregado é aplicado imediatamente contra a quota atual, sem esperar uma segunda leitura.
- O contador compacto de agents agora recebe um arco branco com blur luminoso de 60% que gira no sentido invertido somente enquanto há trabalho e o Windows permite animações; com movimento reduzido, ele permanece oculto e a animação é interrompida com segurança.

## [0.11.1] - 2026-08-14

### Fixed

- O ComboBox agora propaga o padding configurado para seu ToggleButton interno, garantindo respiro à esquerda do texto selecionado.
- O espaço entre o marcador e o texto dos checkboxes passou a ser aplicado diretamente no conteúdo, mantendo 13 DIP estáveis sem depender do layout interno de `BulletDecorator`.
- O modo detalhado agora adota a altura total permitida depois que os dados são aplicados, limitada pelo conteúdo e pela área de trabalho atual, reposicionando-se apenas quando necessário para continuar visível.
- As superfícies compactas do gauge, contador e lista de agents receberam elevação curta e sutil, sem bordas decorativas ou recorte de sombra.
- Modelo e effort agora ficam imediatamente ao lado do tipo do agent, com espaço curto consistente e truncamento seguro.

## [0.11.0] - 2026-08-14

### Added

- Configurações agora oferecem um seletor nativo de cor de destaque. Uma única cor-base persistida gera automaticamente variantes acessíveis para destaque, superfícies suaves, hover e glow nos temas claro e escuro.
- A interface agora pode ser alternada entre `pt-BR` e `en-US` nas configurações, incluindo textos, tray, estados, tooltips, previsões, contagem regressiva e formatação numérica.
- O painel de configurações recebeu polimento de ritmo, contraste e alinhamento dos campos; o caminho manual do Codex agora só aparece como fallback após falhar a auto-detecção, enquanto o acesso ao log permanece disponível.

### Fixed

- O parse frio do histórico local agora processa arquivos JSONL independentes em paralelo limitado e lê cada arquivo somente até o tamanho capturado no planejamento, mantendo cache, deduplicação e resultados determinísticos; numa cópia estável do histórico local de 394 arquivos/532,8 MB, a mediana caiu de 5.002 ms para 3.007 ms (aprox. 40%).
- A abertura detalhada mantém quota oficial e analytics locais em paralelo, mas agora preserva o resultado que terminar primeiro: se analytics terminar antes do snapshot, ele é aplicado uma única vez quando a quota chega, sem releitura nem painel incompleto.
- A lista de agents permanece totalmente fechada no modo detalhado, inclusive quando um novo agent começa a trabalhar depois da troca de modo.
- Hover e ripple das linhas de agents/subagents agora ocupam toda a largura do wrapper, com respiro vertical ao redor do texto sem perder a indentação hierárquica.
- O glow de trabalho não aparece mais nas barras do ranking; ele permanece exclusivo do gauge semanal de quota.
- O seletor de período Dia/Semana/Mês do ranking agora sinaliza interação com cursor de mão no hover.
- Modelo e effort na lista de agents agora usam uma variante menos saturada, mas com contraste garantido, da cor de destaque escolhida.
- Labels do painel de configurações agora compartilham tipografia e cor semântica; checkboxes têm respiro maior e ComboBox preserva texto contrastante nos temas claro e escuro.
- O botão visível de Pin foi removido do chrome; a preferência Sempre no topo continua disponível e persistida exclusivamente nas configurações.
- Modelo e effort passaram para a mesma linha do tipo na lista de agents; o tempo de execução fica alinhado à direita na linha de status, com truncamento para títulos e metadados longos.
- O tema escuro agora usa `#2D2D2D` como superfície-base consistente; Settings ajusta automaticamente a altura ao conteúdo dentro da área de trabalho, bloqueia resize manual, amplia o respiro dos checkboxes e deixa ComboBox mais confortável e claramente interativo.

## [0.10.6] - 2026-08-14

### Fixed

- O chevron do indicador de agents volta a aparecer em todo hover, independente de a lista estar aberta ou fechada.
- Linhas de agents/subagents agora clareiam suavemente no hover e exibem um ripple de glow a partir do ponto exato do clique antes de abrir a conversa.
- O glow do reasoning passou a usar uma faixa física constante, com percurso de 1,30 s e espera de 2 s entre ciclos, preservando a velocidade visual em textos curtos e longos.
- A lista de agents fecha somente de forma visual ao entrar no modo detalhado e reabre no compacto quando a preferência persistida e agents ativos permitem.

## [0.10.5] - 2026-08-14

### Fixed

- O indicador de agents mantém o número visível fora do hover, mesmo com a lista aberta. No hover, ele troca para um chevron compacto, centralizado e proporcional: para baixo fechado e para cima aberto.
- Cada linha de agent/subagent agora abre a conversa correspondente no Codex por deep link validado, sem fechar a lista.

## [0.10.4] - 2026-08-14

### Fixed

- A lista de agents agora guarda sua preferência de expansão, não fecha ao clicar fora do widget e restaura-se quando novos agents surgem. O hover fechado mostra novamente a seta para baixo.

### Added

- A abertura automática da lista usa uma entrada curta ancorada ao indicador; com a lista já aberta, apenas o agent novo recebe uma entrada discreta. As animações respeitam a redução de movimento do Windows.

## [0.10.3] - 2026-08-14

### Fixed

- O glow do reasoning agora percorre o texto da esquerda para a direita; a lista de agents ficou sem contorno externo e o indicador compacto usa fundo escuro com texto e seta brancos.

## [0.10.2] - 2026-08-14

### Changed

- A lista de agents ganhou tipografia e espacamento mais legiveis, hierarquia pai-filho com recuo e glow cinza discreto no reasoning ativo, desativado quando o Windows reduz animacoes.

## [0.10.1] - 2026-08-14

### Fixed

- O indicador compacto agora centraliza a seta, mostra seta para cima enquanto a lista esta aberta e a fecha ao clicar; durante o arraste, a lista aberta acompanha o widget.
- O status dos agents em execucao agora mostra o raciocinio/atividade corrente do agente e nao e substituido por mensagens de commentary.

## [0.10.0] - 2026-08-14

### Added

- A visualizacao compacta agora mostra quantos agents e subagents estao trabalhando e abre uma lista com tipo, titulo, ultimo status, modelo, effort e tempo em execucao.
- O progresso verde recebe um glow reverso de um segundo, com dois segundos de intervalo, enquanto houver trabalho ativo do Codex.

## [0.9.1] - 2026-08-13

### Fixed

- A restauracao da posicao do widget agora reconhece todas as areas de trabalho dos monitores: posicoes em telas secundarias ou com coordenadas negativas sao preservadas, e uma posicao realmente fora das telas disponiveis volta de forma acessivel ao monitor mais proximo.

## [0.9.0] - 2026-08-13

### Added

- A posicao final do widget agora e salva ao terminar cada arraste e permanece em `%APPDATA%\\CodexTracker\\settings.json` entre atualizacoes e reinstalacoes.

### Changed

- A fonte do widget minimalista foi aumentada em mais 15%, para 15,18 DIP, preservando a escala proporcional durante o redimensionamento.

## [0.8.2] - 2026-08-13

### Changed

- A fonte do widget minimalista foi aumentada em 20%, preservando a escala proporcional durante o redimensionamento.

## [0.8.1] - 2026-08-13

### Fixed

- O risco de esgotamento agora aparece logo abaixo do contador de reset e recebe destaque amarelo em negrito somente quando a quota pode acabar antes do reset.
- O contador de reset nao exibe mais o texto redundante "Restante esta semana".
- A altura maxima da visualizacao detalhada acompanha o conteudo para evitar espaco vazio abaixo da versao.

## [0.8.0] - 2026-08-13

### Changed

- A distribuicao agora usa .NET Framework 4.8 fornecido pelo Windows 10 22H2 e Windows 11 suportados, em vez de embutir o runtime .NET 8; o instalador fica drasticamente menor sem download externo durante a instalacao.
- O instalador passa a exigir Windows 10 22H2 ou posterior, que inclui o .NET Framework 4.8.

## [0.7.0] - 2026-08-13

### Added

- O widget agora permanece exclusivamente na bandeja do sistema: nao aparece na barra de tarefas nem no seletor Alt+Tab, e o menu "Mostrar" continua a exibi-lo e ativa-lo.

## [0.6.4] - 2026-08-13

### Fixed

- A entrega local obrigat\u00f3ria agora tamb\u00e9m inicia o aplicativo instalado e verifica o caminho e a vers\u00e3o do processo em execu\u00e7\u00e3o.

## [0.6.3] - 2026-08-13

### Fixed

- O ranking semanal agora considera apenas o ciclo ativo de quota principal do Codex, sem fallback para a semana de calendario quando os limites nao estao disponiveis.

## [0.6.2] - 2026-08-13

### Fixed

- As regras de entrega local agora exigem declarar a versão-alvo, compilar, gerar e instalar o instalador e verificar a versão instalada.
- O texto de versão na visualização detalhada foi duplicado para leitura sem esforço.

## [0.6.1] - 2026-08-13

### Fixed

- O compacto agora limita a largura a 100 DIP, escala proporcionalmente o texto central e desativa a composicao nao-cliente do DWM para nao exibir sombra.
- As superficies detalhada e de configuracoes usam fundos opacos tematicos, preservando os cantos arredondados.

Todas as mudanças relevantes deste projeto são registradas neste arquivo, seguindo [Semantic Versioning](https://semver.org/lang/pt-BR/).

## [0.6.0] - 2026-08-13

### Added

- GitHub Actions release automation builds the Windows installer, attaches it to the release, and retains only the newest release and tag.

## [0.5.0] - 2026-08-13

## [0.5.1] - 2026-08-13

### Fixed

- O widget compacto agora deixa transparente toda a area fora do fundo circular, sem recorte nativo que corte o antialias do indicador.

### Added

- Versão da aplicação exibida de forma discreta, centralizada no rodapé da visualização detalhada.
- Fonte única de versão em `VERSION`, usada pela build e pelo instalador.
- Regra de versionamento semântico para alterações futuras.
