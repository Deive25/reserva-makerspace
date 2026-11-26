# 0006. Concorrência nas reservas

Status: aceita · Incremento 1.5 · 2026-11-25

## Contexto
O pior defeito do sistema é duas reservas ativas sobrepostas na mesma máquina, e o segundo pior é uma pessoa sem treinamento válido operando uma máquina. As duas coisas podem acontecer com pedidos simultâneos, porque cada pedido decide lendo o banco e depois grava: se os dois leem antes de qualquer um gravar, os dois passam. O PostgreSQL, no nível padrão (READ COMMITTED), faz cada comando enxergar o que já foi confirmado quando ele começa. Isso tem três consequências para as reservas:
- dois pedidos na mesma máquina e no mesmo horário;
- uma reserva e uma revogação do treinamento da mesma pessoa, ao mesmo tempo;
- dois pedidos da mesma pessoa em máquinas diferentes, que contariam cada um uma reserva pessoal a menos e furariam o limite de 2.

## Decisão
**Duas camadas contra a sobreposição:**
- **A checagem em C#, sob uma trava.** A criação da reserva roda numa transação que começa travando a linha da máquina (`SELECT … FOR NO KEY UPDATE`). Só depois lê o que decide (o `ativo`, o tipo, os conflitos) e grava. Os pedidos da mesma máquina ficam em fila: o segundo vê a reserva já confirmada do primeiro e recebe 409 com a lista dos conflitos (início, fim e finalidade, sem o nome do operador).
- **Uma restrição de exclusão no PostgreSQL, a garantia final.** `EXCLUDE USING gist (equipamento_id WITH =, tstzrange(inicio, fim, '[)') WITH &&) WHERE (situacao = 'Ativa')`, com `CHECK (fim > inicio)`. Ela vale para qualquer caminho que escape da trava, como um bug ou um SQL manual. A violação (código 23P01) vira 409, sem a lista. O EF Core não modela `EXCLUDE`, então a restrição entra por SQL na migração `Reservas`, com nome fixo (`ex_reservas_sem_sobreposicao`), e o `Down` a remove.

**Reservar e revogar ficam em fila.** Antes de conferir a validade do treinamento, a reserva trava com `FOR SHARE`, em ordem de id, as linhas de treinamento da pessoa no tipo e as segura até o commit. A revogação (1.4) trava as mesmas linhas com `FOR UPDATE` e, na mesma transação, cancela as reservas ativas da pessoa nas máquinas do tipo que ainda não começaram. Se a reserva chega primeiro, a revogação espera e depois a cancela. Se a revogação chega primeiro, a reserva espera e encontra o treinamento revogado (422). Duas reservas da mesma pessoa no mesmo tipo não se bloqueiam, porque as duas pedem `FOR SHARE`.

**O limite de 2 reservas pessoais também é travado.** Na reserva pessoal, a transação trava a linha da pessoa em `usuarios` (`FOR NO KEY UPDATE`) antes de contar. Sem isso, três pedidos simultâneos da mesma pessoa em máquinas diferentes contariam 0 ou 1 cada e passariam todos.

**Ordem fixa das travas: máquina, treinamentos, pessoa.** Toda transação que pega mais de uma trava as pega nesta ordem, e é isso que evita deadlock. O `PUT /equipamentos/{id}` trava só a máquina, do mesmo jeito, antes de conferir as reservas futuras. A trava vai até o commit, e a transação só fecha depois do `SaveChanges`. A leitura que decide vem sempre depois da trava, nunca antes: antes dela, o comando enxergaria um estado já velho.

**`FOR NO KEY UPDATE`, e não `FOR UPDATE`, na máquina e na pessoa.** As duas variantes põem em fila quem trava a mesma linha. A diferença é que `FOR NO KEY UPDATE` não conflita com `FOR KEY SHARE`, a trava que o PostgreSQL toma na linha apontada quando outra transação grava uma linha com chave estrangeira para ela. Sem essa variante, gravar uma reserva ou um treinamento da pessoa ficaria preso por quem só está travando a máquina ou a pessoa. O caso mais claro é a Equipe revogando o próprio treinamento enquanto reserva: a revogação grava `revogado_por_id` (chave para a própria pessoa), e a reserva trava a linha dela.

**Um cancelamento não sobrescreve outro.** O cancelamento pelo dono e o da revogação são `UPDATE … WHERE situacao = 'Ativa'` (no do dono, também `AND inicio > agora`). Quem chega depois encontra 0 linhas: o dono recebe 409 ("A reserva já foi cancelada." ou "A reserva já começou."), e a revogação deixa aquela reserva de lado. O motivo de quem cancelou primeiro fica. A leitura anterior ao `UPDATE` serve só para escolher entre 404, 403 e 409.

## Alternativas consideradas
- **Só a checagem em C#, sem trava:** não segura dois pedidos simultâneos, porque os dois leem "livre" antes de qualquer um gravar.
- **Só a restrição de exclusão:** segura a sobreposição, mas o pedido que perde a corrida recebe um 409 sem a lista de conflitos, e a restrição não cobre as outras regras (a desativação da máquina, a revogação do treinamento, o limite de 2).
- **Isolação `SERIALIZABLE` com nova tentativa:** resolveria, porque o banco recusa a transação que quebraria a serialização. Mas exigiria repetir as transações recusadas, em todas as rotas, e o 409 útil (com a lista) viria de uma mensagem de erro do banco.
- **Advisory locks (`pg_advisory_xact_lock`):** funcionariam, mas são travas por número escolhido pela aplicação, sem relação com as linhas. Quem esquecer de chamar não é avisado, e a trava na linha da máquina já existe e já protege o `PUT` e, no 1.7, a manutenção.
- **Uma trava na memória da aplicação (`lock`, `SemaphoreSlim`):** só vale com uma instância. Com duas, cada uma teria a sua trava.
- **`SELECT … FOR UPDATE` na máquina e na pessoa:** também funciona, mas segura quem só grava uma linha apontando para elas (uma reserva nova, um treinamento novo), e abre a chance de deadlock com a revogação.

## Consequências
- O 1.6 (desmarcação) e o 1.7 (manutenção) usam a mesma trava da máquina e a mesma ordem das travas. A desmarcação entra no fim da criação, onde o conflito já é conhecido. A manutenção trava a linha da máquina antes de cancelar as reservas do intervalo.
- Os testes de concorrência repetem várias rodadas, com um cliente por pedido e `Task.WhenAll`: dois pedidos na mesma máquina dão um 201 e um 409, três pedidos pessoais da mesma pessoa dão dois 201 e um 422, reservar e revogar nunca deixam uma reserva ativa e futura sem treinamento, e reservar e desativar a máquina nunca deixam uma reserva ativa numa máquina inativa. Um teste por SQL confere a restrição de exclusão sem passar pela API. Tirei cada trava para ver o teste correspondente falhar.
- **Pior:** toda criação de reserva numa máquina passa por uma fila, o que limita a vazão por máquina. No tamanho de um makerspace (poucas reservas por minuto, cada transação de alguns milissegundos), isso não aparece.
- **Pior:** quem faz um `INSERT` direto na tabela pode violar uma regra que só a API confere (o treinamento, o limite de 2). A restrição de exclusão cobre só a sobreposição, que é a mais grave.
- **Pior:** a restrição de exclusão não aparece no modelo do EF Core. Um comentário no `MakerspaceDbContext` avisa, mas quem apagar e recriar a migração `Reservas` precisa lembrar de recriá-la.
