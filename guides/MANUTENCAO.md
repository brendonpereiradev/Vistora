# Guia de manutenção do Vistora

## Configuração técnica

As configurações ficam em `%LOCALAPPDATA%\Vistora\settings.json`. Feche o aplicativo antes de editar o arquivo e conserve os demais campos.

| Campo | Uso | Valor inicial |
|---|---|---|
| `JiraUrl` | Endereço principal do Jira. | Endereço fictício em `jira.example.com`. |
| `PortalUrl` | Formulário de abertura da visita. | Endereço fictício do portal. |
| `QueueUrl` | Fila de atendimento. | Endereço fictício da fila. |
| `ClosingTeam` | Equipe informada e conferida no fechamento. | `Field Services`. |
| `TimeoutSeconds` | Tempo máximo de espera por etapa. | 60 segundos; valores aceitos entre 10 e 180. |
| `LoginTimeoutMinutes` | Tempo de espera pela autenticação. | 15 minutos. |
| `SelectorOverrides` | Seletores de controles para o ambiente configurado. | Mapa vazio. |

Novas visitas guardam uma cópia das configurações. Uma retomada usa os valores salvos na visita original.

## Compatibilidade com o Jira

O adaptador utiliza Playwright com o canal `msedge`, em navegador visível e com uma sessão própria do Vistora. Os endereços do Jira, do formulário e da fila precisam pertencer ao mesmo servidor HTTPS.

O fluxo implementado utiliza as transições **Iniciar Atendimento** e **Transitar para Fechado**, com os estados **Aguardando N2** e **Em andamento N2**. O fechamento preenche a resolução, seleciona o comentário público e informa a equipe configurada. Antes de usar outro ambiente, confira se seus formulários, estados e permissões correspondem a esse fluxo.

O aplicativo identifica os controles por rótulos, funções e seletores. Quando um controle é ambíguo ou o resultado diverge da visita, a execução requer conferência.

### Ajustar seletores

`SelectorOverrides` permite definir seletores CSS para controles específicos. O exemplo abaixo demonstra o formato; os seletores precisam ser confirmados na interface do Jira utilizado:

```json
{
  "SelectorOverrides": {
    "portal.description": "#description",
    "close.publicComment": "#comment"
  }
}
```

As chaves usadas pelo adaptador estão em `src/Vistora.Automation.Edge/EdgeJiraAutomation.cs` e `DomControls.cs` no repositório. Controles de fechamento são procurados dentro do modal. A leitura do comentário deve identificar conteúdo comprovadamente público e conferir o texto esperado.

## Persistência e retomada

O motor salva a intenção de uma operação antes de enviá-la ao Jira. Após a leitura do resultado, registra a etapa confirmada. Arquivos JSON existentes recebem uma cópia `.bak` ao serem substituídos.

Os arquivos em `runs/` controlam a retomada. Os diagnósticos registram o que foi observado em cada tentativa. Alterar ou remover logs não corrige o progresso de uma visita.

Uma abertura sem número confirmado exige vinculação e conferência do chamado existente. Um fechamento pendente pode exigir confirmação do operador. Os procedimentos de conferência estão no [guia de uso](USO.md).

## Registros e exportação

Cada tentativa recebe eventos, resumo e informações do ambiente em `diagnostics/<id-da-visita>/attempts/<id-da-tentativa>/`. Os eventos incluem etapas, duração, confirmações e causas técnicas das falhas. Os registros do aplicativo ficam em `logs/`.

O ZIP exportado pelo Histórico reúne:

| Arquivo | Conteúdo |
|---|---|
| `resumo.txt` e `resumo.json` | Resultado consolidado dos registros disponíveis. |
| `attempts/<id>/events.jsonl` | Eventos da tentativa em ordem de sequência. |
| `attempts/<id>/summary.json` | Resultado, tempos e avisos da tentativa. |
| `attempts/<id>/environment.json` | Ambiente observado na tentativa. |
| `app.jsonl` | Eventos do aplicativo relacionados à exportação. |
| `manifest.json` | Escopo, versões, arquivos e checksums SHA-256. |
| `LEIA-ME.txt` | Orientações para analisar o pacote. |

Na exportação de uma abertura do aplicativo, as informações do ambiente ficam em `environment.json` na raiz. Capturas são incluídas apenas quando escolhidas pelo operador. Perfis e configurações completos, anexos e arquivos de sessão ficam fora do ZIP.

Datas dos registros usam UTC; durações usam milissegundos. O manifesto identifica exportações parciais e registros ausentes. Tentativas antigas sem diagnóstico conservam seu progresso, mas não recebem métricas retroativas.

### Rotação e retenção

O grupo `Diagnostics` de `settings.json` aceita:

| Campo | Valor inicial |
|---|---|
| `RotationMegabytes` | 10 MB por arquivo de eventos. |
| `RetentionDays` | 30 dias. |
| `MaxStorageMegabytes` | 200 MB para logs e diagnósticos. |

O nível de registro é `Debug`, inclusive quando um arquivo antigo indica outro nível. A limpeza automática preserva os diagnósticos de visitas pendentes e a sessão atual. Os arquivos de progresso em `runs/` não fazem parte dessa limpeza. Se o orçamento for atingido, o aplicativo avisa e limita detalhes e capturas.

## Compilação e testes

Execute os scripts na raiz do repositório:

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
```

`Vistora.Tests` verifica validação, persistência, retomada, resultados incertos e diagnóstico. `Vistora.DesktopTests` confere seleção de pendências, bloqueios e limpeza pela interface. `Vistora.BrowserTests` utiliza o adaptador real do Edge com formulários locais controlados.

Para executar regras, persistência e interface sem abrir o Edge:

```powershell
.\scripts\Test.ps1 -SkipBrowser
```

Os testes são projetos executáveis acionados por `Test.ps1`. A aprovação dessas verificações confirma o comportamento nos cenários cobertos; a compatibilidade de um Jira específico precisa ser conferida nesse ambiente.

## Capturas para documentação

O modo de prévia utiliza dados fictícios e armazenamento temporário isolado. Para gerar uma imagem da interface após compilar:

```powershell
. .\scripts\Common.ps1
& (Get-VistoraDotnet) run --project .\src\Vistora.Desktop -c Release --no-build -- --preview .\output\executar-visita.png --preview-pending
& (Get-VistoraDotnet) run --project .\src\Vistora.Desktop -c Release --no-build -- --preview .\output\historico.png --preview-history --preview-pending
```

Esse modo renderiza a interface, salva a imagem e encerra o aplicativo. A prévia não envia operações ao Jira.

## Publicação

`scripts/Publish.ps1` lê a versão do projeto Desktop e gera a aplicação para Windows x64 com o runtime .NET incluído. O pacote conserva `.playwright`, os modelos de resolução, as capturas públicas, os guias e a licença.

O README é copiado como `COMO-USAR.md`. As imagens e os guias mantêm seus caminhos relativos no pacote. Para repetir uma publicação sem substituir os arquivos existentes, informe uma pasta nova:

```powershell
.\scripts\Publish.ps1 -OutputDirectory .\output\nova-publicacao
```
