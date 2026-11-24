# C1: Reserva de Equipamentos de Makerspace (design)

- **Status:** aprovado pelo Arthur em 2026-11-24 (conversa e spec escrita).
- **Stack:** .NET 10 (SDK 10.0.403), ASP.NET Core Web API com controllers, EF Core 10 + Npgsql, PostgreSQL 17, ASP.NET Core Identity + JWT, xUnit, Testcontainers, Blazor WebAssembly, Docker Compose, GitHub Actions.

## 1. Problema e objetivo
Em parques tecnológicos como o Itec Park, onde trabalhei como bolsista, a cortadora a laser, as impressoras 3D (de filamento e de resina) e o scanner 3D ficam ocupados o dia inteiro. Usam essas máquinas os bolsistas, em demandas do parque, e alunos ligados ao parque ou a algum professor, em projetos pessoais. Com o controle em planilha, as reservas conflitam (há mais de uma máquina do mesmo tipo e ninguém sabe qual foi marcada), gente sem treinamento usa as máquinas e a manutenção não bloqueia a agenda.

O C1 é uma API de reservas com regras claras e testadas, mais uma interface em Blazor, publicada de graça com contas de demonstração. No portfólio, é a primeira API .NET pública: prova ASP.NET Core, EF Core com migrations, PostgreSQL, JWT com papéis, regras de negócio testáveis e testes de integração com banco real.

**Anti-objetivos** (falhas mesmo que "funcione"):
- duas reservas ativas sobrepostas na mesma máquina, mesmo com dois pedidos chegando juntos;
- alguém sem treinamento válido conseguir reservar;
- uma reserva sumir sem motivo registrado;
- segredo no repositório;
- qualquer custo mensal.

## 2. Escolhas do Arthur (2026-11-24)
| Tema | Escolha |
|---|---|
| Uso real | O aluno opera a máquina, ou pede a um bolsista e o pedido vira demanda do parque. A reserva é sempre feita por alguém treinado |
| Prioridade | Mistura: por padrão, quem marca primeiro fica com o horário, e o projeto pessoal tem limites menores; a Equipe pode desmarcar uma reserva pessoal para encaixar uma demanda do parque, com travas (§4.3) |
| Frontend | Blazor (WebAssembly), depois da API completa |
| Deploy | Gratuito no fim, com contas de demonstração por papel |
| Repositório | `reserva-makerspace`, código do domínio e documentos em português |
| Estrutura | Domínio separado: Dominio, Contratos, Api e Web, mais os projetos de teste de unidade e de integração |
| Ordem | API inteira (1.1–1.7), depois o Blazor (1.8–1.9) e o deploy (1.10) |

As regras das §3 e §4 foram propostas na conversa e o Arthur não as contestou. Esta leitura da spec é a confirmação final.

## 3. Abordagens consideradas
**Estrutura da solução:**
- **Domínio separado (escolhida).** As regras ficam num projeto sem banco nem web, com testes unitários rápidos. Os DTOs ficam num projeto compartilhado com o Blazor: é o mesmo tipo C# no servidor e no navegador.
- **Clean Architecture completa** (Dominio, Aplicacao, Infraestrutura, Api). Neste tamanho de domínio, várias camadas só repassariam chamadas.
- **API num projeto só.** É a mais simples, mas mistura as regras com o EF Core e dificulta os testes unitários.

**Prioridade entre demandas:** "quem marca primeiro" com limites no pessoal; aprovação de reserva pessoal; e a demanda do parque passando na frente. O Arthur escolheu misturar a primeira com a terceira. A aprovação ficou de fora, porque traria um fluxo de estados e telas de aprovação.

**Garantia contra sobreposição:** só a regra em C# não segura dois pedidos simultâneos. Uma trava na aplicação não vale com mais de uma instância. Por isso a regra fica em C# (mensagem clara) **e** numa restrição de exclusão do PostgreSQL (garantia final).

**Frontend:** Blazor WebAssembly *standalone* (arquivos estáticos no Cloudflare Pages, como o front do J1). O Blazor Server ficaria ruim no Render gratuito: o servidor dorme e a tela depende de uma conexão sempre aberta.

## 4. Domínio e regras
### 4.1 Conceitos
- **Tipo de equipamento** (ex.: Cortadora a laser): nome único e **duração máxima da reserva pessoal** (ex.: 2 h na cortadora, 12 h na FDM), configurada pela Coordenação. O treinamento vale para o tipo.
- **Equipamento:** cada máquina física, com nome único ("Impressora FDM 2"), tipo e `ativo`. Máquina inativa não aceita reserva nova (a de resina e o scanner que "às vezes" estão disponíveis). Desativar não apaga o histórico.
- **Treinamento:** pessoa × tipo, data em que foi feito, validade de **12 meses** e quem registrou. Pode ser revogado. Renovar é registrar um novo, e o histórico fica.
- **Reserva:** máquina, intervalo `[início, fim)`, **operador** (quem cria a reserva é quem opera), finalidade (`ProjetoPessoal` ou `DemandaDoParque`), `solicitante` (texto, só na demanda do parque, quando um aluno pediu ao bolsista), descrição opcional e situação (`Ativa`, `CanceladaPeloDono`, `CanceladaPelaEquipe`, `CanceladaPorManutencao`), com motivo, data e autor do cancelamento.
- **Manutenção:** máquina, intervalo, motivo e quem registrou.

Os horários trafegam e são gravados em UTC (`DateTimeOffset`, `timestamptz`). O Blazor mostra no fuso `America/Sao_Paulo`. Os intervalos são semiabertos: uma reserva que acaba às 10:00 não conflita com outra que começa às 10:00.

### 4.2 Criar uma reserva
1. `início < fim` e `início ≥ agora`.
2. A máquina existe e está ativa.
3. O operador tem treinamento válido e não revogado no tipo da máquina **até o fim da reserva**, e não só no dia em que reserva.
4. Não há outra reserva ativa nem manutenção na máquina no intervalo.
5. **Projeto pessoal:** duração ≤ a máxima do tipo; início em até 7 dias; no máximo 2 reservas pessoais ativas e futuras (fim > agora) por pessoa.
6. **Demanda do parque:** só Equipe ou Coordenação; início em até 30 dias; sem duração máxima.
7. O papel Usuário só cria projeto pessoal.

O dono cancela a própria reserva ativa enquanto ela não começou (`CanceladaPeloDono`).

### 4.3 Desmarcação para uma demanda do parque
Ao criar uma demanda do parque que conflita **só** com reservas pessoais, a Equipe pode mandar um `motivoDesmarcacao`. Na mesma transação, o sistema cancela as pessoais (`CanceladaPelaEquipe`, com o motivo) e cria a demanda. Travas:
- toda reserva pessoal desmarcada precisa começar daqui a **mais de 24 h**;
- se houver **outra máquina ativa do mesmo tipo livre** no intervalo inteiro, a desmarcação é recusada e a resposta sugere essa máquina;
- uma demanda do parque nunca desmarca outra, e manutenção não se desmarca;
- o motivo é obrigatório. Sem ele, o conflito volta como 409 com a lista das reservas em conflito, para a tela perguntar.

O dono vê a reserva cancelada e o motivo em "minhas reservas". Aviso por e-mail está fora do escopo.

### 4.4 Manutenção
- A Equipe registra uma manutenção com `fim > início` e `início ≥ agora`. Ela **cancela toda reserva ativa** que cruze o intervalo, inclusive em andamento e de qualquer finalidade (`CanceladaPorManutencao`, com o motivo), e bloqueia reservas novas.
- Uma manutenção não se sobrepõe a outra na mesma máquina (409).
- **Encerrar:** em andamento, o fim passa a ser agora; se ainda não começou, ela é cancelada. As reservas canceladas não voltam.
- Reserva e manutenção da mesma máquina são serializadas por uma trava na linha da máquina (`SELECT … FOR UPDATE`) dentro da transação. A restrição de exclusão continua como garantia final entre reservas.

### 4.5 Papéis
Cada pessoa tem um papel, e os papéis são cumulativos (a Coordenação faz tudo o que a Equipe faz, e a Equipe tudo o que o Usuário faz).
| Papel | Pode |
|---|---|
| Usuário | Ver máquinas, agenda e disponibilidade; reservar como projeto pessoal; ver e cancelar as próprias reservas; ver os próprios treinamentos |
| Equipe | Demandas do parque e desmarcação; registrar e revogar treinamentos; registrar e encerrar manutenções; ver as reservas de todos |
| Coordenação | Cadastrar tipos e máquinas; definir o papel das pessoas |

O cadastro público cria um Usuário. A primeira Coordenação nasce de variáveis de ambiente, na subida, se ainda não existir nenhuma. A última Coordenação não pode perder o papel (409).

### 4.6 Efeitos sobre reservas que já existem (decisões de implementação)
- **Revogar um treinamento** cancela as reservas futuras dessa pessoa nas máquinas daquele tipo (`CanceladaPelaEquipe`, motivo "Treinamento revogado"). Sem isso, alguém sem treinamento ainda operaria a máquina.
- **Desativar uma máquina** com reservas ativas e futuras é recusado (409 com a lista). Para tirá-la de uso na hora, registra-se uma manutenção.
- Mudar a duração máxima de um tipo ou o papel de uma pessoa não mexe nas reservas já feitas.

## 5. API
**Erros:** ProblemDetails (RFC 9457) com títulos em português, nas regras abaixo:
| Código | Quando |
|---|---|
| 400 | Formato ou validação (`errors` por campo) |
| 401 | Sem token ou token inválido |
| 403 | Papel insuficiente, ou reserva de outra pessoa |
| 404 | Recurso inexistente |
| 409 | Conflito de agenda (com a lista de conflitos ou a máquina sugerida) |
| 422 | Regra de negócio, como "sem treinamento válido em Cortadora a laser" ou limite do projeto pessoal |

**Rotas (resumo):**
- `GET /saude` (público; 200 ou 503 conforme o banco);
- `POST /auth/cadastro`, `POST /auth/login`, `GET /me`;
- `GET /usuarios` e `PUT /usuarios/{id}/papel` (Coordenação);
- `GET /tipos-de-equipamento` e `GET /equipamentos` (autenticado), com `POST` e `PUT` só para a Coordenação;
- `GET /equipamentos/{id}/agenda?de=&ate=` (reservas ativas e manutenções, janela de até 31 dias; nomes dos operadores só para a Equipe);
- `GET /disponibilidade?tipoId=&inicio=&fim=` (máquinas do tipo livres no intervalo);
- `POST /treinamentos`, `POST /treinamentos/{id}/revogacao` e `GET /treinamentos?usuarioId=` (Equipe), além de `GET /me/treinamentos`;
- `POST /reservas` (as duas finalidades, com `motivoDesmarcacao` opcional), `GET /me/reservas`, `POST /reservas/{id}/cancelamento` (dono) e `GET /reservas` (Equipe);
- `POST /equipamentos/{id}/manutencoes`, `POST /manutencoes/{id}/encerramento` e `GET /equipamentos/{id}/manutencoes`.

**Documentação:** OpenAPI nativo do ASP.NET Core 10, com a interface Scalar sempre ligada. É a vitrine da API até o Blazor ficar pronto.

**Login:**
- ASP.NET Core Identity (usuários, hash de senha, papéis) com o EF Core;
- endpoint próprio que emite um JWT HS256 de 60 min, com a chave em variável de ambiente;
- **sem refresh token**, porque o J1 já mostra esse fluxo;
- senha com **15 caracteres ou mais** e sem regras de composição (NIST SP 800-63B-4).

## 6. Dados
- Migrations do EF Core, aplicadas na subida da API (uma instância só; o trade-off fica registrado em ADR).
- A extensão `btree_gist`, mais a restrição `EXCLUDE USING gist (equipamento_id WITH =, tstzrange(inicio, fim, '[)') WITH &&) WHERE (situacao = 'Ativa')` na tabela de reservas. A violação vira 409.
- Dados de exemplo inspirados no Itec Park: cortadora a laser, 3 impressoras FDM, uma de resina e um scanner 3D, mais pessoas de exemplo nos três papéis, só no ambiente de desenvolvimento e no Compose.

## 7. Testes e entrega
- **Unidade** (`Testes.Unidade`, sem Docker): cada regra das §4.2 a §4.4, com o relógio controlado (`TimeProvider` e `FakeTimeProvider`).
- **Integração** (`Testes.Integracao`): a API inteira (`WebApplicationFactory`) contra um PostgreSQL 17 real (Testcontainers), com um container compartilhado pela coleção de testes. Cobre papéis por rota (401/403), contratos de erro e **concorrência** (dois `POST /reservas` simultâneos na mesma máquina e horário → um 201 e um 409).
- **Blazor:** testes de componente com bUnit, e o fluxo conferido no navegador.
- **CI** (GitHub Actions): `push` só na `main` e em `pull_request`; build, testes e o build da imagem Docker.
- **Um comando:** `docker compose up` sobe o PostgreSQL e a API com os dados de exemplo.
- **README:** problema, diagrama, como rodar, decisões, roteiro de entrevista e a declaração do uso de IA ("Desenvolvido com apoio de ferramentas de IA, com as decisões e a revisão feitas por mim.").

## 8. Fora do escopo
- e-mail e notificações;
- fila de pedidos com aprovação;
- cobrança por hora e controle de material (filamento, resina, chapas);
- horário de funcionamento do parque (impressões atravessam a noite);
- reservas recorrentes;
- encerrar antes do fim uma reserva em andamento (a impressão que acabou cedo);
- refresh token e login social;
- vários parques numa instância.

## 9. Roteiro
⚑ = exige aprovação do Arthur antes de começar.

| Inc. | Entrega | Critério de aceitação |
|---|---|---|
| **1.1** | Esqueleto: solução (`Dominio`, `Api`, `Testes.Unidade`, `Testes.Integracao`; `Contratos` entra no 1.2 e `Web` no 1.8), `Intervalo` no domínio, EF Core + PostgreSQL com a primeira migração (`btree_gist`), `/saude`, ProblemDetails, OpenAPI + Scalar, Dockerfile, Compose e CI | `dotnet test` verde (unidade + integração com Testcontainers); `docker compose up` responde 200 em `/saude`; CI verde no primeiro PR |
| 1.2 ⚑ | Login e papéis: Identity + JWT, cadastro, login, `/me`, Coordenação inicial por variáveis de ambiente, definir papel | 401 sem token e 403 no papel errado, testados por rota; senha < 15 → 400 |
| 1.3 | Tipos e máquinas (Coordenação) + dados de exemplo | CRUD com validação 400, nome único → 409, máquina inativa visível |
| 1.4 | Treinamentos (Equipe registra e revoga, 12 meses) | Validade e revogação testadas com relógio falso |
| 1.5 | Reservas, o núcleo (§4.2), com a restrição de exclusão | Um teste por regra; concorrência: um 201 e um 409 |
| 1.6 | Disponibilidade por tipo + desmarcação (§4.3) | Um teste por trava; desmarcação e criação na mesma transação |
| 1.7 | Manutenção (§4.4) com a trava por máquina | Cancelamento das reservas no intervalo e bloqueio de novas, testados |
| 1.8 ⚑ | Blazor, parte 1: direção visual aprovada pelo Arthur, login, agenda semanal por máquina, minhas reservas, reservar e cancelar | Testes bUnit; fluxo conferido no navegador |
| 1.9 | Blazor, parte 2: telas da Equipe e da Coordenação | Fluxos conferidos no navegador |
| 1.10 ⚑ | Deploy gratuito (Render + Neon + Cloudflare Pages, limites conferidos antes) com contas de demonstração por papel; README final | Link público funcionando nos 3 papéis |

## 10. Deixado para quem implementa
- Nomes de classes, pastas internas e a forma dos DTOs, desde que sigam as rotas e os códigos da §5.
- O mapeamento de tipos no EF Core (`DateTimeOffset` ↔ `timestamptz`) e a forma de traduzir a violação da restrição em 409.
- A forma de isolar os dados entre testes de integração, sem dependência nova se der.
- Cada incremento tem um plano curto em `docs/planos/`, escrito quando o anterior termina.
