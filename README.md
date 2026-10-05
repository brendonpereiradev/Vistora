# Vistora

Aplicativo para Windows que automatiza visitas preventivas no Jira pela interface do Microsoft Edge, sem usar a API do Jira.

Cada perfil reúne os dados de um solicitante, uma unidade, seus pavimentos e os textos de resolução. O aplicativo abre um chamado por pavimento, atribui ao técnico conectado, inicia o atendimento e realiza o fechamento com resolução e comentário público.

Versão atual: **1.0.0**.

## Como executar

Baixe `Vistora-1.0.0-Windows-x64.zip` na [release mais recente](https://github.com/brendonpereiradev/Vistora/releases/latest), extraia todo o conteúdo para uma pasta e execute `Vistora.exe`. Mantenha os arquivos extraídos junto ao executável, incluindo a pasta `.playwright`.

O pacote é para Windows x64 e inclui o runtime .NET. O Microsoft Edge precisa estar instalado. Nas **Configurações**, informe os endereços do Jira; em **Perfis**, cadastre a unidade, os dados do solicitante, os pavimentos e os textos de resolução. Entre na sua conta pela janela do Edge antes de executar a primeira visita.

A validação desta release foi realizada no Windows 10 x64, com testes de automação em páginas locais do Edge. A execução no Windows 11 e o ciclo completo de abertura e fechamento no Jira real ainda precisam de validação nesses ambientes. O fluxo e as permissões do Jira devem corresponder aos configurados no aplicativo.

## Código fonte

Este repositório contém o código fonte, testes, scripts de compilação e modelos genéricos necessários ao aplicativo. Configurações reais, históricos, sessões do navegador, anexos, capturas e arquivos compilados ficam fora do versionamento.

## Compilar e testar

Requisitos: Windows, SDK .NET 10 e Microsoft Edge. Os scripts são executados a partir da raiz do projeto no PowerShell.

```powershell
# Instalar o SDK somente na pasta do projeto, se necessário
.\scripts\Bootstrap.ps1

# Compilar
.\scripts\Build.ps1

# Testar regras, persistência, interface e navegador com páginas locais controladas
.\scripts\Test.ps1

# Gerar um aplicativo com o runtime incluído
.\scripts\Publish.ps1
```

Os testes de navegador abrem o Edge em um ambiente local de teste. Para executar os testes de regras, persistência e interface sem abrir o navegador, use `.\scripts\Test.ps1 -SkipBrowser`.

A publicação gera o aplicativo e o ZIP `Vistora-1.0.0-Windows-x64.zip` em `output/release/1.0.0`. Esses arquivos não entram no Git. O pacote inteiro deve ser mantido junto ao executável, incluindo a pasta `.playwright`.

O script recusa uma pasta de aplicativo com arquivos ou um ZIP já existente. Para gerar outro pacote sem misturar publicações, informe uma pasta nova, por exemplo `.\scripts\Publish.ps1 -OutputDirectory .\output\release\1.0.0\nova-validacao`.

## Configuração local

Os endereços padrão e os modelos incluídos são exemplos fictícios. Configure os endereços reais do Jira nas configurações do aplicativo, preencha o perfil da unidade e ajuste os pavimentos e suas resoluções antes de executar.

Os dados são salvos em `%LOCALAPPDATA%\Vistora`. O login ocorre na janela do Edge e a sessão permanece nesse diretório. A equipe de fechamento é definida por `ClosingTeam` em `settings.json`; o valor de exemplo é `Field Services`. Uma execução guarda uma cópia das configurações e dos textos utilizados.

A aba **Executar visita** concentra a execução e a retomada. Ao escolher um perfil, sua visita pendente é selecionada quando não há ambiguidade. O campo **Visita pendente** permite escolher entre várias visitas, incluindo as de perfis excluídos. A retomada usa os dados originais da visita, preserva os chamados já registrados e confere seu estado antes das próximas ações; os pavimentos dessa visita ficam disponíveis para consulta.

Se uma abertura foi enviada sem identificação do número, a retomada solicita o chamado já criado e confere seus dados no Jira antes de vinculá-lo. Cancelar ou informar um chamado divergente impede a continuação, evitando novas aberturas automáticas. O histórico mostra os resultados e permite exportar logs; as ações de execução ficam na tela principal.

O fechamento aguarda o término das requisições de gravação, a saída do formulário e o status final antes de recarregar e conferir o chamado. Se um fechamento anterior ficou pendente e o chamado ainda está em atendimento com o técnico conectado, a retomada pede uma conferência explícita na janela do Edge. Autorize uma nova tentativa somente se a resolução e o comentário público ainda não foram enviados. Se já foram enviados, escolha **Não** e conclua o fechamento no Jira; a próxima retomada confere o resultado sem repetir o comentário. Chamados já fechados são sempre conferidos sem novo envio.

O fluxo e os seletores precisam corresponder à interface e às permissões do Jira configurado. `SelectorOverrides` permite ajustar a identificação dos controles nos dados locais.

No **Histórico**, o botão **Limpar histórico e pendências** remove todas as visitas locais de todas as unidades, inclusive as pendentes e as de perfis excluídos, seus backups, logs e capturas. A confirmação mostra as quantidades e tem **Cancelar** como opção padrão. A limpeza é permanente: as visitas removidas deixam de permitir retomada pelo Vistora. Os chamados no Jira, perfis, configurações, login do Edge, logs gerais do aplicativo e ZIPs já exportados são preservados. O botão fica bloqueado durante carregamento, execução, verificação de acesso, exportação ou outra limpeza. Se houver falha, a tela mostra os registros restantes e permite repetir a operação.

## Logs e exportação para análise

Cada execução e retomada registra uma linha do tempo com etapas, resultados conferidos, tempos, causas técnicas das falhas e detalhes de investigação sempre ativados. O **Histórico** mostra os resultados e o progresso nas tabelas e concentra o botão **Exportar log**, inclusive para visitas bem-sucedidas.

Para analisar o comportamento em diferentes computadores:

1. Execute o Vistora no computador de teste.
2. No **Histórico**, selecione a visita e clique em **Exportar log**.
3. Informe, se desejar, um rótulo como “Teste A”, escolha se quer incluir capturas e salve o ZIP fora da pasta de dados do Vistora.
4. Anexe o ZIP na conversa para análise. Repita em outra máquina para comparar os resultados.

O pacote contém `resumo.txt`, `resumo.json`, os eventos de todas as tentativas disponíveis, informações do ambiente de origem e `manifest.json` com versões e checksums dos arquivos. Credenciais, sessão do navegador, perfis completos, configurações completas e anexos ficam fora do pacote. Números dos chamados e identificadores técnicos são conservados. Capturas ficam desmarcadas por padrão e podem conter dados visíveis do chamado.

Sem uma visita selecionada no **Histórico**, o mesmo botão **Exportar log** permite escolher uma abertura do aplicativo para análise. Os detalhes técnicos são registrados desde a inicialização e em todas as tentativas, inclusive retomadas de visitas antigas.

Os logs ficam em `%LOCALAPPDATA%\Vistora\logs`; diagnósticos de visitas ficam em `diagnostics\<id>\attempts\<tentativa>`. Datas dos arquivos estão em UTC. Uma exportação durante a execução registra apenas o progresso observado e identifica o resultado como parcial. Dados antigos sem logs continuam permitindo retomada, mas não recebem métricas retroativas.

Por padrão, os arquivos giram a cada 10 MB, a retenção é de 30 dias e o orçamento de logs/capturas é de 200 MB. Diagnósticos de visitas pendentes e a sessão atual são preservados; os arquivos de progresso nunca são removidos pela limpeza. Um aviso informa quando o diagnóstico fica incompleto ou o orçamento é atingido. Os limites podem ser ajustados no grupo `Diagnostics` de `settings.json` com o aplicativo fechado. O nível de registro permanece em `Debug`, inclusive quando uma configuração antiga indica outro nível.

## Estrutura

- `src/Vistora.Core`: modelos, validação, seleção e execução de visitas.
- `src/Vistora.Infrastructure`: persistência dos dados locais.
- `src/Vistora.Automation.Edge`: automação da interface com Playwright para .NET.
- `src/Vistora.Desktop`: interface WPF.
- `tests`: verificações de regras, persistência, diagnósticos, limpeza, interface e navegador.
- `scripts`: instalação do SDK, compilação, testes e publicação local.
- `assets`: modelos genéricos usados na criação de perfis.
