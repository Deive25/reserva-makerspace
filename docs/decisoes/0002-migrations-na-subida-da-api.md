# 0002. Migrations na subida da API

Status: aceita · Incremento 1.1 · 2026-11-24

## Contexto
O esquema do banco evolui por migrations do EF Core. É preciso decidir quem as aplica e quando. O projeto roda com uma instância da API (Compose local e, no fim, um serviço gratuito) e precisa subir com um comando, sem etapa manual.

## Decisão
- A API aplica as migrations pendentes na subida (`Database.MigrateAsync()`, antes de `Run`). Assim, `docker compose up` entrega um banco pronto, e os testes de integração partem de um banco criado pelo mesmo caminho da produção.
- A opção `Banco:AplicarMigracoesNaSubida` (padrão `true`) permite desligar o comportamento. Os testes de banco fora do ar a usam para que a API suba sem tentar conectar.
- A primeira migração (`Inicial`) cria a extensão `btree_gist`, que a restrição de exclusão contra reservas sobrepostas vai usar no incremento 1.5.

## Alternativas consideradas
- **Migration bundle (`dotnet ef migrations bundle`) executado no deploy:** é o correto com várias instâncias, porque o esquema muda uma vez, antes de a nova versão subir. Fica como evolução se a API passar de uma instância.
- **Script SQL aplicado à mão:** simples, mas é uma etapa manual a mais e fácil de esquecer.

## Consequências
- Um comando sobe tudo, e não há passo extra para quem clona o projeto.
- **Pior:** com duas instâncias subindo juntas, as duas tentariam migrar ao mesmo tempo (o EF Core usa um bloqueio no banco, mas o desenho não é pensado para isso). Aceitável com uma instância.
- **Pior:** a conta do banco usada pela API precisa de permissão para alterar o esquema (e para criar a extensão), mais do que a API precisaria só para ler e gravar.
- **Pior:** uma migração com erro derruba a subida. É o comportamento desejado (falhar cedo), mas o serviço fica fora do ar até corrigir.
