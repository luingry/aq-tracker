# Plano: perfil Claude dinâmico (versão 0.22.0)

Objetivo: o widget passa a ter dois perfis, **Codex** (atual) e **Claude**. Cada perfil exibe sua cota e sua cor quando está *engajado*. Se os dois estiverem engajados, o widget compacto exibe as duas porcentagens lado a lado.

## Regras de comportamento

### Engajamento (regra pura em Core, testada por matriz)
Um perfil está engajado se qualquer uma das condições abaixo for verdadeira:
- a janela do app do perfil está em primeiro plano e não está minimizada;
- o perfil tem trabalho ativo;
- o perfil tem trabalho concluído não lido.

- Visibilidade: `WidgetVisibilityPolicy` mostra o widget se algum perfil estiver engajado ou se o widget estiver ativo. Para o Codex, o comportamento atual não muda.
- Exibição no compacto:
  - **Só um perfil engajado:** mostra os medidores desse perfil conforme `CompactQuotaDisplay` (`5h`, `7d` ou `both`), na cor dele.
  - **Os dois engajados:** mostra um medidor por perfil (2 no total), sempre com o Codex à esquerda e o Claude à direita. Cada medidor mostra a janela mais restritiva entre as permitidas por `CompactQuotaDisplay`, ou seja, a de menor % restante. O rótulo (`5h`/`7d`) fica na cor do perfil.
  - **Nenhum engajado e o widget visível** (por foco no próprio widget ou no popup): mostra o último perfil exibido, com o Codex como padrão.
- Cores: o Codex usa o `AccentColor` atual. O Claude usa o novo `ClaudeAccentColor` (padrão `#D97757`), passado pela mesma `AccentPalette` para claro/escuro. Os medidores deixam de usar `DynamicResource Accent` fixo e passam a ser ligados ao pincel do perfil exibido. O restante da UI continua usando o accent do Codex.
- Modo detalhado: mantém as análises do Codex e adiciona um bloco compacto **Claude** com 5h e 7d (% restante e reset). Quando não houver integração, o bloco mostra "Conectar Claude nas configurações".

### Sessões do Claude (trabalho ativo e não lido)
- Fonte: `%USERPROFILE%\.claude\sessions\<pid>.json`. Campos usados: `pid`, `sessionId`, `cwd`, `kind`, `entrypoint`, `name`, `status`, `updatedAt` e `statusUpdatedAt` (epoch em ms).
- Considera apenas `kind == "interactive"` cujo `pid` esteja vivo (ignora arquivos órfãos).
- Ativo somente se `status == "busy"`. Qualquer outro valor ou um valor desconhecido conta como inativo (fail-closed).
- Leitura defensiva: abrir com `FileShare.ReadWrite | FileShare.Delete`. JSON inválido ou parcial é ignorado sem exceção.
- Metadados opcionais vêm de `%APPDATA%\Claude\claude-code-sessions\**\local_*.json`, casando `cliSessionId == sessionId`. Campos: `title`, `model`, `effort`, `lastFocusedAt`. Use cache por caminho+mtime, porque esses arquivos são grandes.
- Título: `name` → `title` do desktop → último segmento do `cwd` → "Claude". O projeto é resolvido com `ProjectRootResolver`.
- Não lido:
  - Quando o Tracker observa a transição `busy` → não-`busy` da mesma `sessionId`, cria uma `CompletedAgentWork` com `Provider = Claude`.
  - O processo sumir enquanto estava `busy` não conta como conclusão.
  - Remoção da conclusão: clique na linha, "marcar todos como lidos", a mesma sessão voltar a `busy`, ou `lastFocusedAt` do desktop da mesma sessão maior que `CompletedAt`. O último caso é identidade por sessão e não contraria a regra do ERRORS.md sobre o índice global.
- `ActiveAgent` e `CompletedAgentWork` ganham `Provider` (enum `Codex`/`Claude`) com padrão `Codex`. JSON persistido sem o campo precisa desserializar como Codex.
- Lista de agents: as linhas do Claude têm marcador/cor do Claude. Clicar marca como lido e traz a janela do Claude para frente, se houver uma. Não inventar deep link.

### Foco da janela
- Generalize `CodexDesktopWindowMonitor` para identificar o app em primeiro plano (`Codex`, `Claude` ou nenhum).
- O Claude é `Claude.exe` com caminho contendo `\WindowsApps\Claude_` (MSIX) ou `\AnthropicClaude\` (Squirrel).
- Excluir o CLI `%APPDATA%\Claude\claude-code\*\claude.exe`.

### Cota do Claude via OAuth (integração própria do Tracker)
- **Nunca ler nem escrever `~/.claude/.credentials.json`.** O Tracker faz o próprio login e guarda os próprios tokens.
- Constantes (as mesmas do Claude Code, em um único arquivo):
  - `ClientId = 9d1c250a-e61b-44d9-88ed-5944d1962f5e`
  - `AuthorizeUrl = https://claude.com/cai/oauth/authorize`
  - `TokenUrl = https://platform.claude.com/v1/oauth/token`
  - `ManualRedirect = https://platform.claude.com/oauth/code/callback`
  - `UsageUrl = https://api.anthropic.com/api/oauth/usage`
  - `Scopes = "user:profile user:inference"`
- Fluxo automático:
  - Botão **Conectar Claude** nas configurações.
  - PKCE S256 com `code_verifier` de 32 bytes aleatórios em base64url, além de `state` aleatório.
  - `HttpListener` em `http://localhost:{porta livre}/callback`.
  - Abre o navegador com os parâmetros `code=true`, `client_id`, `response_type=code`, `redirect_uri`, `scope`, `code_challenge`, `code_challenge_method=S256` e `state`.
  - Valida o `state`. Timeout de 5 min, com opção de cancelar. Responde ao navegador com uma página simples dizendo que a janela pode ser fechada.
- Fluxo manual (fallback): link **Conectar com código** usa `redirect_uri = ManualRedirect`. O usuário cola `code#state` em um campo; o `state` é validado.
- Troca do código: `POST TokenUrl` com JSON `{grant_type:"authorization_code", code, redirect_uri, client_id, code_verifier, state}`. A resposta traz `access_token`, `refresh_token` e `expires_in`.
- Refresh: quando faltarem menos de 5 min para expirar, ou em resposta 401, envia `{grant_type:"refresh_token", refresh_token, client_id}` e persiste o novo `refresh_token` de forma atômica. Em `invalid_grant`, 400 ou 401 após o refresh, marca como desconectado e o status passa a "Reconectar Claude".
- Armazenamento: arquivo ao lado das settings, cifrado com DPAPI `ProtectedData` (`CurrentUser`). Escrita atômica (tmp + replace). "Desconectar" apaga o arquivo.
- Uso:
  - `GET UsageUrl` com os headers `Authorization: Bearer`, `anthropic-beta: oauth-2025-04-20` e `User-Agent: aq-tracker/<versão>`.
  - Interpretar `five_hour` e `seven_day` como `{utilization: número 0–100 ou null, resets_at: string ISO ou null}`. Ignorar as demais chaves.
  - Mapear para `QuotaWindow` com ids `claude:five_hour` (300 min) e `claude:seven_day` (10080 min).
- Polling: a cada 60 s junto com o refresh atual, e imediatamente quando o perfil Claude ficar engajado e o último dado tiver mais de 60 s. Em 429 ou 5xx, backoff exponencial até 10 min.
- Guardar o último snapshot do Claude para exibir ao reiniciar o app, com o estado "desatualizado".
- Nunca registrar tokens, códigos nem headers em log (`SanitizedLogger`). Timeouts HTTP de 20 s.

### Configurações
- Seção **Claude** nas configurações, com:
  - status: Conectado / Desconectado / Reconectar;
  - **Conectar Claude**, **Conectar com código** e **Desconectar**;
  - toggle **Perfil Claude** (padrão ligado);
  - cor do Claude (mesmo seletor do accent).
- Novos campos em `AppSettings` com defaults retrocompatíveis; settings antigas carregam sem erro.
- Todos os textos em pt-BR e en-US no `LocalizationManager`.

## Restrições
- Somente `net48` e as APIs já usadas no projeto. Sem pacotes NuGet novos (DPAPI vem da referência de framework `System.Security`).
- Siga o estilo do código ao redor. Lógica pura em `AqTracker.Core`, testável sem UI.
- Não altere o comportamento atual do Codex, exceto o que está descrito acima.
- `VERSION` = `0.22.0`. Adicionar seção `## [0.22.0]` no `CHANGELOG.md` (Keep a Changelog). Atualizar o README (o que faz / dados e privacidade). Registrar no `ERRORS.md` os erros não triviais resolvidos.
- Não fazer commit nem push, não rodar o instalador, não instalar.

## Testes obrigatórios (em `tests/AqTracker.Tests/Program.cs`, no padrão atual)
1. Matriz da regra de engajamento e exibição: só Codex, só Claude, os dois, nenhum com widget ativo; foco + trabalho; foco + não lido; minimizado.
2. Leitor de sessões com diretório de exemplo:
   - `busy`/`idle`/desconhecido;
   - `pid` morto;
   - JSON inválido;
   - arquivo aberto para escrita;
   - `kind` diferente de interactive;
   - casamento com os metadados do desktop e cadeia de fallback do título.
3. Transições de não lido: busy→idle cria a conclusão; processo some enquanto busy não cria; voltar a busy remove; `lastFocusedAt` posterior remove; `lastFocusedAt` anterior mantém.
4. `CompletedAgentWork` persistido sem `Provider` desserializa como Codex.
5. Detecção de executável: Codex continua igual; Claude em MSIX e em Squirrel; CLI do Claude excluído.
6. OAuth com `HttpMessageHandler` falso:
   - URL de autorização e `code_challenge` corretos;
   - troca de código;
   - refresh antes de expirar e refresh após 401;
   - `invalid_grant` leva a desconectado;
   - rotação do refresh token persistida;
   - parse de `code#state` e rejeição de `state` divergente;
   - round-trip do armazenamento DPAPI.
7. Parser de uso: valores normais, `null`, chaves ausentes, `resets_at` inválido.
8. Todos os testes existentes continuam passando.

## Critério de aceite
- `dotnet build .\AqTracker.sln` sem erros e sem warnings novos.
- `dotnet run --project .\tests\AqTracker.Tests\AqTracker.Tests.csproj` 100% verde.
- Ao final, entregar um resumo listando os arquivos alterados, as decisões tomadas e qualquer ponto não implementado.
