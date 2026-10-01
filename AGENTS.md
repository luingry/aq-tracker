# Versionamento

- `VERSION` é a fonte única da versão do produto. Use SemVer sem o prefixo `v` (por exemplo, `0.5.0`).
- Em toda alteração entregue, faça o bump antes de finalizar: `MAJOR` para quebra incompatível, `MINOR` para funcionalidade retrocompatível e `PATCH` para correção retrocompatível.
- Na mesma alteração, registre o novo número e as mudanças em `CHANGELOG.md`, no formato Keep a Changelog.
- Não duplique a versão em projetos ou no instalador: `Directory.Build.props` e `installer/AqTracker.iss` a leem de `VERSION`.
- Valide com `dotnet build .\AqTracker.sln` e `dotnet run --project .\tests\AqTracker.Tests\AqTracker.Tests.csproj`.
- A release é disparada apenas por push para `main` que altere `VERSION` ou `CHANGELOG.md`; a seção do changelog deve corresponder exatamente à versão atual. O workflow publica o setup e conserva somente a release e a tag mais recentes.

## Entrega local obrigatória

- Para toda implementação concluída, registre e aplique a versão alvo seguindo as regras de versionamento existentes.
- O resumo final deve incluir explicitamente `Versão esperada: X.Y.Z`.
- Execute `dotnet build .\AqTracker.sln` e os testes aplicáveis antes de encerrar.
- Gere/atualize o instalador com `./scripts/finalize-build.ps1` (use `-SkipTests` apenas quando um problema conhecido não relacionado bloquear, e somente após as tentativas aplicáveis de testes).
- Instale nesta máquina o `artifacts/AqTracker-latest.exe` gerado.
- Após a instalação, inicie o executável instalado (não o da árvore de build) e confirme que o processo em execução usa o caminho de instalação e a mesma versão de `VERSION`.
- Verifique que o executável instalado e o `DisplayVersion` de registro do uninstall bateram com `VERSION`.
- Não declare implementação como concluída sem sucesso em build, instalador, instalação local, execução do executável instalado com verificação de caminho e versão em execução, e verificação de versão, salvo bloqueio genuíno reportado.
