# Guia de uso do Vistora

## Abrir o aplicativo

Extraia o ZIP da distribuição para uma pasta e abra `Vistora.exe`. Conserve todos os arquivos do pacote, incluindo `.playwright`, `assets` e `guides`. O aplicativo utiliza o Microsoft Edge instalado no computador.

## Configurar o Jira

Em **Configurações**, preencha:

| Campo | Endereço esperado |
|---|---|
| Endereço do Jira | Endereço principal do servidor. |
| Endereço do formulário de visita preventiva | Página usada para abrir a solicitação no portal. |
| Endereço da fila de atendimento | Fila de chamados do ambiente. |

Os três endereços devem usar HTTPS e pertencer ao mesmo servidor. Clique em **Salvar configurações**. Os endereços iniciais são fictícios.

A conta do técnico precisa ter acesso ao formulário e às ações de atribuição, atendimento e fechamento. O fluxo da automação deve corresponder aos campos e às transições disponíveis nesse Jira.

## Cadastrar uma unidade

Em **Perfis e pavimentos**, selecione o perfil inicial e clique em **Editar**, ou use **Novo perfil** para cadastrar outra unidade.

Na aba **Dados do formulário**, informe o nome do perfil, o nome da unidade no Jira e os dados do solicitante. Preencha também o nome completo do usuário, o ramal, o título e os detalhes da solicitação. Telefone e anexos são opcionais. Os anexos informados serão enviados em cada chamado da visita.

Na aba **Pavimentos e resoluções**, ajuste o nome do pavimento, o setor e a sala. Selecione uma linha para editar a resolução correspondente. Esse texto será utilizado na resolução do chamado e no comentário público.

Use **Adicionar pavimento**, **Remover**, **Subir** e **Descer** para organizar a lista. Os textos iniciais são modelos genéricos: revise-os para descrever a visita realizada. Clique em **Salvar perfil** ao terminar. O perfil pode ser salvo durante seu preenchimento; antes da execução, o aplicativo valida os campos obrigatórios dos pavimentos selecionados.

## Verificar o acesso

Na tela **Executar visita**, clique em **Verificar acesso ao Edge**. Faça login na janela do navegador quando solicitado. Essa ação confere o acesso ao formulário, sem enviar a abertura de uma visita.

O técnico responsável será a conta conectada no Edge. O solicitante dos chamados será a pessoa cadastrada no perfil. Trocar o perfil da unidade mantém a conta do técnico. Para conectar outra conta, encerre a sessão no Jira pela janela do Edge e faça o novo login.

## Executar uma visita

1. Escolha o perfil em **Executar visita**.
2. Marque os pavimentos que deseja registrar.
3. Clique em **Executar visita preventiva**.
4. Faça login no Edge se a sessão precisar de autenticação.
5. Acompanhe as etapas e os números dos chamados na tela.

O Vistora abre os chamados dos pavimentos selecionados. Depois, confere a atribuição ao técnico, inicia o atendimento e fecha os chamados com seus respectivos textos. Uma visita só é concluída após a conferência do status, da resolução, do comentário público e da equipe de fechamento.

Em **Histórico**, selecione uma visita para consultar o resultado e as etapas de cada chamado.

## Parar e retomar

Clique em **Parar execução** para solicitar a interrupção. Se uma ação já tiver sido enviada ao Jira, o aplicativo aguarda seu resultado antes de parar. O progresso fica salvo localmente.

Para continuar, volte a **Executar visita**. O aplicativo seleciona uma pendência do perfil quando a escolha é inequívoca. Havendo várias pendências, escolha a desejada em **Visita pendente** e clique em **Retomar visita preventiva**. Também é possível selecionar visitas de perfis excluídos.

Os dados de uma visita pendente permanecem iguais aos utilizados na sua criação. Alterações posteriores no perfil e nas configurações serão utilizadas em novas visitas. Os pavimentos da pendência ficam disponíveis para consulta, e os chamados conhecidos são conferidos antes da continuação.

### Abertura sem número confirmado

Se a abertura foi enviada, mas o Vistora não identificou o número do chamado, consulte a fila do Jira. Ao retomar, informe o número do chamado já criado, como `SD-123456`, quando solicitado.

O aplicativo confere os dados do chamado antes de vinculá-lo ao pavimento. Cancelar a solicitação ou informar um chamado divergente impede a continuação. Confira o chamado existente antes de tentar novamente.

### Fechamento sem confirmação

Se um fechamento anterior ficou pendente e o chamado ainda está em atendimento com o técnico conectado, a retomada pede uma conferência na janela do Edge.

- Se a resolução e o comentário público ainda não foram enviados, confirme a nova tentativa após verificar o chamado.
- Se os textos já foram enviados, escolha **Não** e conclua o fechamento no Jira. Depois, retome a visita para conferir o resultado e continuar.

Chamados já fechados são conferidos pelo Vistora sem novo envio de comentário. Se o status, os textos ou a equipe não corresponderem à visita, corrija o chamado no Jira antes de retomar.

## Exportar um diagnóstico

Em **Histórico**, selecione a visita e clique em **Exportar log**. O pacote inclui os registros disponíveis da execução e das retomadas, mesmo quando a visita foi concluída com sucesso.

1. Informe uma identificação opcional para o teste.
2. Marque **Incluir capturas de falha** se essas imagens forem necessárias à análise.
3. Clique em **Escolher onde salvar** e salve o ZIP fora de `%LOCALAPPDATA%\Vistora`.

Capturas ficam desmarcadas por padrão e podem conter os dados visíveis no chamado. Credenciais, sessão do navegador, anexos e perfis ou configurações completos ficam fora do pacote. Números dos chamados e identificadores técnicos são conservados.

Sem uma visita selecionada, o botão **Exportar log** permite escolher uma abertura do aplicativo. Uma exportação durante a execução representa apenas o progresso disponível naquele momento e pode ser identificada como parcial.

## Limpar o histórico

O botão **Limpar histórico e pendências**, no Histórico, remove todas as visitas locais de todas as unidades, inclusive as pendentes e as de perfis excluídos. Também remove os backups das visitas, seus logs e suas capturas.

A confirmação informa as quantidades e oferece **Cancelar** como opção padrão. A operação é permanente: uma visita removida deixa de permitir retomada pelo Vistora. Os chamados no Jira, os perfis, as configurações, a sessão do Edge e os ZIPs já exportados são preservados.

A limpeza fica bloqueada durante carregamento, execução, verificação de acesso e exportação. Se houver uma falha, a tela mostra os registros restantes e permite repetir a operação.

## Dados locais

O aplicativo guarda seus dados em `%LOCALAPPDATA%\Vistora`:

| Caminho | Conteúdo |
|---|---|
| `profiles.json` | Perfis, pavimentos e resoluções. |
| `settings.json` | Endereços do Jira e configurações técnicas. |
| `runs/` | Dados e progresso de cada visita. |
| `browser/` | Sessão do Microsoft Edge utilizada pelo Vistora. |
| `logs/` | Registros de abertura e funcionamento do aplicativo. |
| `diagnostics/` | Eventos, resumos e capturas das tentativas de execução. |
