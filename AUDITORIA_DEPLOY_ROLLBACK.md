# Auditoria: rollback do deploy falhou sem motivo (2026-11-07)

**Status: CAUSA NAO REPRODUZIDA.** Este documento registra o que foi descartado,
com evidencia. Nao afirmo uma causa que nao consegui provar.

## O sintoma

```
[18:51:41] update [aviso] API nao ficou saudavel apos o update; executando rollback...
[18:51:41] update [aviso] Sem release anterior para rollback.
```

Ou seja: o rollback existia para este caso exato e nao encontrou alvo,
mesmo havendo 34 releases em /opt/discovery-api/releases.

## Fato: havia alvo de sobra

A release `20261107165102-initial` (16:51, anterior ao deploy quebrado das 18:45)
existe, e executavel e NAO contem a M279 (strings: 0 ocorrencias). Era um alvo valido.
Eu a usei para recuperar a producao as 19:20 — o rollback funcionou quando executado a mao.

## Hipotese 1 — REFUTADA: o script nao sabia achar o alvo

O codigo de selecao em `restart_api_with_rollback` (deploy.sh:1141-1174) foi
reexecutado por mim, read-only, em dois cenarios:

| Cenario | Resultado |
|---|---|
| `DISCOVERY_API_CURRENT` definido, current -> release quebrada (estado das 18:51) | encontra `20261107165102-initial` |
| `DISCOVERY_API_CURRENT` **unset** (estado de `update_all_components`) | encontra `20261107165102-initial` |

Em ambos os casos o fallback `${DISCOVERY_API_CURRENT:-/opt/discovery-api/current}`
resolve corretamente. **A logica de selecao esta correta e nao explica a falha.**

## Hipotese 2 — REFUTADA: a versao do script em uso era antiga

O instalador roda a partir de `/tmp/discovery-bootstrap.<id>/`. Verifiquei o clone
da execucao das 18:44 (`jDAFrN`): ele CONTEM a logica corrigida
(`skipped_newest` aparece 3x, `readlink -f "$candidate"` 1x). Os tres clones
existentes sao identicos entre si (md5 conferido pelo teammate).
**A versao em execucao estava corrigida.**

## Hipotese 3 — NAO VERIFICAVEL: o laco de health consumiu a janela inteira

O laco de health faz ate 180 tentativas x 2s = 6 min. O restart comecou 18:45:31;
18:45:31 + 6 min = **18:51:31**, e a mensagem saiu **18:51:41**. A coincidencia e exata,
o que confirma que o bloco de rollback FOI alcancado. Mas isso apenas confirma o
caminho — nao explica por que `previous_release` saiu vazio.

## O que NAO consegui obter

**Nao ha log do instalador das 18:44 preservado.** `/root/.install-*.log` e de 14/10.
`grep -rl "Sem release anterior"` so encontra o proprio script. Sem esse log nao
e possivel saber o valor real das variaveis no momento da execucao.

**Nao ha como reproduzir sem repetir o deploy.** O cenario depende de estado que
ja mudou (o symlink foi reapontado por mim).

## Risco residual (o que importa na pratica)

Independente da causa, a consequencia e clara: **quando o deploy falha, o rollback
automatico nao pode ser presumido como confiavel.** No incidente de hoje ele nao
protegeu o servico, e a recuperacao dependeu de intervencao manual.

Recomendacao: instrumentar. Registrar em log, no inicio do bloco de rollback, os
valores de `current_release`, `previous_release`, `releases_root` e a contagem de
candidatos. Sao 4 linhas de `log` e tornam a proxima ocorrencia diagnosticavel em
segundos, em vez de exigir arqueologia. **Nao implementei** — e mudanca no script
de deploy, e prefiro sua aprovacao.

## Achado colateral — confirmado

`restart_api_with_rollback` trata `systemctl is-failed` como condicao de parada
(deploy.sh:1193), mas o servico tem `Restart=always`. Em crash-loop o estado e
`activating`, nunca `failed`. Logo o laco **sempre** roda os 6 min completos antes
de desistir: um update quebrado fica 6 minutos em silencio antes de qualquer aviso.
Verifiquei: `systemctl show -p Restart` -> `Restart=always`; durante o incidente
`is-active` retornava `activating` e `is-failed` nao disparava.

Isso **agrava** incidentes mas **nao** e a causa do rollback vazio. Sao dois problemas
distintos.