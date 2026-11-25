# 0004. Dados de exemplo na subida

Status: aceita · Incremento 1.3 · 2026-11-24

## Contexto
Sem dados, a API sobe vazia: quem a abre no Scalar, ou vai ver o Blazor no 1.8, precisaria cadastrar tipos, máquinas e pessoas à mão. A demonstração do portfólio (incremento 1.10) também precisa de contas prontas nos três papéis. Esses dados são de exemplo: não podem ir para um ambiente de verdade, e a senha das pessoas não pode ficar no repositório.

## Decisão
- **Ligada por configuração:** `DadosDeExemplo:Aplicar` é `false` por padrão. Fica `true` no `appsettings.Development.json` e no Compose (`DadosDeExemplo__Aplicar`). Os testes de integração a desligam, porque a fábrica de testes roda em `Development`.
- **Roda na subida, depois das migrações e da Coordenação inicial,** numa classe parecida com a `CoordenacaoInicial` (`Dados/DadosDeExemplo.cs`), que recebe o `UserManager` e por isso gera o hash da senha do jeito do Identity.
- **Só cria, nunca altera nem apaga:**
  - os tipos e as máquinas, só se ainda não existir nenhum tipo. Assim, uma máquina renomeada pela Coordenação não volta com o nome antigo;
  - cada pessoa, pelo e-mail, só se ele ainda não tiver conta. Um papel ou uma senha que mudaram depois continuam como estão.
- **O conteúdo:** 4 tipos (Cortadora a laser, 2 h; Impressora 3D FDM, 12 h; Impressora 3D de resina, 8 h; Scanner 3D, 2 h), 6 máquinas (a do Scanner 3D, **inativa**, para mostrar o caso da máquina fora de uso) e três pessoas, uma em cada papel, com e-mails em `@makerspace.example`, um domínio reservado que não recebe e-mail.
- **A senha das pessoas vem de uma variável:** `EXEMPLO_SENHA` no `.env` (`DadosDeExemplo__Senha` na API), a mesma para as três, como já acontece com a `COORDENACAO_SENHA`. Sem ela, os tipos e as máquinas entram e as pessoas não, com um aviso no log. Uma senha fora da política (menos de 15 caracteres) faz a subida falhar com a mensagem. Escolha do Arthur.

## Alternativas consideradas
- **`HasData` na migração:** os dados iriam para a produção junto com o esquema, e a migração não gera o hash da senha.
- **`UseAsyncSeeding` do EF Core 9:** recebe só o `DbContext`, sem o `UserManager`, e então o hash da senha teria de ser refeito à mão.
- **Um script SQL:** teria o hash da senha escrito no arquivo, e o formato do hash é detalhe do Identity.
- **Senha fixa no código:** uma credencial conhecida por todo mundo no repositório.
- **Senha gerada e escrita no log:** o segredo ficaria no log e mudaria a cada banco novo.
- **Uma variável por pessoa:** três itens a mais no `.env`, sem ganho para uma demonstração.

## Consequências
- Um ambiente novo sobe pronto para a demonstração, e subir de novo não duplica nem desfaz nada.
- O Compose cria no banco local pessoas com senha conhecida por quem tem o `.env`. Por isso a opção não vem ligada em nenhum outro lugar, e o deploy (1.10) a liga de propósito, com a senha vinda das variáveis do serviço.
- **Pior:** quem define `EXEMPLO_SENHA` e esquece a opção ligada num ambiente real cria três contas de exemplo, uma delas Coordenação. É por isso que o padrão é desligado.
- **Pior:** a semente é um código a mais para manter. O 1.4 e o 1.5 vão acrescentar treinamentos e reservas à mesma classe, e o 1.10 reaproveita as pessoas como contas de demonstração.
