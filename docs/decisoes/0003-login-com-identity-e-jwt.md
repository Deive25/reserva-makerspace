# 0003. Login com Identity e JWT

Status: aceita · Incremento 1.2 · 2026-11-24

## Contexto
A API precisa de cadastro, login e três papéis cumulativos (Usuário, Equipe e Coordenação). O Blazor do incremento 1.8 vai consumir a API, e o login com refresh token já aparece em outro projeto do portfólio.

## Decisão
- **ASP.NET Core Identity** guarda usuários e hash de senha (`AddIdentityCore`, sem cookies). A senha tem 15 caracteres ou mais, sem regras de composição (NIST SP 800-63B).
- **O papel é um campo do usuário** (texto: `Usuario`, `Equipe`, `Coordenacao`), e não as tabelas de papéis do Identity. Cada pessoa tem exatamente um papel, e o esquema garante isso. Escolha do Arthur.
- **O papel é lido do banco a cada pedido.** O JWT HS256 de 60 min, sem refresh, só prova quem é a pessoa (`sub`, `email`, `name`). Promover ou rebaixar vale no pedido seguinte, e o token de uma conta apagada dá 401. Escolha do Arthur.
- **Seguro por padrão:** a `FallbackPolicy` exige login em toda rota, e só as públicas são marcadas (`/auth/*`, `/saude`, OpenAPI e Scalar). Políticas cumulativas: `Equipe` aceita Equipe ou Coordenação, e `Coordenacao` só a Coordenação.
- **A última Coordenação não perde o papel:** a troca roda numa transação que trava as linhas das Coordenações (`SELECT … FOR UPDATE`, em ordem de id) antes de contar.
- **A primeira Coordenação** vem de variáveis de ambiente, na subida, se não existir nenhuma. Se o e-mail já tem conta, a subida falha, em vez de promover quem chegou primeiro.
- Nomes em snake_case no banco, o padrão do PostgreSQL.

## Alternativas consideradas
- **Tabelas de papéis do Identity:** servem a N papéis independentes por pessoa, que não é o caso, e o esquema deixaria dar dois papéis à mesma pessoa.
- **Papel dentro do token:** evita a consulta, mas uma Coordenação rebaixada continuaria agindo como Coordenação (e podendo se promover de novo) até o token vencer.
- **`MapIdentityApi`:** os tokens dele são opacos, traz refresh, e as rotas e mensagens são em inglês.
- **Refresh token:** o outro projeto já mostra esse fluxo, e aqui só acrescentaria código.

## Consequências
- Mudar o papel é imediato, e o rebaixamento não deixa brecha.
- **Pior:** uma consulta ao banco por pedido autenticado. É uma leitura pela chave primária, aceitável neste tamanho.
- **Pior:** sem limite de tentativas de login até o incremento 1.10 (o *rate limiter* por IP). O bloqueio de conta do Identity foi descartado, porque deixaria qualquer visitante travar as contas de demonstração.
- **Pior:** sem refresh, a pessoa entra de novo a cada 60 min.
- **Pior:** a trava de linhas deixa duas trocas de papel simultâneas em fila (raras, e só da Coordenação).
