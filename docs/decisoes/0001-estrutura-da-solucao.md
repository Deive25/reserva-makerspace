# 0001. Estrutura da solução

Status: aceita · Incremento 1.1 · 2026-11-24

## Contexto
O domínio tem regras de agenda (sobreposição de horários, validade de treinamento, limites do projeto pessoal) que precisam de testes rápidos, sem banco nem web. Ao mesmo tempo, o Blazor do incremento 1.8 vai consumir a API e deve compartilhar os tipos de entrada e saída com ela.

## Decisão
- **Domínio separado:** o projeto `Dominio` não depende de nada (nem EF Core, nem ASP.NET). As regras, como o `Intervalo`, são testadas em `Testes.Unidade` sem Docker.
- **`Api`** guarda controllers, EF Core e Npgsql, e referencia o `Dominio`.
- **`Contratos`** (DTOs compartilhados com o Blazor, o mesmo tipo C# no servidor e no navegador) entra no incremento 1.2, e o `Web` no 1.8. Não criei projetos vazios antes de precisar deles.
- **Testes:** `Testes.Unidade` (rápidos) e `Testes.Integracao` (a API inteira contra um PostgreSQL real em container).
- Solução em `.slnx`, versões de pacotes centralizadas em `Directory.Packages.props` e SDK fixado em `global.json`.

## Alternativas consideradas
- **Clean Architecture completa** (Dominio, Aplicacao, Infraestrutura, Api): neste tamanho de domínio, as camadas extras só repassariam chamadas.
- **API num projeto só:** a mais simples, mas mistura as regras com o EF Core e obriga os testes de regra a subir infraestrutura.

## Consequências
- Os testes de regra rodam em milissegundos e sem Docker.
- **Pior:** com o `Dominio` sem EF Core, as entidades vão precisar de mapeamento explícito (configurações do EF na `Api`), e isso dá um pouco mais de código.
- **Pior:** o `Contratos` é um projeto a mais para manter quando o Blazor chegar, mas evita duplicar os DTOs.
