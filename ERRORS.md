# Erros e solucoes conhecidas

## Percentual do Claude congelado sem nenhum erro no log (0.30.2)

- **Sintoma (2026-10-08):** o widget mostrava o percentual do Claude parado (5h = 55% as 19:47) enquanto a API ja retornava 84%; agentes Claude continuavam aparecendo. Estado `Connected`, token valido (expira 00:09) e nenhuma linha `Claude ...` no log durante mais de uma hora, embora o timer chamasse `RefreshClaudeAsync` a cada minuto.
- **Causa raiz:** `api.anthropic.com` resolve primeiro para IPv6 (`2607:6bc0::10`), que nesta rede nao conecta. O .NET Framework tenta os enderecos em sequencia e so cai para IPv4 depois do timeout TCP de ~21 s; o `HttpClient.Timeout` de 20 s abortava toda conexao nova logo antes do fallback. Enquanto havia conexao no pool funcionava; quando ela caiu, toda consulta passou a dar timeout para sempre. Reproduzido: `HttpClient` no PowerShell 5.1 = 200 em 21,4 s; TCP para IPv6 falha em 21 s, IPv4 conecta em 17 ms.
- **Causa do silencio:** `ClaudeUsageClient.RefreshAsync` tinha caminhos de falha sem log: timeout e `429`/`5xx` (backoff sem log), outro status nao-2xx (stale sem log e sem backoff) e gate ocupado. `IsStale` so mudava quando uma falha era reportada.
- **Solucao:** timeout por requisicao de 60 s (`RequestTimeout`) e log quando uma requisicao leva >= 10 s; toda falha e logada com motivo (`usage HTTP <status>`, `network ...`, `timeout`), repeticao limitada a cada 30 min e linha de recuperacao; qualquer nao-2xx faz backoff; `429` respeita `Retry-After` (ate 1 h); prazo total por refresh de 3x60+15 s (gate nunca fica preso; refresh preso alem de 2x o prazo e logado); 2 falhas de rede seguidas recriam o `HttpClient`; `IsStale` considera idade > 5 min. Com a 0.30.2 inicial (timeout ainda 20 s) o log mostrou `Claude usage refresh failed (timeout)`, que levou a causa raiz.
- **0.30.3 (IPv4 primeiro):** `IPv4FirstConnections` define `ServicePoint.BindIPEndPointDelegate` (antes de cada requisicao, pois service points ociosos sao recriados sem o delegate); lancar `SocketException` para endpoint IPv6 faz o .NET Framework pular direto para o IPv4 (conexao nova em ~0,2 s em vez de 22 s). Falha de rede/timeout alterna a preferencia, para redes so-IPv6 continuarem funcionando.
- **Diagnostico:** leia o log e os dados reais via `\\localhost\C$\Users\<user>\AppData\...` — o caminho UNC contorna a virtualizacao de AppData do shell do Claude desktop (o caminho normal mostra copias paradas em 05/10). Para checar a API sem tocar no app: decifrar `claude-tokens.dat` com DPAPI (CurrentUser) e fazer um GET em `/api/oauth/usage` sem imprimir o token.
- **Prevencao:** nenhum caminho de falha de rede pode retornar sem log; dado com idade precisa virar "desatualizado" por idade, nao so por erro reportado. Em .NET Framework, nunca use timeout de requisicao HTTP <= ~25 s para hosts com AAAA: IPv6 quebrado consome 21 s antes do fallback IPv4. Teste rapido: `TcpClient.ConnectAsync` para cada endereco de `Dns.GetHostAddresses`.

## Pop-up "taskkill.exe - Erro de aplicativo (0xc0000142)" ao desligar/reiniciar o Windows

- **Sintoma:** as vezes, ao desligar ou reiniciar, aparecia o pop-up `taskkill.exe - O aplicativo nao pode ser inicializado corretamente (0xc0000142)`. No log System: evento 26 (Application Popup) no mesmo segundo do evento 1074 de desligamento.
- **Causa:** no fim da sessao o Windows fecha a janela, `OnClosing` chama `CodexAppServerClient.DisposeAsync` e `TerminateProcessTree` iniciava `taskkill.exe /T /F` para encerrar o `codex.exe app-server`. Processo novo criado com a sessao terminando falha na inicializacao (0xc0000142). Era intermitente porque so acontecia quando o `codex.exe` ainda nao tinha sido encerrado pelo proprio desligamento (`HasExited` falso).
- **Solucao (0.29.8):** `ChildProcessJob` (Core) coloca o app-server em um Job Object com `KILL_ON_JOB_CLOSE` (+ `BREAKAWAY_OK` para nao quebrar `CREATE_BREAKAWAY_FROM_JOB` do filho). Ao fechar, `TerminateJobObject` encerra a arvore inteira sem criar processo; se o Tracker morrer/crashar o kernel faz o mesmo. `taskkill` ficou so como fallback quando o job nao pode ser criado/atribuido (logado).
- **Prevencao:** nunca iniciar processos (taskkill, cmd, powershell) em caminhos de encerramento/`OnClosing`/`SessionEnding`; use Job Object ou APIs diretas (`TerminateJobObject`, `Process.Kill`).

## Indicador de 5h preso no ultimo percentual com "reiniciando agora"

- **Sintoma:** o gauge de 5h ficava parado num valor antigo (ex.: 98%) e o tooltip/detalhe dizia "reiniciando agora" indefinidamente.
- **Causa:** o percentual so muda quando chega um snapshot novo. Se o provedor nao entrega o novo ciclo (Claude em backoff/offline/token indisponivel, ou servidor ainda devolvendo a janela antiga), a janela mantinha `ResetsAt` no passado e `ResetCountdown` so mostrava "reiniciando agora"; nada tratava a janela como vencida.
- **Atualizacao (0.29.7):** mostrar 100% para janela vencida era um valor inventado (o usuario ja tinha usado 6%). A janela vencida agora vira desconhecida (`HasUsage=false`, "--", "reiniciado, aguardando dados"). A causa raiz do dado parado no Claude era `claude-tokens.dat` existente mas ilegivel (`CryptographicException` a cada minuto no log desde o boot): o cliente ficava `Disconnected` sem avisar. Agora, 3 leituras seguidas falhando com o arquivo presente levam a `Reconnect` (arquivo preservado e relido; volta a `Connected` se ler) e o log traz o HRESULT. O tooltip mostra a hora do dado quando desatualizado.
- **Diagnostico:** o log real do app so aparece fora do pacote do Claude desktop; processos lancados do shell do Claude herdam a virtualizacao de AppData e leem/gravam copias antigas de `claude-quota.json`, `settings.json` e do log, mostrando dados falsos. Nunca lance o app instalado a partir do shell do Claude para validar dados; abra pelo menu Iniciar.
- **Solucao (0.29.6, substituida pela atualizacao acima):** `QuotaWindowExpiry` (Core) normaliza janelas cujo reset passou ha mais de 2 min (`Grace`) para 0% usado, sem proximo reset e `Expired=true` (texto "reiniciado"). O ViewModel guarda os snapshots crus e apresenta sempre a versao normalizada; historico e forecast continuam usando o dado cru. `ReevaluateQuotaExpiry` roda a cada tick de 60 s para refletir o vencimento entre snapshots. Um snapshot novo substitui a apresentacao normalmente.
- **Prevencao:** todo dado com validade (`ResetsAt`) precisa de um estado "vencido" na apresentacao, nao so no calculo. Fixtures de teste com datas absolutas passam a ficar "vencidas" com o tempo: ancore resets ao relogio real quando o teste nao injeta relogio (o smoke WPF usa o relogio do sistema). Observacao de diagnostico: o PowerShell do Claude desktop enxerga `%APPDATA%`/`%LOCALAPPDATA%` virtualizados (`Packages\\Claude_*\\LocalCache`) e pode mostrar log/dados antigos; nao conclua que o app parou de gravar so por isso.

## Pastas do usuario vazias no login do Windows (Claude desconectado, preferencias padrao, sem log)

- **Sintoma:** iniciando pelo Run (`--startup`) no boot, o Claude aparecia desconectado e o widget seguia preferencias diferentes; a instancia rodava por dezenas de minutos sem escrever nenhuma linha no `aq-tracker.log` (nem `Application startup`) e nenhum arquivo de dados era alterado. Iniciar manualmente o mesmo comando funcionava.
- **Causa:** `Environment.GetFolderPath(SpecialFolder.X)` no .NET Framework verifica a pasta e retorna `""` quando o shell ainda nao disponibilizou as pastas do perfil no inicio da sessao. Todos os caminhos (`settings.json`, `claude-tokens.dat`, log) viravam relativos ao diretorio de trabalho (System32): nao encontrados/sem permissao, falhas engolidas silenciosamente.
- **Solucao:** `UserFolders` (Core) resolve com `SpecialFolderOption.DoNotVerify`, cai para variaveis de ambiente (`APPDATA`/`LOCALAPPDATA`/`USERPROFILE`/`HOMEDRIVE+HOMEPATH`) e nunca devolve caminho relativo. Todo o codigo usa `UserFolders` em vez de `GetFolderPath`.
- **Prevencao:** nunca chamar `Environment.GetFolderPath` diretamente; ao diagnosticar boot, ausencia total de log da instancia e sinal de caminho errado, nao de travamento. A entrada "Indisponibilidade transitoria no startup" abaixo tratava sintomas secundarios do mesmo cenario.

## Widget iniciado oculto nunca reaparecia sozinho

- **Sintoma:** apos o boot o widget so voltava a aparecer/ocultar dinamicamente depois de abrir pelo tray.
- **Causa:** o startup oculto exigia ciclo ocioso -> engajado; com Codex/Claude iniciando com trabalho ativo ou nao lido, o estado nunca ficava ocioso.
- **Solucao:** `StartupVisibilityGate` libera a visibilidade normal na primeira mudanca de atividade em relacao ao snapshot inicial.

## Janela fantasma (so sombra) no startup oculto e widget sem reaparecer

- **Sintoma:** apos o login no Windows ficava um retangulo invisivel com sombra, do tamanho do widget detalhado, sem interacao; sumia ao abrir pelo tray. O widget nao reaparecia sozinho ate ser aberto pela area de notificacao.
- **Causa:** o startup oculto fazia `Show()` com `Opacity = 0` e so escondia no `Loaded`. A janela e layered (`AllowsTransparency`), mas o `Backdrop` habilita renderizacao DWM/cantos, entao o DWM desenha sombra mesmo com conteudo transparente. Quando a janela ficava visivel com opacidade 0, `IsVisible` era true e a visibilidade automatica nunca chamava `Show()`/ajustava opacidade.
- **Solucao:** `MainWindow.Start()` usa `WindowInteropHelper.EnsureHandle()` quando inicia oculta (nunca mostra) e roda o runtime em `StartRuntime()` idempotente; a opacidade e calculada antes de cada `Show()` automatico.
- **Regressao (0.29.3-0.29.4):** `UpdateWidgetVisibility` saia com `!IsLoaded`; janela criada so com `EnsureHandle` nunca dispara `Loaded`, entao o widget ficava apenas no tray. Corrigido usando `_runtimeStarted`.
- **Prevencao:** nunca esconder janela "mostrando transparente"; criar so o HWND. Com `EnsureHandle`, nao usar `IsLoaded` como sinal de "app inicializado". O teste de Topmost da suite WPF e ocasionalmente instavel (corrida de foco) — reexecutar antes de investigar.

## Indisponibilidade transitoria no startup deixava Claude desconectado ate reiniciar

- **Sintoma:** credenciais validas e preferencias continuavam no disco, mas o usuario precisava reconectar o Claude; reiniciar o processo recuperou o estado esperado em 2026-10-03. O registro Run apontava para a instalacao correta. O gatilho exato do boot relatado nao ficou registrado e nao foi atribuido a renomeacao.
- **Causa reproduzida:** `ClaudeTokenStore.Load` retorna null em falhas temporarias de leitura/DPAPI e `RefreshAsync` retornava para sempre sem reler o store. Separadamente, uma falha temporaria ao ler preferencias encerrava o startup sem novas tentativas.
- **Solucao:** reler credenciais durante polling quando desconectado, respeitando intervalo e sem repetir credenciais rejeitadas; aguardar ate nove segundos pela leitura das preferencias antes de criar a janela e informar falha persistente preservando os arquivos. Registrar versao, modo de startup, caminho de preferencias e estado Claude sem segredos.
- **Prevencao:** regressao segura o arquivo DPAPI com FileShare.None durante a construcao e exige recuperacao apos libera-lo; preferencias cobrem liberacao do lock e limite de tentativas. Nao confundir teste de `--startup` com prova de reboot fisico.

## Fixture WPF ainda alterava o historico real de cotas

- **Sintoma:** hash de quota-history.json mudava ao executar testes, apesar do isolamento de preferencias e tokens.
- **Causa:** MainWindow recebia SettingsStore isolado, mas criava QuotaSnapshotStore com o caminho padrao do usuario.
- **Solucao:** derivar o caminho de historico do diretorio do SettingsStore injetado.
- **Prevencao:** exigir historico dentro da fixture e comparar hashes de todos os dados reais antes/depois da suite com a instancia de producao encerrada.

## Callback do layout detalhado executava sem PresentationSource

- **Sintoma:** a suíte WPF encerrava com `InvalidOperationException: Este Visual não está conectado a um PresentationSource`, em `GetResizeWorkArea`, durante a atualização de layout de uma janela desconectada.
- **Causa:** `LayoutUpdated` pode disparar após o fechamento/desconexão da janela; o callback detalhado chamava `PointToScreen` sem verificar se ainda existia uma fonte de apresentação.
- **Solução:** o callback só calcula a área de trabalho quando a janela está no modo detalhado e `PresentationSource.FromVisual(this)` existe.
- **Prevenção:** callbacks de layout que dependem de coordenadas de tela precisam validar a conexão do visual; exercitar a abertura, atualização e fechamento de janelas reais no smoke WPF.

## Plataformas simultâneas misturavam agentes e ocultavam janelas de cota

- **Sintoma:** agentes Codex e Claude apareciam sob o mesmo separador de projeto; ao ativar ambos, a cota semanal do Claude desaparecia apesar da seleção 5h + 7d.
- **Causa:** o agrupamento usava somente o caminho do projeto. A política de cotas substituía as janelas selecionadas por uma única janela mais restritiva por provedor, e o XAML e a geometria comportavam somente dois círculos.
- **Solução:** agrupar primeiro por plataforma e usar sempre seu nome no separador existente, inclusive com um único agente ou conclusão não lida. A primeira correção mantinha o nome do projeto quando havia uma só plataforma, mas o marcador deve identificar a plataforma em todos os casos. Materializar todos os indicadores selecionados por plataforma num ItemsControl horizontal; dimensionar cada círculo, o widget e as regiões de resize pela quantidade real, preservando a escala salva.
- **Prevenção:** testar um único agente Codex, um único Claude, uma conclusão isolada e trabalho entre plataformas com projetos iguais e diferentes, além das transições para uma plataforma e cenários com três e quatro cotas. Validar os retângulos WPF em escala mínima, intermediária e máxima, incluindo espaçamento e limites do widget.

## Clique em sessão Claude abria somente a janela geral

- **Sintoma:** clicar numa linha Claude trazia o aplicativo para frente sem selecionar o chat correspondente.
- **Causa:** o leitor ignorava `hostSessionId` da inscrição local; a atividade, a conclusão persistida e a linha não transportavam a identidade desktop necessária à rota de continuação.
- **Solução:** preservar o id validado em toda a cadeia e abrir `claude://code/continue?session=<id>&source=url_external` pelo shell. Ausência, valor inválido ou falha de abertura mantêm o fallback de foco da janela e a marcação explícita como lido.
- **Prevenção:** validar a string original sem `Trim()`, permitir somente `local_[A-Za-z0-9-]{1,64}` com correspondência integral e manter testes de injeção, limites de tamanho, conclusão e JSON persistido antigo sem o campo.

## Callback OAuth indisponível em ambiente com HttpListener não suportado

- **Sintoma:** um smoke adicional do callback real retornou `PlatformNotSupportedException` no construtor de `HttpListener`; a consulta do framework confirmou `HttpListener.IsSupported=false` neste ambiente, apesar do serviço HTTP do Windows estar em execução.
- **Causa:** o runtime não disponibilizava a API do listener no ambiente de execução restrito. O serviço HTTP em execução não prova que a API esteja utilizável pelo processo.
- **Solução:** Conectar Claude oferece automaticamente o fluxo manual quando `IsSupported` é falso ou o listener não pode iniciar. A validação de método, rota, código e state é pura e testada independentemente; o smoke de rede local é condicionado ao suporte da plataforma.
- **Prevenção:** não exija listener local para autorizar a integração. Preserve PKCE e state também no fallback; informe a limitação de ambiente quando o callback físico não puder ser exercitado.

## Rotação atômica de tokens DPAPI falhava ao copiar metadados do arquivo

- **Sintoma:** a primeira gravação cifrada funcionava, mas o teste de substituição dos tokens retornava `UnauthorizedAccessException` em `File.Replace` no diretório temporário do ambiente restrito.
- **Causa:** a substituição padrão também tenta copiar metadados do destino; essa etapa falhava no ambiente, embora a escrita dos bytes e o round-trip DPAPI estivessem permitidos.
- **Solução:** a gravação atômica usa `File.Replace(..., ignoreMetadataErrors: true)`, conserva o destino até a substituição e remove arquivos temporários em `finally`. O teste real confirma a rotação, a decifragem e a ausência de temporários.
- **Prevenção:** teste duas gravações consecutivas, não apenas o primeiro save. Nunca contorne a falha removendo o destino antes de gravar, pois isso elimina a atomicidade e pode perder o refresh token.

## Identidade de agents precisa incluir o provedor ao integrar Claude

- **Sintoma:** durante a integração, a reconciliação antiga por `ThreadId` permitiria que uma sessão Claude reativada removesse uma conclusão Codex com o mesmo id; títulos vindos do app-server também poderiam ser aplicados à linha errada.
- **Causa:** a identidade anterior pressupunha uma única fonte de sessões. O estado de animação global também passou a incluir trabalho Claude ao mesclar as listas.
- **Solução:** linhas, persistência e marcação de leitura agora usam provedor + id. A busca de títulos e a remoção automática do Codex continuam limitadas ao Codex; o Claude confirma leitura somente pela própria sessão. Os gauges detalhados do Codex usam exclusivamente o trabalho Codex.
- **Prevenção:** mantenha fixtures com ids iguais entre provedores e trabalho simultâneo. Não use foco ou índices globais do Codex como confirmação de leitura de um chat.

## Quota semanal assumia sempre o slot primary

- **Sintoma:** contas em que o limite de 5h ocupava `primary` e o semanal ocupava `secondary` mostravam o indicador semanal ausente ou registravam o limite errado no histórico diário.
- **Causa:** a seleção procurava apenas `codex:primary`, em vez de identificar o bucket oficial `codex` pela duração da janela.
- **Solução:** os indicadores e o extrator histórico agora escolhem 5h e 7d por duração nos slots `primary` ou `secondary` do bucket oficial; buckets de famílias de modelos continuam excluídos.
- **Prevenção:** mantenha fixtures com slots invertidos, bucket `codex` somente em `rateLimitsByLimitId`, e bucket de família contendo as duas janelas.

## Testes de analytics dependiam do relogio real

- **Sintoma:** fixtures de agosto deixavam de validar totais mensais quando executadas em outro mês.
- **Causa:** alguns construtores de `LocalUsageAnalyticsService` nos testes usavam o relógio padrão, embora os JSONL de fixture usassem `analyticsNow` fixo.
- **Solução:** as fixtures temporais agora injetam o mesmo relógio fixo.
- **Prevenção:** qualquer teste que agrupe uso por dia ou mês deve controlar o relógio e os timestamps de fixture.

## Conclusão de agent era removida sem confirmação de leitura no Codex

- **Sintoma:** ao concluir um chat já aberto no Codex, a lista do Tracker podia remover sua conclusão não lida quando a janela do Codex estava em segundo plano ou minimizada. A regra introduzida na 0.18.12 ainda era ampla: ao voltar o Codex para primeiro plano, ela também removia a conclusão de um chat não selecionado.
- **Causa:** `RefreshAgentsAsync` tratava a ausência no índice global local de threads não lidas do desktop como leitura confirmada. Esse estado não expõe a thread global selecionada, então foco da janela e ausência de uma thread não provam que o usuário leu sua conclusão.
- **Solução:** removida a reconciliação automática pelo índice unread do desktop, em fail-closed. A remoção continua apenas no clique explícito da linha do Tracker, em marcar todos como lidos, ou quando a mesma root chat volta a ficar ativa.
- **Prevenção:** não deduza uma ação específica do usuário de estado global sem identidade da thread selecionada. Mantenha regressão que proíba qualquer remoção automática por ausência no índice unread e preserve as ações explícitas e a transição da mesma root para ativa.

## Chat sem projeto aparecia com diretorio transitorio como projeto

- **Sintoma:** a lista de agents exibia o ultimo segmento de um `cwd` temporario, como `qu`, para um chat que nao pertencia a nenhum projeto; o rotulo esperado era `Sem projeto`.
- **Causa:** `AgentActivityService` copiava o `cwd` bruto do `session_meta` para `ProjectPath`, tratando qualquer diretorio existente ou informado como um projeto.
- **Solucao:** o servico agora resolve o `cwd` com `ProjectRootResolver`, guardando apenas uma raiz Git/worktree verificavel ou `null`; a heranca de subagents continua usando a raiz canonica do pai.
- **Prevencao:** mantenha regressao para o `cwd` transitorio observado, um subdiretorio dentro de repositorio Git e um subagent que herda a raiz valida do pai; nao introduza heuristicas textuais para caminhos de sessoes do Codex.

## Chat ativo perdia identidade nos detalhes por chat

- **Sintoma:** um chat recente com rollout JSONL ainda aberto para escrita podia aparecer como conversa sem título/projeto ou deixar de aparecer no agrupamento esperado de detalhes por chat, embora seus tokens fossem contabilizados.
- **Causa:** `LocalUsageAnalyticsService.Describe` usava `File.ReadLines`, cuja abertura não compartilhava escrita com o writer ativo do Codex. Ao falhar, o fallback usava o caminho físico como `ThreadId`; o parser de tokens posterior já usava `FileShare.ReadWrite`, criando uma linha com uso sem a identidade do chat.
- **Solução:** a leitura limitada dos oito primeiros registros agora usa `FileStream` e `StreamReader` com `FileShare.ReadWrite`, preservando o metadata do rollout durante escrita concorrente.
- **Prevenção:** manter uma regressão que segura uma fixture JSONL aberta para escrita enquanto chama o seam público `Read`, exigindo `ThreadId`, título e tokens corretos; leituras de JSONL append-only devem compartilhar escrita tanto no metadata quanto no parser de uso.

## Rollouts de manutencao de memories apareciam como agents do produto

- **Sintoma:** a lista de agents mostrava trabalho interno cujo `cwd` era `%USERPROFILE%\\.codex\\memories`, incluindo roots concluidos e subagents ativos.
- **Causa:** `AgentActivityService` preservava o `cwd` do primeiro `session_meta`, mas nao o usava para decidir se o rollout era exibivel.
- **Solucao:** o servico marca, no primeiro metadata, caminhos canonicos iguais ou descendentes de `%USERPROFILE%\\.codex\\memories` e os exclui antes da deduplicacao por `ThreadId` nas listas ativa e concluida.
- **Prevencao:** mantenha cobertura para a raiz, descendentes, comparacoes sem distincao de maiusculas/minusculas e prefixos irmaos como `memories-sibling`; `cwd` ausente ou invalido deve continuar visivel.

## Fixture SQLite não liberava o arquivo temporário no Windows

- **Sintoma:** o cleanup da fixture de títulos por chat falhava com `IOException` ao apagar o banco SQLite temporário após a consulta.
- **Causa:** conexões de fixture usavam o pool padrão do provider e podiam manter um handle do arquivo até depois do fim do bloco `using`.
- **Solução:** os testes chamam `SqliteConnection.ClearAllPools()` antes de remover o diretório temporário; o índice de produção já usa `Pooling=false` em sua conexão somente leitura.
- **Prevenção:** ao apagar bancos SQLite temporários no Windows, limpe explicitamente os pools do provider antes de `Directory.Delete`.

## Nova execução no mesmo chat root duplicava a linha concluída

- **Sintoma:** depois que um chat root concluía e ficava não lido, iniciar outra task nesse mesmo chat adicionava uma linha ativa sem remover a conclusão anterior; status e tempo apareciam em entradas separadas.
- **Causa:** linhas ativas eram reconciliadas por `ThreadId`, enquanto conclusões eram reconciliadas e persistidas por `CompletionId`; a composição apenas concatenava as duas coleções, permitindo que turnos diferentes do mesmo chat coexistissem.
- **Solução:** a identidade visual e persistida de conclusões passou a ser o `ThreadId`; quando esse chat volta a ficar ativo, a conclusão não lida é removida e persistida, a mesma linha é promovida para ativa e seu status e tempo são recalculados a partir da nova execução.
- **Prevenção:** cubra a sequência conclusão não lida -> novo `task_started` no mesmo root, exigindo uma única linha, identidade visual estável, status ativo e elapsed reiniciado.

## Widget permanecia visível após fechar o Codex para a bandeja

- **Sintoma:** ao clicar no X da janela do Codex, o aplicativo permanecia nos ícones ocultos, mas o tracker ocioso continuava visível.
- **Causa:** `GetForegroundWindow` ainda podia apontar para uma HWND do processo Codex depois do X, embora ela já estivesse invisível ou DWM-cloaked; o monitor verificava somente `IsIconic`, então a classificava incorretamente como Codex em primeiro plano. A primeira tentativa de distinguir foco transitório do tracker foi insuficiente porque não tratava essa HWND residual.
- **Solução:** o monitor agora rejeita HWNDs do Codex que não estão visíveis ou estão cloaked. Se `DwmGetWindowAttribute` não estiver disponível ou falhar, mantém o comportamento compatível e considera a janela não cloaked.
- **Prevenção:** cubra no seam do monitor uma HWND do caminho real do Codex escondida, cloaked, visível e minimizada; preserve a prioridade absoluta de trabalho ativo e conclusões não lidas na política de visibilidade.

## Widget sumia ao ler a última conclusão com o Codex em primeiro plano

- **Sintoma:** ao abrir o último trabalho concluído não lido, o indicador era removido e o widget desaparecia mesmo com a janela do Codex em foco.
- **Causa:** a janela desktop real pertence a `ChatGPT.exe` dentro do pacote `WindowsApps\OpenAI.Codex_*`; o detector aceitava somente `codex.exe`, que neste host não possui a HWND principal. Depois que o último não lido era removido, o falso estado de background fazia a política ocultar o widget.
- **Solução:** reconhecer `ChatGPT.exe` e `codex.exe` somente quando pertencem ao pacote/caminho desktop do Codex, preservando a rejeição do CLI app-server. O teste reproduz o caminho real e também rejeita um `ChatGPT.exe` fora do pacote.
- **Prevenção:** identificar a aplicação pelo pacote e pelo executável que realmente possui a HWND, não pelo nome do processo auxiliar; validar `GetForegroundWindow`, PID e caminho no runtime instalado.

## Histórico local perdia o modelo antes do primeiro contexto do rollout

- **Sintoma:** tokens podiam permanecer no bucket `unknown` quando o JSONL não incluía `turn_context` antes do primeiro snapshot, embora o estado local do Codex registrasse o modelo da thread.
- **Causa:** analytics usava somente metadados do JSONL para o modelo temporal e não consultava a tabela local `threads(id, model)`.
- **Solucao:** um índice SQLite opcional, somente leitura e com timeout curto inicializa o modelo da thread antes do primeiro snapshot; `turn_context` e `thread_settings_applied` continuam sobrescrevendo-o temporalmente. A assinatura inclui banco principal e WAL; falhas transitórias preservam o último mapa válido, e alterações de fallback reconstroem somente os rollouts afetados.
- **Prevencao:** manter fixtures com modelo SQLite antes do primeiro snapshot, troca posterior no rollout, thread sem modelo, banco ausente/corrompido/bloqueado, atualização em WAL e invalidação seletiva do cache.

## Ranking local atribuía tokens a unknown após trocar o modelo

- **Sintoma:** o ranking semanal podia concentrar grande volume em `unknown`, embora a conversa tivesse aplicado um modelo antes dos snapshots seguintes.
- **Causa:** `LocalUsageAnalyticsService` atualizava o modelo temporal somente para `turn_context`, ignorando `event_msg.payload.type=thread_settings_applied` e `payload.thread_settings.model`.
- **Solucao:** `thread_settings_applied` agora atualiza o modelo corrente antes do próximo `token_count`; snapshots anteriores não são reatribuídos, e `model_provider` isolado continua sem inferência.
- **Prevencao:** para toda nova fonte de modelo no rollout, testar a ordem evento-configuracao -> snapshot, snapshot anterior, troca de modelo e eventos auxiliares que não podem atribuir modelo.

## Sessao ativa sumia quando o mtime do JSONL ficava estagnado

- **Sintoma:** o widget podia mostrar zero agents para uma sessao com `task_started` e eventos recentes, embora o arquivo JSONL continuasse crescendo.
- **Causa:** `AgentActivityService` filtrava arquivos exclusivamente por `FileInfo.LastWriteTimeUtc` antes de parsear; em alguns rollouts o timestamp de escrita permanecia no inicio da sessao.
- **Solucao:** rollouts de hoje ou ontem no calendario local sao considerados mesmo com mtime antigo, e arquivos ja cacheados continuam sendo verificados pela assinatura de tamanho enquanto permanecem ativos ou mudam. O parser incremental processa apenas os bytes novos e descarta cache inativo inalterado fora dessa janela.
- **Prevencao:** nao trate mtime como a unica prova de atividade em streams append-only; cubra um mtime estagnado com timestamps JSON recentes e crescimento do arquivo.

## Abertura detalhada concorria leitura fria dos rollouts com a quota oficial

- **Sintoma:** ao abrir diretamente no modo detalhado, se analytics terminasse antes do primeiro snapshot de quota, o painel podia permanecer sem dados locais até a próxima atualização periódica.
- **Causa:** o handler de analytics aplicava seu resultado apenas quando `_client.Snapshot` já existia, descartando silenciosamente o resultado que vencesse a corrida de inicialização.
- **Solucao:** quota e analytics continuam paralelos, mas um coordenador thread-safe retém analytics até o primeiro snapshot e o consome uma única vez. Cada conexão recebe uma geração; callbacks antigos são rejeitados antes e dentro do dispatcher, e o cliente anterior é encerrado antes da nova descoberta. A busca do executável também cede o dispatcher via `Task.Run` com o token de shutdown, sem mover o `Process.Start` do app-server.
- **Prevencao:** em inicialização, não serializar fontes independentes apenas para evitar corridas. Guardar resultados prontos com ownership explícito, versionar callbacks de recursos substituíveis, medir o painel completo como `max(quota, analytics)` e cobrir ambos os ordenamentos e callbacks obsoletos por teste determinístico.

## Parse frio do histórico local bloqueava a abertura detalhada

- **Sintoma:** o primeiro analytics de um histórico grande podia levar vários segundos, embora leituras aquecidas fossem muito menores.
- **Causa:** o serviço analisava cada JSONL frio em série; as métricas de cache aquecido não representavam a abertura real.
- **Solucao:** a fase pura de parse por arquivo usa no máximo duas tarefas paralelas. Cada leitura termina no tamanho capturado pela assinatura inicial, adiando bytes acrescentados por um writer para o próximo ciclo. Assinaturas, cache, tails parciais, contadores, merge e deduplicação continuam consolidados serialmente e em ordem estável.
- **Prevencao:** medir parse frio com instâncias novas do serviço e informar arquivos, bytes, totais e mediana; não paralelizar o app-server ou serviços que já possuem seu próprio gate.

## Widget restaurava na tela primaria apos uma reinstalacao com varios monitores

- **Sintoma:** uma posicao persistida na tela secundaria, por exemplo `Left=2877, Top=176`, voltava para a tela primaria depois de reinstalar.
- **Causa:** o construtor limitava `Left` e `Top` com `SystemParameters.WorkArea`, que representa apenas a area de trabalho primaria.
- **Solucao:** a posicao persistida e aplicada sem clamp; apos criar o HWND, seus bounds nativos sao comparados com as areas de trabalho nativas de todos os monitores. Posicoes visiveis, inclusive parciais e negativas, sao mantidas. Somente uma janela inteiramente offscreen e movida para caber no monitor disponivel mais proximo, com `SetWindowPos` nativo para evitar conversao ingenua entre px e DIP em DPI misto.
- **Prevencao:** nunca valide restauracao de janela multimonitor contra `SystemParameters.WorkArea`; use os bounds do HWND e todas as areas de trabalho reais, cobrindo tela secundaria, coordenadas negativas, visibilidade parcial e monitor removido com teste deterministico.

## Visualizacao detalhada podia manter espaco vazio abaixo da versao

- **Sintoma:** a janela detalhada podia ser ampliada ate 720 DIP mesmo quando seu conteudo terminava no texto da versao, deixando uma grande area vazia no rodape.
- **Causa:** o limite maximo era fixo e independente da altura desejada pelo conteudo dentro do `ScrollViewer`; uma altura persistida maior continuava valida.
- **Solucao:** o conteudo detalhado agora recalcula o teto da janela apos o layout, limitado pela politica existente, e reduz imediatamente a altura quando ela excede o conteudo real. A janela ainda pode ser reduzida para usar rolagem.
- **Prevencao:** mantenha o limite superior do resize derivado do `DesiredSize` do conteudo e cubra a normalizacao do teto com teste deterministico.

## Fundo retangular permanecia no widget compacto circular

- **Atualizacao:** alem do fundo transparente da janela layered, o compacto agora desativa `DWMWA_NCRENDERING_POLICY` com `DWMNCRP_DISABLED` e usa `DWMWCP_DONOTROUND`; detalhado e Settings restauram rendering habilitado e cantos arredondados em toda transicao de modo e preview de tema. Isto elimina a composicao nao-cliente que pode acrescentar uma sombra sem usar `SetWindowRgn`/crop.

- **Sintoma:** o modo compacto mostrava um retangulo escuro arredondado atras do gauge, mesmo com uma `Ellipse` visual de 36 x 36.
- **Causa:** `Background=GlassSurface` da `Window` e da borda raiz ainda preenchia todo o HWND (62 x 52 DIP). Uma elipse filha nao recorta nem a superficie da janela nem seu hit testing nativo.
- **Solucao (substituida):** `SetWindowRgn`/`CreateEllipticRgn` foi removido: o recorte nativo cortava a franja antialias do anel nas bordas. A janela agora usa `AllowsTransparency=True` e fundo transparente; somente a `Ellipse` central pinta o compacto. O gauge e o fundo sao vetores no tamanho final, derivados da altura atual (42/38 DIP no minimo, sempre com diferenca de 4 DIP), sem `Viewbox`; compacto fica sem sombra para nao introduzir uma superficie retangular.
- **Prevencao:** nao use regioes HWND para recortar geometria WPF antialiasada. Em widgets compactos circulares, mantenha a superficie da janela transparente e deixe apenas os elementos vetoriais circulares desenharem pixels; aplique superficies retangulares somente nos modos que realmente as exigem.

## Fundo detalhado parecia transparente em uma janela layered

- **Sintoma:** o desktop podia ficar visivel no modo detalhado apesar de ele dever manter um painel solido.
- **Causa:** `AllowsTransparency=True` e necessario ao compacto circular e nao muda depois do HWND; a raiz detalhada dependia de uma superficie glass generica em vez de um recurso semantico opaco.
- **Solucao:** `DetailedSurface` totalmente opaco pinta a raiz detalhada e `SettingsSurface` pinta configuracoes; ambos acompanham o tema no `ThemeManager` e o container raiz preserva cantos arredondados.
- **Prevencao:** em janelas layered de modos mistos, mantenha o HWND transparente global e pinte superficies opacas semanticas em cada modo retangular; valide troca de tema e abertura/fechamento das configuracoes.

## Alternancia Compacto/Detalhado restaurava a largura transitoriamente limitada

- **Sintoma:** depois de redimensionar o compacto, alternar para detalhado e voltar podia abrir o compacto em 320 px, apesar do slot persistido conter, por exemplo, 124 px.
- **Causa:** `ApplyWindowModeSize` lia corretamente o slot compacto, mas chamava `SetCompactSize` com a propriedade `Width` transitoria da janela, ainda herdada do detalhado. Tambem o `SizeChanged` reagia as alteracoes programaticas de constraints durante a alternancia e podia recalcular o compacto com esse valor intermediario.
- **Solucao:** a politica publica seleciona explicitamente o tamanho do modo de destino e o ramo compacto aplica esse slot. O listener `SizeChanged` foi removido: `ResizeMode=NoResize` e `ApplyManualResize` ja sao a unica via de resize do usuario e preservam a proporcao.
- **Prevencao:** em transicoes de modo, nunca derive o destino de propriedades WPF que podem refletir constraints do modo anterior. Testar a sequencia compacto -> detalhado -> compacto repetida com slots distintos, e manter resize de usuario em um caminho explicito separado de layout programatico.

## `WS_THICKFRAME` reservava uma faixa não-cliente no widget sem moldura

- **Sintoma:** o compacto exibia uma faixa horizontal no topo mesmo sem `WindowChrome`; ajustes de brush e borda DWM apenas mascaravam a cor.
- **Causa:** `ResizeMode=CanResize` mantém `WS_THICKFRAME`. Mesmo quando `WM_NCCALCSIZE` estende o client a todo o HWND (insets 0/0/0/0), o DWM ainda pode compor pixels de frame diferentes entre os estados ativo e inativo. Um resize apenas atualizava temporariamente esses pixels.
- **Solução:** usar `ResizeMode=NoResize`, que remove `WS_THICKFRAME` por construção, e implementar o resize no preview de mouse do WPF. O compacto mantém proporção 62:52 e limites 62–320 px; detalhado e Settings mantêm largura 300 px e resize apenas vertical. Captura perdida ou desativação encerra o gesto com segurança. Para multimonitor, o gesto captura a work area Win32 do monitor que continha os bounds iniciais, converte-a para DIP uma vez e a reutiliza até o fim; a geometria limita o tamanho na direção da alça em vez de aplicar clamp em `Left`/`Top`, preservando a borda oposta.
- **Prevenção:** diagnosticar janelas customizadas comparando style nativo, `GetWindowRect`, `GetClientRect`, `ClientToScreen` e pixels ativo/inativo. Não considerar inset zero prova de que o DWM deixou de compor o frame; se a aparência não-cliente precisa ser invariável, remova o style que a cria e substitua também sua interação. No resize manual, mantenha o gesto de borda reservado até o `MouseUp`, mesmo se a captura for perdida, para o mesmo clique nunca ser reinterpretado como arraste da janela. Nunca use `SystemParameters.WorkArea` durante um gesto multimonitor: ele representa só a área primária e pode teletransportar o widget.

## Gráfico WPF desenhado manualmente não recebia hover entre as barras

- **Sintoma:** o gráfico diário renderizava normalmente, mas mover o cursor sobre dias com barra mínima ou sobre o espaço vertical acima da barra não exibia o tooltip.
- **Causa:** um `FrameworkElement` sem `Background` participa do hit-test apenas nas primitivas efetivamente desenhadas. Como as barras ocupavam poucos pixels, a maior parte da coluna visual não gerava `MouseMove`.
- **Solução:** o controle passou a declarar uma superfície de hit-test própria e sobrescrever `HitTestCore`; cada dia mantém um retângulo de coluna exato, independente da altura visível da barra. O tooltip usa esse mapa para mostrar dia, tokens e custo na moeda selecionada.
- **Prevenção:** controles WPF renderizados diretamente em `OnRender` devem definir explicitamente sua geometria de hit-test. Não presuma que toda a caixa de layout recebe mouse apenas porque `ActualWidth` e `ActualHeight` estão definidos.

## `AccessViolationException` ao consultar composição do DWM

- **Sintoma:** a janela encerrava na inicialização ao aplicar o backdrop, com `System.AccessViolationException` em `DwmIsCompositionEnabled`.
- **Causa:** a função nativa retorna um `HRESULT` e recebe um ponteiro de saída para `BOOL`; declará-la como retorno booleano sem parâmetro corrompe a chamada nativa.
- **Solução:** declarar `DwmIsCompositionEnabled(out bool enabled)` com retorno `int` e só aplicar o backdrop quando o `HRESULT` indicar sucesso e a composição estiver ativa.
- **Prevenção:** conferir assinaturas P/Invoke com o contrato Win32, especialmente parâmetros de saída e tipos de retorno `HRESULT`.

## Desktop Acrylic fica sólido quando a janela perde ativação

- **Sintoma:** `DWMSBT_TRANSIENTWINDOW` exibe Desktop Acrylic com a janela ativa, mas a superfície fica sólida ao ativar outro aplicativo.
- **Causa:** Background/Desktop Acrylic substitui a translucidez por fallback sólido quando a janela desktop é desativada; esse comportamento é intencional da Microsoft.
- **Solução:** manter `CompositionTarget` transparente e o caminho DWM estável, aceitando o fallback sólido inativo. Glass persistente inativo não foi resolvido: `DesktopAcrylicController` com target `Windows.UI.Composition` caiu depois em `CoreMessaging` (`0xc000027b`), e o target `Microsoft.UI.Composition` exigido não está exposto na projeção C# usada.
- **Prevenção:** não misturar stacks `Windows.UI.Composition` e `Microsoft.UI.Composition`; não reportar transparência inativa como resolvida sem teste ativo/inativo. Uma solução futura exigiria interop Microsoft-only suportado, possivelmente via helper C++/WinRT.

## `WindowChrome` deixava faixa sólida no topo

- **Sintoma:** uma faixa horizontal sólida permanecia acima do conteúdo glass.
- **Causa:** a área de frame/caption do `WindowChrome` ainda era desenhada pelo sistema.
- **Solução:** definir `GlassFrameThickness=0` e desativar captions Aero, deixando o conteúdo controlar toda a superfície.
- **Prevenção:** em janelas WPF sem moldura, configure explicitamente frame, caption e hit testing; transparência apenas no conteúdo não elimina o frame nativo.

## Build Release falha porque o executável está em uso

- **Sintoma:** `MSB3027`/`MSB3021` ao copiar `apphost.exe` para `AqTracker.exe` após várias tentativas.
- **Causa:** uma instância da própria build Release permaneceu aberta durante o smoke visual e manteve o executável bloqueado.
- **Solução:** encerrar somente a instância de teste identificada pelo caminho `src\AqTracker\bin\Release` e repetir o build.
- **Prevenção:** finalizar o smoke local antes de recompilar ou gerar o instalador; não encerrar a instalação do usuário por nome de processo sem conferir o caminho.

## Tokens locais divergiam do total processado do Codex

- **Sintoma:** agosto aparecia abaixo do total auditado, apesar de todos os rollouts ativos parecerem presentes.
- **Causa:** o leitor ignorava `archived_sessions`, descartava o primeiro snapshot de forks e usava `total_tokens`. A auditoria do Codex soma 180 rollouts ativos e 14 arquivados; o total processado é `input_tokens + output_tokens + reasoning_output_tokens`. `cached_input_tokens` já é subconjunto da entrada. Além disso, um subagent pode incluir metadata herdado do pai depois de seu próprio `session_meta`; somente o primeiro metadata identifica o arquivo.
- **Solução:** a leitura padrão inclui `sessions` e `archived_sessions`. O primeiro snapshot cumulativo de cada rollout conta como contexto processado, snapshots seguintes entram por delta e uma queda inicia novo segmento. A métrica soma entrada, saída e reasoning; custo cobra reasoning como saída. Duplicatas só são removidas quando mesmo id e prefixo físico byte a byte comprovam checkpoint sobreposto.
- **Prevenção:** regressões cobrem as duas raízes padrão, fork com metadata herdado, primeiro snapshot de fork, reset, `total_tokens` divergente dos componentes e segmentos com mesmo id sem prefixo. Não usar quota, clamp ou igualdade de `session_id` como aproximação.

## `ffmpeg` sem decoder SVG ao exportar assets da marca

- **Sintoma:** `Decoding requested, but no decoder found for: svg` ao tentar converter o SVG fonte em PNG/ICO.
- **Causa:** a build local do `ffmpeg` reconhece o demuxer, mas nao inclui um renderer/decoder SVG como `librsvg`.
- **Solucao:** `scripts\export-brand-assets.ps1` desenha a mesma geometria com `System.Drawing` e empacota PNGs com alpha em um ICO multi-resolucao; cada `byte[]` e preservado como uma entrada, sem flattening do pipeline PowerShell.
- **Prevencao:** use o exportador do repositorio; nao presuma suporte SVG apenas porque `ffmpeg` esta instalado.

## `JsonElement` null em `secondary` ou `individualLimit`

- **Sintoma:** a leitura de `account/rateLimits/read` falhava porque `TryGetProperty` exige um objeto.
- **Causa:** o protocolo pode retornar `rateLimits.secondary=null` e `individualLimit=null`.
- **Solucao:** o parser valida `JsonValueKind.Object` antes de ler uma janela; limites ausentes nao viram 0%.
- **Prevencao:** o teste de regressao reproduz o payload com ambos os campos nulos.

## `Access is denied` ao chamar `codex` em WindowsApps

- **Sintoma:** a descoberta pelo `PATH` pode encontrar um alias em `WindowsApps` que nao e executavel por processos externos.
- **Causa:** App Execution Alias do Windows, em vez do binario real do Codex CLI.
- **Solucao:** o aplicativo tenta `where codex`, caminhos de instalacao conhecidos e um caminho configurado pelo usuario. Neste host, o binario funcional esta em `C:\Users\luing\.codex\plugins\.plugin-appserver\codex.exe`.
- **Prevencao:** configure explicitamente `CodexPath` em `%APPDATA%\AqTracker\settings.json` quando a descoberta falhar.

## Inno Setup instalado pelo `winget`, mas `ISCC.exe` nao encontrado em `Program Files (x86)`

- **Sintoma:** a publicacao do instalador termina com `Inno Setup was installed but ISCC.exe was not found` depois de o `winget` confirmar a instalacao.
- **Causa:** o `winget` pode instalar Inno Setup por usuario em `%LOCALAPPDATA%\Programs\Inno Setup 6`, e nao no caminho tradicional em `Program Files (x86)`.
- **Solucao:** `scripts\build-installer.ps1` consulta `PATH`, o registro de desinstalacao e os caminhos por maquina e por usuario antes de compilar.
- **Prevencao:** use o script de build, em vez de fixar um caminho para `ISCC.exe`.

## ISPP nao suporta `ReadFile` ao obter a versao do instalador

- **Sintoma:** o ISCC 6.7.3 falhava ao compilar `AqTracker.iss` quando `AppVersion` era definido por `Trim(ReadFile("..\\VERSION"))`.
- **Causa:** `ReadFile` nao e uma funcao suportada pelo preprocessor do Inno Setup nessa versao.
- **Solucao:** `scripts\\build-installer.ps1` le e valida `VERSION`, entao passa `/DAppVersion=<versao>` ao ISCC. O `.iss` mantem somente um fallback literal protegido por `#ifndef`, para compilacao manual.
- **Prevencao:** deixe I/O e validacao de arquivos no script PowerShell; no ISPP use definicoes recebidas por linha de comando ou macros compativeis.

## Tema WPF falha ao alterar uma `SolidColorBrush`

- **Sintoma:** a janela encerrava na inicialização com `não é possível definir uma propriedade ... estado somente leitura`.
- **Causa:** brushes compartilhadas de `Application.Resources` podem ser congeladas pelo WPF.
- **Solução:** o gerenciador de tema substitui a brush inteira no dicionário de recursos; as superfícies visuais usam `DynamicResource` para recebê-la em tempo real.
- **Prevenção:** não mutar a propriedade `Color` de brushes declaradas em XAML.

## Quota principal parecia invertida em relação ao Codex

- **Sintoma:** o widget mostrava 16% quando o cliente Codex mostrava 84%.
- **Causa:** `account/rateLimits/read` expõe `usedPercent`; o cliente Codex apresenta o percentual restante. O parser preservava corretamente o valor de uso, mas a métrica periférica o mostrava diretamente.
- **Solução:** a apresentação semanal exibe `100 - usedPercent`; forecast continua recebendo o `usedPercent` original.
- **Prevenção:** teste de regressão com payload real-shaped (`usedPercent=16` resulta em `84%` exibido).

## Arraste da janela sem moldura nao iniciava sobre o conteudo

- **Sintoma:** clicar e arrastar o gauge, textos ou a area vazia do widget podia nao mover a janela.
- **Causa:** o arraste dependia de `MouseLeftButtonDown` com bubbling no `Grid` raiz e de `DragMove()`. Com `WindowChrome` e elementos sobrepostos, esse ponto de entrada nao e confiavel; alem disso, o chrome invisivel continuava hit-testable.
- **Solucao:** a janela observa o gesto no preview e só inicia o movimento nativo depois do limiar de arraste do Windows. Mantém a ativação normal, não reaplica o backdrop durante/depois do movimento e preserva cliques e duplos cliques.
- **Prevencao:** em janelas WPF sem moldura, diferencie clique de arraste pelo limiar nativo; não suprima ativação nem recomponha o backdrop como efeito colateral do gesto.

## Forecast semanal podia contradizer o risco e perder o timing

- **Sintoma:** a previsao podia exibir `Risco de esgotar antes do reset · 100% projetado`; notificacoes parciais tambem podiam apagar `resetsAt` e `windowDurationMins`, deixando a previsao indisponivel.
- **Causa:** o status comparava o valor bruto com 100%, enquanto a interface arredondava para inteiro. O merge sparse substituia a janela completa mesmo quando a notificacao trazia apenas o percentual usado, e o forecast era recalculado com o relogio atual em vez do instante do snapshot.
- **Solucao:** status e formatacao agora compartilham precisao de uma casa perto do limiar; 100% usado tem estado explicito. O forecast usa `ReceivedAt`, valida dados temporais e numericos, e o merge so preserva timing ausente quando percentual monotono, reset futuro e campos fornecidos comprovam o mesmo ciclo.
- **Prevencao:** regressões cobrem projecao e esgotamento exatos, limites temporais, offsets UTC, arredondamento e updates sparse do mesmo ciclo, de ciclo novo e apos o reset.

## Upgrade falhava ao substituir `clrjit.dll` com o app na bandeja

- **Sintoma:** o instalador abortava com `DeleteFile failed; code 5. Acesso negado` ao atualizar uma instalacao cujo Agent Quota Tracker continuava aberto, inclusive oculto na bandeja.
- **Causa:** o Restart Manager registrava todos os arquivos do runtime self-contained (462 no repro), incluia `System` junto das instancias do app e recusava o fechamento com `Permission Denied + Session Mismatch`. Duas instancias instaladas podiam coexistir e manter `clrjit.dll` carregado. O desinstalador tambem nao encerrava automaticamente o processo antes de remover os arquivos.
- **Solucao:** `CloseApplicationsFilter` limita o Restart Manager ao executavel exato `AqTracker.exe`; o app usa mutex por caminho instalado para impedir duplicatas futuras. Um evento nomeado acionado por `--shutdown-existing` permite ao desinstalador solicitar shutdown gracioso e aguardar a liberacao do mutex antes da remocao.
- **Prevencao:** `scripts/test-installer-upgrade.ps1` cobre instalacao, duas instancias legadas, upgrade com app aberto, single-instance na nova versao, relancamento, uninstall com app aberto, preservacao das configuracoes e ausencia de processos/arquivos orfaos.

## Validação local de instalação comparava nome incorretamente

- **Sintoma:** validação local informava falha de instalação mesmo com instalador concluído, porque o DisplayVersion retornava vazio ao ler a configuração por nome esperado.
- **Causa:** o installer registra DisplayName como Agent Quota Tracker version <version>, e a rotina de validação buscava exatamente Agent Quota Tracker; também usava uma visão implícita de registro sem distinguir Registry32/Registry64.
- **Solução:** validar pela chave estável do app ({D8C84F82-ED90-4F1F-AB4E-1455E5B66C2C}_is1) ou por prefixo de DisplayName, em HKCU Uninstall com Registry64 e Registry32, e comparar DisplayVersion com VERSION.
- **Prevenção:** durante smoke de instalação, validar FileVersion do executável instalado e DisplayVersion da chave do uninstall, sem depender de igualdade exata de nome de exibição.

## Upgrade mantinha o runtime self-contained obsoleto

- **Sintoma:** após atualizar do instalador .NET 8 auto-contido para o payload .NET Framework 4.8, o setup novo era pequeno, mas o diretório instalado ainda mantinha centenas de DLLs do runtime e mais de 150 MB.
- **Causa:** a seção `[Files]` do Inno Setup copia os arquivos novos, mas não remove arquivos que deixaram de fazer parte do publish.
- **Solução:** `[InstallDelete]` remove somente o conteúdo de `{app}` antes de copiar o novo payload. `CloseApplications` continua encerrando apenas `AqTracker.exe`, e `%APPDATA%\AqTracker` não é tocado.
- **Prevenção:** toda migração que reduz ou renomeia o payload deve testar upgrade sobre a versão anterior e medir contagem/tamanho do diretório instalado, além do tamanho do setup.

## Popup de agentes travava na tela durante arraste do widget

- **Atualização (0.29.1):** o resize sem mudança de `Left`/`Top` também deixava o popup na posição antiga. `SizeChanged` agora agenda o reposicionamento em `DispatcherPriority.Loaded`, após o arrange do indicador. A regressão WPF usa um agente ativo e compara coordenadas reais do popup após aumentar e diminuir o compacto, sem mover a janela, com as coordenadas de um refresh explícito.

- **Sintoma:** o popup/lista de agentes abertos permanecia parado na tela quando o widget era arrastado, não acompanhando a posição da janela.
- **Causa:** o `Popup` do WPF usa HWND separado; mover a janela proprietária não acionava o `Reposition` interno no `PlacementTarget` apenas com `InvalidateArrange`/`UpdateLayout`.
- **Solução:** em `LocationChanged` (e/ou `WM_MOVING`), variar `HorizontalOffset` em `+0.01` DIP e restaurar imediatamente, forçando `OnOffsetChanged`/`Reposition` sem fechar o popup nem deslocá-lo perceptivelmente.
- **Prevenção:** para popups que precisam seguir janelas nativas sem moldura, validar movimento real do popup durante arraste e usar uma propriedade de posicionamento que force reposicionamento; build/layout local isolado não prova comportamento de ancoragem dinâmica.

## Lista de agentes fechava fora do widget e perdia a seta de hover

- **Sintoma:** a lista aberta fechava ao clicar fora do widget e o indicador fechado podia continuar mostrando apenas o número, sem a seta para baixo no hover.
- **Causa:** `Popup.StaysOpen=False` delegava o fechamento ao mecanismo global de clique do WPF; o estado da seta dependia de uma ligação `Tag` ao `IsOpen` de um `Popup`, atravessando o namescope separado do popup e deixando o template sem um estado visual confiável.
- **Solução:** a preferência persistida `IsAgentListExpanded` é independente do estado físico do popup, que só abre com agents ativos. `StaysOpen=True` mantém a lista até o clique explícito no indicador; o template lê `IsAgentListOpen` diretamente do view model. Linhas existentes são preservadas entre atualizações e apenas linhas novas recebem animação de entrada.
- **Prevenção:** não use um `Popup` como fonte de estado visual para templates fora do seu namescope. Separe preferência persistida, estado físico condicionado aos dados e estado visual do controle; cubra o round-trip e a detecção de itens novos com testes determinísticos.

## Novo agente reabria a lista sobre o modo detalhado

- **Sintoma:** depois de entrar no modo detalhado com a lista fechada, a chegada do primeiro agent podia abrir o popup por cima da janela.
- **Causa:** `ToggleDetailed` fechava o estado físico corretamente, mas `RefreshAgentsAsync` restaurava a preferência persistida quando a atividade passava de zero para ativa sem verificar o modo visual atual.
- **Solução:** o caminho de refresh só pode restaurar a lista quando `Expanded` é falso; a preferência continua preservada para reabertura ao voltar ao compacto.
- **Prevenção:** toda atribuição que abre um popup exclusivo do compacto deve carregar a condição do modo no mesmo ramo. Um teste estrutural cobre o callback assíncrono de atualização, não apenas o handler que troca o modo.

## Glow de trabalho aparecia nas barras do ranking

- **Sintoma:** durante trabalho ativo, cada barra de modelo no ranking recebia o mesmo sweep luminoso destinado ao percentual semanal.
- **Causa:** o template implícito global de `ProgressBar` continha o `WorkGlow` e reagia ao estado de trabalho herdado, atingindo toda instância do controle.
- **Solução:** o template global de ranking voltou a ser estático; a animação permanece implementada somente em `CircularQuotaGauge`, usado pelo percentual semanal compacto e detalhado.
- **Prevenção:** efeitos semânticos específicos de uma métrica não devem viver em estilos implícitos globais. Cubra a ausência do trigger no template de `ProgressBar` e a presença do estado de trabalho no gauge semanal.

## Preview de idioma podia acumular handles da tray

- **Sintoma:** alternar idioma repetidamente nas configurações poderia aumentar continuamente a contagem de handles GDI e menus do processo.
- **Causa:** a atualização recriava `NotifyIcon`, `Icon` e `ContextMenuStrip`, mas `NotifyIcon.Dispose()` não assume a propriedade nem descarta explicitamente os dois últimos no .NET Framework.
- **Solução:** uma única instância de `NotifyIcon` é preservada; ícone e menu novos são atribuídos antes que os anteriores sejam descartados, e os recursos finais também são liberados no fechamento.
- **Prevenção:** trate objetos nativos atribuídos a componentes WinForms como recursos com ownership explícito. Testes estruturais cobrem a troca e o descarte em preview repetido e no shutdown.

## Teste de entrada de agent dependia da preferência visual do runner

- **Sintoma:** o workflow de release falhava no teste de linha nova, embora a suíte passasse na máquina local.
- **Causa:** `SystemParameters.ClientAreaAnimation` é falso no runner GitHub Actions; o teste esperava animação ativa sem controlar essa entrada ambiental.
- **Solução:** `ApplyAgents` aceita uma preferência opcional injetável para testes, enquanto produção continua consultando o Windows. Os testes cobrem explicitamente animação habilitada e reduced motion.
- **Prevenção:** parâmetros de acessibilidade do sistema operacional devem ser entradas controláveis em testes determinísticos; não derive uma expectativa fixa do ambiente do runner.

## Publicação de release falhava durante indisponibilidade transitória do GitHub

- **Sintoma:** a execução de release `32042391885` recebeu HTTP 503 ao criar a release e, depois de publicar `v0.14.0`, falhou ao remover a release antiga `v0.13.3` por outro HTTP 503.
- **Causa:** o workflow tratava toda falha de `gh release view` como se a release não existisse e executava criação, upload e remoções sem retry. O cleanup obrigatório transformava uma indisponibilidade temporária após a publicação válida em falha total do pipeline.
- **Solução:** o workflow agora reconhece explicitamente HTTP 404 — e a mensagem exata `release not found` do `gh release view` — como release ausente, interpreta também o formato real `status code: 503`, aplica quatro tentativas com backoff exponencial às operações de publicação e API, confirma criações ambíguas sem interromper o retry se a confirmação também estiver indisponível, aceita 404 nas remoções idempotentes e sempre tenta a tag antiga mesmo após uma resposta ambígua ao apagar a release. Quando apenas o cleanup de versões antigas esgota tentativas transitórias, conclui com warning.
- **Prevenção:** em automações de publicação, classifique falhas HTTP por status; preserve erros não transitórios e separe a validade da release atual da retenção de artefatos antigos.

## Operadores de range/index (`x[1..]`) não compilam no target net48

- **Sintoma:** código novo usando `texto[1..]`, `texto[..indice]` ou `texto[^1]` compilaria em um projeto net8.0, mas falharia com `CS0518: Predefined type 'System.Index' is not defined` (ou `System.Range`) neste repositório, que ainda tem `TargetFramework=net48` em `AqTracker`, `AqTracker.Core` e `AqTracker.Tests`.
- **Causa:** `System.Index`/`System.Range` não existem no mscorlib do .NET Framework 4.8; `Microsoft.NETFramework.ReferenceAssemblies` fornece apenas as assemblies de referência do framework real, sem um shim para esses tipos. `LangVersion=latest` permite a sintaxe no compilador, mas o binding dos tipos falha em tempo de compilação.
- **Solução:** evitar `[..]`/`[^]` em qualquer projeto do repositório (todos net48); usar `string.Substring(start)`/`string.Substring(start, length)` equivalentes. Coleções (`[]`, `[..spread]`) continuam permitidas normalmente, pois expression collections não dependem de `System.Index`/`System.Range`.
- **Prevenção:** ao escrever código novo, prefira revisar arquivos já existentes no mesmo projeto para confirmar quais recursos de C# 8+ realmente compilam sob net48 antes de assumir que qualquer sintaxe válida para `LangVersion=latest` também é válida no runtime alvo.

## Chevron de hover do indicador de agentes não aparecia após o ajuste de estado aberto

- **Sintoma:** o indicador mostrava apenas o número em hover, especialmente com a lista aberta.
- **Causa:** dois `MultiDataTrigger`s misturavam `IsMouseOver` e `IsAgentListOpen`; a troca essencial de visibilidade ficou acoplada ao estado da lista em vez de depender somente do hover do botão.
- **Solução:** um `Trigger` direto de `IsMouseOver` agora oculta o número e mostra o chevron; um `DataTrigger` independente altera apenas o traço para cima quando a lista está aberta. As linhas também passaram a usar overlays sem hit-test, com hover de 400 ms e ripple de 600 ms que não bloqueiam o deep link.
- **Prevenção:** mantenha a visibilidade de affordances de hover em um trigger único do controle; estados de dados devem ajustar somente a aparência variante. Cubra o template com teste estrutural que exija o trigger direto e rejeite `MultiDataTrigger` nessa superfície.

## Configurações abriam altas e encolhiam alguns segundos depois

- **Sintoma:** ao abrir Configurações, a janela surgia alta (até 720 DIP) e, segundos depois, encolhia para 440 DIP sem interação.
- **Causa:** dois caminhos disputavam a altura. `Settings()` chamava `ScheduleSettingsHeightForContent()`, que media o conteúdo inteiro (sem limite do `ScrollViewer`) e expandia a janela; depois, qualquer snapshot de cota ou mudança de perfil chamava `ApplyWindowModeSize()`, que redefinia o modo Settings para `SettingsMinHeight` (440).
- **Solução:** removido o ajuste ao conteúdo. O modo Settings tem uma única altura fixa (`WidgetSizePolicy.SettingsHeight` = 572, 30% acima de 440) limitada à área de trabalho por `SettingsHeightForWorkArea`, e todo chamador de `ApplyWindowModeSize()` resolve para o mesmo valor; o conteúdo rola.
- **Prevenção:** um modo de janela deve ter uma única fonte de altura; não agende re-medições assíncronas que outro caminho síncrono possa sobrescrever.

## Teste WPF iniciava o Tracker real e acessava preferencias e tokens do usuario

- **Sintoma:** executar a suite WPF alterava o `settings.json` real e podia rotacionar os tokens Claude fora da instancia instalada; o usuario relatou perda de preferencias e necessidade de repetir o setup. O episodio historico nao possui evidencia suficiente para atribuir sua causa com certeza.
- **Causa:** `Application` agenda `OnStartup` no dispatcher mesmo sem chamar `Run()`. O teste construia `App`, inicializava recursos e bombeava o dispatcher, disparando uma segunda `MainWindow` com os stores reais, alem da janela de fixture. O mutex baseado no caminho do executavel permitia concorrencia com o Tracker instalado. Separadamente, `SettingsStore` truncava o JSON em cada save e convertia qualquer falha de leitura em preferencias padrao.
- **Solucao:** `App(launchMainWindow: false)` inicializa recursos WPF sem iniciar o runtime no harness; o teste exige exatamente sua propria janela. O mutex passou a usar a identidade do usuario, independente do executavel. Configuracoes usam escrita atomica com flush, backup da ultima versao salva e recuperacao; arquivos inacessiveis ou sem copia valida nunca viram defaults silenciosamente.
- **Prevencao:** testes WPF precisam controlar o startup e injetar stores e registro isolados. Foram reproduzidas em vermelho a abertura da janela real e a perda ao interromper o JSON. Verificar hashes dos arquivos reais antes/depois da suite, alem de testar JSON invalido, arquivo ausente, locks de leitura/substituicao, tokens DPAPI e registro Run temporario. Tokens OAuth rotativos nunca devem ser acessados por duas instancias simultaneas.

## Primeiro snapshot de agentes reabria a inicializacao minimizada

- **Sintoma:** o executavel instalado com `--startup` mostrava o widget e a lista em poucos segundos quando ja existiam agentes ativos e a preferencia da lista expandida estava ligada.
- **Causa:** o timer de visibilidade podia estabelecer um baseline sem agentes antes de terminar o primeiro snapshot; a chegada desse snapshot era interpretada como trabalho novo. A restauracao da lista tambem ignorava o estado de inicializacao oculta.
- **Solucao:** estabelecer o baseline somente depois da inicializacao do estado de agentes e suprimir a restauracao automatica da lista enquanto o startup estiver oculto.
- **Prevencao:** validar o executavel instalado com agentes realmente ativos e a lista expandida persistida, alem do smoke demo; exigir ausencia de janelas visiveis apos a primeira leitura e manter Mostrar da bandeja como restauracao explicita.

## Hover dos controles desaparecia durante atualizacoes ou ao chegar em Configuracoes

- **Sintoma:** no modo detalhado, passar o mouse nem sempre revelava Configuracoes; ao aproximar o ponteiro do botao, os controles podiam sumir e so voltar depois de sair e entrar novamente.
- **Causa:** `ApplyWindowModeSize()` zerava `Chrome.Opacity` e desabilitava hit tests em cada snapshot de quota/mudanca de perfil, mesmo com o mouse dentro. A revelacao dependia de um novo `MouseEnter` no `Root`, sem background: espacos vazios do cabecalho atingiam somente a borda externa `WindowSurface`, causando `MouseLeave` no Root durante a travessia ate os botoes.
- **Solucao:** um trigger declarativo controla opacidade e hit tests com `WindowSurface.IsMouseOver`, modo detalhado e Settings fechado. Removidos os handlers de entrada/saida e resets imperativos no resize; a superficie externa inclui padding e descendentes dos controles sem alterar os cantos transparentes do compacto.
- **Prevencao:** nao sobrescrever estado de interacao em refreshes de dados/layout. Regressao WPF cobre o hit test do padding e botao, entrada/saida, dez refreshes com hover mantido, compacto e expansao sob ponteiro parado. O estado herdado de mouse e injetado apenas na fixture isolada; o preview WPF isolado confirmou com mouse real a travessia padding -> engrenagem, com refreshes de 250 ms e opacidade/hit tests mantidos.

## Atualizacao silenciosa podia reativar inicializacao desabilitada nas configuracoes

- **Sintoma:** desabilitar o novo switch removia o Run, mas a proxima atualizacao silenciosa podia recria-lo.
- **Causa:** o instalador reutilizava a task `autostart` lembrada na instalacao anterior, independente da escolha posterior do aplicativo.
- **Solucao:** capturar o estado real do Run antes da atualizacao e usa-lo em upgrades silenciosos; preservar a task selecionada em instalacoes novas ou interativas. O switch consulta o Run como fonte efetiva do Windows.
- **Prevencao:** validar reinstalacao silenciosa com Run presente e ausente, conservando os arquivos de preferencias e tokens nos dois casos.


## Quotas da bandeja desapareciam sem foco ou trabalho ativo

- **Sintoma:** apenas o perfil ativo ou usado por ultimo permanecia na area de notificacao, mesmo com quotas disponiveis nos dois perfis.
- **Causa:** os icones consumiam `CompactQuotas`, cuja selecao depende do foco e da atividade do widget.
- **Solucao:** `NotificationQuotas` seleciona as quotas disponiveis dos perfis habilitados e periodos configurados sem consultar atividade; icones usam numeros maiores sem `%`.
- **Prevencao:** regressao WPF cobre ambos os perfis ociosos, foco exclusivo, selecao 5h/7d, perfil desabilitado e quota indisponivel; comparar icones ampliados e em 16 px.


## Widget aberto pela bandeja continuava visivel ao perder foco

- **Sintoma:** no modo de notificacao, clicar no icone abria o widget ate um fechamento explicito, mesmo apos trocar de aplicativo.
- **Causa:** o modo ignorava a politica automatica de visibilidade e nao encerrava a abertura manual no evento de desativacao.
- **Solucao:** acompanhar a abertura pela bandeja e verificar o processo da janela em primeiro plano apos desativacao e no timer existente; foco externo usa o mesmo fechamento do X, preservando icones e fechando a lista. Popups e dialogos do Tracker conservam a abertura.
- **Prevencao:** testes cobrem foco interno/externo, reabertura e opcao desabilitada; nao confundir HWND separado de um popup com outro aplicativo.


## Lista de agentes permanecia sempre no topo com a opcao desativada

- **Sintoma:** a lista podia cobrir outros aplicativos mesmo com `IsTopmost=false`. A janela principal instalada foi inspecionada e estava corretamente sem `WS_EX_TOPMOST`.
- **Causa:** `Popup` cria um HWND independente que o WPF marca como topmost; alterar `Window.Topmost` nao altera esse popup.
- **Solucao:** sincronizar a faixa de Z-order do popup com `Topmost` usando `SetWindowPos` sem ativar ou mover, ao trocar a opcao, abrir e reposicionar a lista.
- **Prevencao:** regressao WPF consulta `WS_EX_TOPMOST` nos HWNDs reais da janela e do popup, em ambos os valores, apos dez reposicionamentos e apos reabertura. Nao validar apenas a preferencia ou a propriedade gerenciada.

## Preview de icones falhava no checkout limpo do CI

- **Sintoma:** Image.Save encerrava o smoke WPF com erro generico de GDI+ no GitHub Actions.
- **Causa:** o teste pressupunha a existencia de artifacts, presente localmente mas ausente no checkout novo.
- **Solucao:** criar explicitamente o diretorio antes de salvar notification-icons.png.
- **Prevencao:** testes que produzem evidencias devem criar seus proprios diretorios de saida.

## Arrastar o slider movia a janela

- **Sintoma:** arrastar o marcador de opacidade movia a janela junto.
- **Causa:** o preview reconhecia controles interativos apenas para suprimir duplo clique, mas armava o arraste nativo mesmo assim.
- **Solucao:** retornar antes de armar movimento ou resize quando a origem pertence a um controle interativo.
- **Prevencao:** regressao WPF envia PreviewMouseDown pelo Thumb real e verifica que o gesto nao arma o arraste da janela.

## AqTracker consumia ~80 s de CPU no login e ~25% de um núcleo continuamente

- **Sintoma:** logo após ligar o Windows o processo acumulava dezenas de segundos de CPU e seguia usando ~0,2 núcleo mesmo ocioso na bandeja.
- **Causa:** o cache do `LocalUsageAnalyticsService` existia só em memória, então todo início reparseava o histórico inteiro de `~/.codex/sessions` (4 GB/1.700 rollouts ≈ 11 s de relógio e 20 s de CPU). Cada leitura periódica ainda relia os metadados (`Describe`) e o último byte de todos os arquivos. Além disso, o poll de agentes de 1 s fazia `EnumerateFiles(AllDirectories)` + `stat` de todos os rollouts (~100 ms de CPU por segundo).
- **Solução:** cache por arquivo persistido em `%LOCALAPPDATA%\AqTracker\cache\usage-analytics.bin` (binário versionado por `PersistentCacheFormat` e pela tabela de preços; escrita atômica; qualquer erro só causa um parse frio). Descritores e a checagem de newline são reaproveitados enquanto a assinatura prova que os bytes não mudaram. O poll de agentes, no layout `yyyy\MM\dd`, lê só o conjunto quente (cache, pastas de hoje/ontem, caminhos do `FileSystemWatcher`) e faz a varredura completa a cada 10 s; raízes sem esse layout continuam varrendo tudo.
- **Prevenção:** ao mudar `Describe`/`ParseAggregate` ou o significado de um campo cacheado, incremente `PersistentCacheFormat`. Valide com o harness que compara leitura fria sem cache vs. reinício a partir do cache (resultado precisa ser idêntico) e compare hot set vs. varredura completa por várias leituras.

## Indicador do Codex reaparecia após abrir o Codex desktop sem usá-lo

- **Sintoma:** com trabalho no Claude, abrir o Codex por acidente fazia o widget exibir também o gauge/indicador do Codex, que continuava visível após fechar o Codex. A lista trazia como não lida uma conclusão de dias atrás.
- **Causa:** ao iniciar, o Codex desktop anexa `event_msg.thread_settings_applied` ao rollout do último chat aberto (arquivo antigo, ex. `sessions/2026/09/17/...`). O mtime recente o recolocava no scan do `AgentActivityService`; seu `task_complete` antigo não estava em `_observedCompletionIds` (o baseline do startup só cobre os rollouts quentes) e virava não lido, deixando o perfil Codex `IsEngaged` indefinidamente.
- **Solução (0.30.1):** `CompletionNoveltyPolicy.IsNew` só aceita como não lida a conclusão Codex com `CompletedAt` a partir do início do rastreamento (menos 1 min de tolerância); as antigas ainda entram em `_observedCompletionIds`.
- **Prevenção:** "arquivo modificado" não significa "turno novo"; novidade de conclusão deve se basear no timestamp do evento. A entrada já persistida em `settings.json` precisa ser marcada como lida uma vez.
