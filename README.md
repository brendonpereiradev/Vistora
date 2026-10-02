# Vistora

Aplicativo para Windows que automatiza visitas preventivas no Jira pela interface do Microsoft Edge, sem usar a API do Jira.

Cada perfil reúne os dados de um solicitante, uma unidade, seus pavimentos e os textos de resolução. O aplicativo abre um chamado por pavimento, atribui ao técnico conectado, inicia o atendimento e realiza o fechamento com resolução e comentário público.

## Código fonte

Este repositório contém o código fonte, testes, scripts de compilação e modelos genéricos necessários ao aplicativo. Configurações reais, históricos, sessões do navegador, anexos, capturas e arquivos compilados ficam fora do versionamento.

Versão inicial: **0.1.5**.

## Compilar e testar

Requisitos: Windows, SDK .NET 10 e Microsoft Edge. Os scripts são executados a partir da raiz do projeto no PowerShell.

```powershell
# Instalar o SDK somente na pasta do projeto, se necessário
.\scripts\Bootstrap.ps1

# Compilar
.\scripts\Build.ps1

# Testar regras e o navegador com páginas locais controladas
.\scripts\Test.ps1

# Gerar um aplicativo com o runtime incluído
.\scripts\Publish.ps1
```

Os testes de navegador abrem o Edge em um ambiente local de teste. Para executar apenas os testes de regras, use `.\scripts\Test.ps1 -SkipBrowser`.

A publicação gera o aplicativo e um ZIP na pasta `output/release`. Esses arquivos não entram no Git. O pacote inteiro deve ser mantido junto ao executável, incluindo a pasta `.playwright`.

## Configuração local

Os endereços padrão e os modelos incluídos são exemplos fictícios. Configure os endereços reais do Jira nas configurações do aplicativo, preencha o perfil da unidade e ajuste os pavimentos e suas resoluções antes de executar.

Os dados são salvos em `%LOCALAPPDATA%\Vistora`. O login ocorre na janela do Edge e a sessão permanece nesse diretório. A equipe de fechamento é definida por `ClosingTeam` em `settings.json`; o valor de exemplo é `Field Services`. Uma execução guarda uma cópia das configurações e dos textos utilizados.

**Executar** continua a visita pendente do perfil. **Retomar** preserva os chamados já registrados e confere seu estado antes de executar as próximas ações. Uma abertura enviada sem identificação do número exige conferência e vinculação pelo histórico, evitando novas aberturas automáticas.

O fluxo e os seletores precisam corresponder à interface e às permissões do Jira configurado. `SelectorOverrides` permite ajustar a identificação dos controles nos dados locais.

## Estrutura

- `src/Vistora.Core`: modelos, validação, seleção e execução de visitas.
- `src/Vistora.Infrastructure`: persistência dos dados locais.
- `src/Vistora.Automation.Edge`: automação da interface com Playwright para .NET.
- `src/Vistora.Desktop`: interface WPF.
- `tests`: verificações de regras, persistência e navegador.
- `scripts`: instalação do SDK, compilação, testes e publicação local.
- `assets`: modelos genéricos usados na criação de perfis.
