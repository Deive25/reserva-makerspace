# 0005. Um relógio só

Status: aceita · Incremento 1.4 · 2026-11-25

## Contexto
O 1.4 traz as primeiras regras que dependem do tempo: um treinamento vale 12 meses e depois vence. Para testar "um segundo antes dos 12 meses" e "nos 12 meses exatos" sem esperar um ano, o teste precisa avançar o relógio da API. Mas a API tem dois lugares que perguntam as horas: o código das regras e a validação do JWT, que confere `exp` e `nbf`. Se só o primeiro usasse o relógio trocado, o token emitido no relógio falso seria validado pelo relógio do sistema e a API recusaria todo pedido assim que o teste avançasse o tempo.

## Decisão
- **Todo "agora" da API vem do `TimeProvider` registrado na injeção de dependência** (hoje, o `TimeProvider.System`). O emissor do token já o usava desde o 1.2. O domínio não lê o relógio: recebe o instante por parâmetro (`Treinamento.Registrar(..., agora)`, `SituacaoEm(agora)`), e os controllers passam `relogio.GetUtcNow()`.
- **A validação do JWT usa o mesmo relógio, por um `LifetimeValidator`.** A propriedade `TokenValidationParameters.TimeProvider` existe na biblioteca (Microsoft.IdentityModel.Tokens 8.19.2), mas é `internal`: não dá para configurá-la. O `LifetimeValidator` é público e recebe `nbf` e `exp`, então a API o configura (`AddOptions<JwtBearerOptions>().Configure<TimeProvider>`) para comparar com o `TimeProvider` da injeção. Ele repete as regras padrão: sem `exp` recusa; `nbf` depois de `exp` recusa; `exp` no passado além da folga de 30 s recusa; `nbf` no futuro além da folga recusa. `ValidateLifetime` continua ligado.
- **Nos testes, o `FakeTimeProvider` entra no lugar do registro** (`FabricaComRelogio`, com `ConfigureTestServices`), num banco próprio, fora da coleção compartilhada. O pacote `Microsoft.Extensions.TimeProvider.Testing` só existe nos projetos de teste.
- **Regra para o resto do projeto:** nenhum código novo usa `DateTime.UtcNow` nem `DateTimeOffset.UtcNow`.

## Alternativas consideradas
- **`DateTime.UtcNow` direto, com testes que criam datas no passado:** o teste teria de gravar um treinamento "feito há 13 meses" direto no banco, em vez de exercitar a regra de registro e o vencimento pelo caminho real. Fica de fora o que mais importa, a fronteira dos 12 meses.
- **Uma interface de relógio própria:** o .NET já traz o `TimeProvider`, com o `FakeTimeProvider` pronto. Uma interface nossa seria código a mais para manter.
- **O relógio falso só nos testes de unidade, sem avançar o tempo na integração:** a regra de tempo ficaria testada, mas não o caminho completo (token, rota, banco, resposta).
- **Configurar `TokenValidationParameters.TimeProvider`:** era a primeira ideia. Não compila, porque a propriedade é interna.
- **Reflexão ou `InternalsVisibleTo` para chamar a propriedade interna:** quebraria sem aviso numa atualização da biblioteca.

## Consequências
- As regras de tempo do 1.5 ao 1.7 (reservas, desmarcação, manutenção) usam o mesmo relógio e podem ser testadas avançando o tempo.
- Um teste prova que o token emitido antes de o relógio avançar 61 minutos deixa de valer, e que um login depois do avanço funciona.
- **Pior:** a fábrica com relógio falso sobe a API num banco próprio a cada teste, o que é mais lento que a fábrica compartilhada. Por isso ela é usada só onde o tempo avança.
- **Pior:** o `LifetimeValidator` repete uma regra que a biblioteca já tem. Se uma versão futura abrir a propriedade `TimeProvider`, ele pode ser trocado por ela, e os testes de token (vencido, sem `exp`, `nbf` futuro, `nbf` depois de `exp`, as duas bordas da folga de 30 s e os 61 minutos) pegam qualquer diferença.
