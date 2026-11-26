# 0008. Manutenção e a trava da máquina

Status: aceita · Incremento 1.7 · 2026-11-26

## Contexto
A Equipe precisa tirar uma máquina de uso: ela quebrou, ou vai passar por uma troca de peça (spec §4.4). A manutenção cancela toda reserva ativa que cruze o intervalo, inclusive a que está em andamento e as das duas finalidades, e bloqueia as reservas novas. O pior defeito do sistema continua o mesmo (ADRs 0006 e 0007): uma reserva ativa cruzando uma manutenção ativa, ou duas manutenções sobrepostas, é uma máquina "reservada" e parada ao mesmo tempo. E a manutenção entra numa agenda que já tem outros caminhos que gravam: a criação da reserva, a desmarcação, o cancelamento pela revogação do treinamento e o `PUT /equipamentos/{id}`.

## Decisão
**Toda escrita que ocupa a agenda de uma máquina passa pela trava da linha dela.** Registrar e encerrar uma manutenção travam a linha da máquina (`SELECT … FOR NO KEY UPDATE`, até o commit), a mesma que a criação da reserva, a desmarcação e o `PUT /equipamentos/{id}` já travam (ADR 0006). Só depois a transação lê o que decide, porque em `READ COMMITTED` cada comando enxerga o que já foi confirmado quando começa.

**Registrar, numa transação só e nesta ordem:**
1. trava a linha da máquina;
2. procura as manutenções ativas que cruzam o intervalo: se houver, 409 com a lista `manutencoes`;
3. trava as linhas das reservas ativas da máquina que cruzam o intervalo (`SELECT id … ORDER BY id FOR UPDATE`) e as cancela com um `UPDATE … WHERE situacao = 'Ativa'` como `CanceladaPorManutencao`, com o motivo da manutenção, quem registrou e o "agora". Entram a reserva em andamento e as das duas finalidades. O `UPDATE` só pega as ainda ativas: se o dono cancelou no meio, o cancelamento dele fica como está;
4. grava a manutenção e confirma. Qualquer falha antes do commit desfaz o cancelamento das reservas junto (um teste faz o `INSERT` da manutenção falhar e confere que as reservas continuam ativas).

**A ordem das travas.** A da manutenção (máquina e depois as linhas das reservas) é um pedaço da ordem comum: máquina, treinamentos, pessoa e linhas das reservas em ordem de id (ADRs 0006 e 0007). Pular etapas dessa ordem não cria ciclo. A revogação do treinamento (treinamentos e depois as linhas das reservas) também a segue, e por isso uma manutenção e uma revogação ao mesmo tempo, sobre um dono com duas reservas no intervalo, não se travam em ciclo. Um teste repete a corrida em 12 rodadas.

**Sem sobreposição entre manutenções: a trava e uma restrição de exclusão.** Duas manutenções na mesma máquina já ficam em fila pela trava, e a segunda vê a primeira (409 com a lista). Mesmo assim, a tabela tem `EXCLUDE USING gist (equipamento_id WITH =, tstzrange(inicio, fim, '[)') WITH &&) WHERE (situacao = 'Ativa')`, com o nome fixo `ex_manutencoes_sem_sobreposicao`, e um `CHECK (fim > inicio)`. É a garantia final para qualquer caminho que escape da trava, no mesmo molde das reservas: a violação (23P01) vira 409 sem a lista, pelo mesmo `ViolacaoDeExclusao`. Encostar (uma acaba às 10:00 e a outra começa às 10:00) é permitido, e é assim que se prorroga. A restrição entra por SQL na migração `Manutencoes` e sai no `Down`, porque o EF Core não modela `EXCLUDE`.

**A reserva e a manutenção se enxergam em todo caminho que cria uma delas.** O `POST /reservas` lê, sob a mesma trava, as reservas e as manutenções ativas que cruzam o intervalo. Qualquer manutenção é 409. Todo 409 de agenda passa a ter as três listas, `conflitos`, `maquinasLivres` e `manutencoes`, sempre presentes e vazias quando não há nada (o 409 da restrição de exclusão continua sem listas, porque a transação já falhou). Com manutenção no intervalo, a mensagem é "A máquina está em manutenção nesse horário.". A consulta única de máquinas livres também deixa de fora a máquina em manutenção: uma máquina parada não é "outra máquina livre", e isso vale juntas para a disponibilidade, para a lista `maquinasLivres` e para a trava 2 da desmarcação. Na trava 3, com motivo, a manutenção recusa antes de qualquer outra checagem, mesmo sem nenhuma reserva em conflito: "Uma manutenção não se desmarca.".

**Encerrar também trava a máquina.** A rota lê a manutenção (404 se não existe), trava a máquina dela, relê e decide pelo domínio: em andamento, o fim passa a ser o "agora" e ela continua ativa; ainda não começou (inclusive a que começa exatamente agora, porque encurtar até o início deixaria o intervalo vazio), fica cancelada; já acabou ou já cancelada, 409. As reservas que ela cancelou não voltam (§4.4).

## Alternativas consideradas
- **Só a trava, sem a restrição entre manutenções:** mais simples, mas sem a garantia final que a spec valoriza nas reservas. A restrição custa um SQL na migração e segura qualquer caminho que escape da trava.
- **A manutenção como uma linha da tabela de reservas, com uma finalidade a mais:** a restrição de exclusão cobriria as duas, e o conflito entre reserva e manutenção passaria a ser garantido pelo banco. Mas misturaria dois conceitos: operador, treinamento, limite de reservas e duração máxima não valem para a manutenção, e toda consulta de reservas (a lista, o `/me`, o limite de 2 pessoais) precisaria deixá-la de fora.
- **Um *trigger* que confere a outra tabela:** sem trava, teria a mesma corrida da checagem em C#: dois pedidos simultâneos leriam o estado antigo.
- **Encerrar sem travar a máquina:** não quebraria nenhuma regra, porque encerrar só libera horário. Mas dois encerramentos da mesma manutenção não ficariam em fila, e a regra deixaria de ser uma só ("toda escrita de manutenção passa pela trava da máquina"). Com a trava, a primeira recebe 200 e a segunda vê o estado novo e recebe 409.

## Consequências
- Toda escrita que ocupa a agenda de uma máquina passa pela trava dela: a reserva, a desmarcação, o `PUT /equipamentos/{id}`, o registro e o encerramento de manutenção. Um caminho novo que grave na agenda precisa pegar a mesma trava antes de ler.
- Os testes de concorrência repetem 12 rodadas, com um cliente por pedido: manutenção e reserva no mesmo horário dão 201 e 409, ou 201 e a reserva cancelada; manutenção e desmarcação sobre a mesma pessoal a cancelam uma vez só; duas manutenções sobrepostas dão um 201 e um 409; manutenção e revogação não dão deadlock; dois encerramentos dão um 200 e um 409. Nenhuma rodada termina em 500, e nunca sobra reserva ativa cruzando uma manutenção ativa.
- **Pior:** entre uma reserva e uma manutenção só a trava garante, porque nenhuma restrição do banco cruza duas tabelas. Um `INSERT` direto em `reservas`, fora da API, que não pegue a trava da máquina, pode criar uma reserva ativa dentro de uma manutenção sem que o banco reclame.
- **Pior:** a manutenção cancela também a reserva em andamento, e o dono só fica sabendo olhando `GET /me/reservas`. Não há aviso por e-mail (fora do escopo, spec §8): quem está com a peça na máquina descobre quando a Equipe aparece.
- **Pior:** a trava da máquina ficou mais disputada. Uma manutenção que cancela muitas reservas segura a linha da máquina até o commit, e as reservas novas dela esperam. É o preço de ter uma regra só, e o tamanho de um makerspace não o torna um gargalo.
