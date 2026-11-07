# Recuperacao da API de Producao - discovery-api @ 192.168.10.74

| Campo | Valor |
|---|---|
| Data da investigacao | 2026-11-07 ~19:08-19:20 (America/Sao_Paulo) |
| Host | discovery-pstefano (192.168.10.74) |
| Servico | discovery-api.service (systemd) |
| Autor | prod-recovery (task-3) |
| Modo | **SOMENTE LEITURA** - nenhuma escrita, restart, symlink ou DDL executada |
| Entregavel | este arquivo |

> **Aviso de escopo.** Todos os comandos abaixo foram de inspecao. Nada foi
> reiniciado, nenhum symlink foi alterado, nenhuma migration foi executada e
> nenhum UPDATE/DELETE/INSERT foi feito no banco. As consultas ao banco foram
> apenas SELECT e \d. As acoes propostas sao para o **Lead** executar.

---

## 1. Veredito em uma linha

**O rollback e seguro e e a rota recomendada.** O banco esta em M278; a release
candidata (17:51) conhece ate M277; **M278 e seed puro, sem DDL**; e **nada foi
escrito entre M277 e M278 alem de linhas de seed**. M279 nunca foi aplicada e a
coluna nunca existiu - logo **nao ha schema sujo para limpar**.

Ressalva importante que **eu refutei parcialmente**: o Lead afirmou que a
candidata "conhece ate M277" e que o banco esta em M278 - **isso esta correto**.
Porem a checagem de "DDL escondido em M278" **devolveu DML, nao DDL** (ver 6.2):
M278 contem DELETE FROM no seu Down(), nao no Up(). O Up() e seed puro.
O veredito se sustenta, mas por um motivo ligeiramente diferente do enunciado.

---

## 2. Estado factual do servico

### 2.1 Crash-loop confirmado

```
$ systemctl status discovery-api --no-pager -l
* discovery-api.service - Discovery API
     Loaded: loaded (/etc/systemd/system/discovery-api.service; enabled; preset: enabled)
     Active: activating (auto-restart) (Result: signal) since Sat 2026-11-07 19:08:28 -03; 2s ago
    Process: 84252 ExecStart=/opt/discovery-api/current/Discovery.Api (code=killed, signal=ABRT)
   Main PID: 84252 (code=killed, signal=ABRT)
        CPU: 1.524s

$ systemctl is-active discovery-api
activating

$ systemctl show discovery-api -p NRestarts -p Restart -p ExecMainStatus
Restart=always
RestartUSec=5s
NRestarts=204
ExecMainStatus=6
```

**204 restarts**, Restart=always, RestartSec=5. O processo morre com
**SIGABRT (status 6)** a cada ~2s. O servico esta em activating (auto-restart),
ou seja, **nao esta "down" para o systemd - esta em loop infinito**. Isso e pior
do que down: gera carga continua e poluicao de journal.

### 2.2 O site responde 200, mas a API inteira esta 502

Este e o achado que mais subestima o impacto. O nginx serve o **shell estatico
do SPA** com 200, o que faz o servico "parecer no ar":

```
$ curl -s -k -o /dev/null -w "%{http_code}" https://127.0.0.1/
200

$ curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8080/
000          # porta 8080 nao tem listener

$ curl -s -k -o /dev/null -w "%{http_code}" https://127.0.0.1/health
502
```

E **todo** endpoint de API via nginx devolve 502:

```
https/health        -> 502
https/healthz       -> 502
https/api/health    -> 502
https/api/v1/health -> 502
```

Confirmado por ss -lntp: nao existe listener em 8080 (nginx, 5432, 4222 e 9000
tem). E o log do nginx prova que **agents de producao estao falhando agora**:

```
2026/11/07 19:09:06 [error] connect() failed (111: Connection refused) while connecting
  to upstream, request: "POST /api/v1/agent-auth/me/sensors/samples HTTP/1.1",
  upstream: "http://127.0.0.1:8080/api/v1/agent-auth/me/sensors/samples"
2026/11/07 19:09:09 ... "GET /api/v1/agent-auth/me/commands?limit=1"
2026/11/07 19:09:12 ... "GET /api/v1/agent-auth/me/support-chat/active"
2026/11/07 19:09:17 ... "GET /health"
```

**Impacto real:** coleta de sensores, fila de comandos e suporte estao todos
quebrados. Os agents estao em retry contra um upstream morto.

### 2.3 Disco e permissao: sem problema

```
$ df -h /
Filesystem                Size  Used Avail Use% Mounted on
ZFSSsd/subvol-100-disk-0   64G   17G   48G  26% /

$ df -i /
ZFSSsd/subvol-100-disk-0 100950974 1328558 99622416    2% /

$ ls -ld /opt/discovery-api /opt/discovery-api/current
drwxr-x--- 10 discovery-api discovery-api 11 Nov  7 18:45 /opt/discovery-api
lrwxrwxrwx  1 discovery-api discovery-api 50 Nov  7 18:45 /opt/discovery-api/current -> /opt/discovery-api/releases/20261107184431-initial
```

26% de uso, 2% de inodes, permissoes corretas, ownership discovery-api. **Disco
cheio e permissao estao DESCARTADOS como causa.**

---

## 3. Causa raiz: a M279

### 3.1 A SQL exata que falha (do journal da producao)

```
Nov 07 19:08:28 discovery-pstefano Discovery.Api[84252]:
            UPDATE departments d
            SET can_execute_on_machines = true
            WHERE EXISTS (
                SELECT 1 FROM ticket_macros m
                WHERE m.department_id = d.id
                  AND m.is_active = true
                  AND m.runbook_steps_json IS NOT NULL
                  AND m.runbook_steps_json <> '''',
                  AND m.runbook_steps_json <> ''[]''
            );

The error was 42601: syntax error at or near ","
POSITION: 346
```

E o stack confirma o ponto exato de execucao:

```
Unhandled exception. System.Exception: An error occurred executing the following sql: ...
  ---> Npgsql.PostgresException (0x80004005): 42601: syntax error at or near ","
    at FluentMigrator.Runner.Processors.Postgres.PostgresProcessor.Process(String sql)
    at FluentMigrator.Runner.MigrationRunner.ApplyMigrationUp(...)
    at Program.<Main>$ (String[] args) in /opt/discovery-api/source/src/Discovery.Api/Program.cs:line 439
```

### 3.2 Diagnostico da SQL

O bug e uma **virgula no lugar de um AND** (e o escape duplicado de aspas). Em
M279_AddCanExecuteOnMachinesToDepartments.cs, dentro do Execute.Sql(@"..."):

```
AND m.runbook_steps_json <> '''',           // <-- VIRGULA: fecha o AND, vira item de lista
AND m.runbook_steps_json <> ''[]''          // <-- continua sozinho -> 42601 em ","
```

Como o Execute.Sql esta dentro de um **verbatim string** (@"..."), o escape
correto de uma aspa simples seria '' (duas aspas no proprio SQL), nao ''''.
O autor escreveu '''' (quatro), que em verbatim string produz '''' literal -
o PostgreSQL le isso como string vazia seguida de aspa solta. O parser entao
encontra "," no meio do WHERE e aborta com **42601 syntax error at or near ","**.

**Reproducao read-only (eu rodei via EXPLAIN, sem executar nada):**

```
$ psql -c "EXPLAIN UPDATE departments d SET can_execute_on_machines = true WHERE EXISTS
   (SELECT 1 FROM ticket_macros m WHERE m.department_id = d.id AND m.is_active = true
    AND m.runbook_steps_json IS NOT NULL AND m.runbook_steps_json <> '', AND m.runbook_steps_json <> '[]');
ERROR:  syntax error at or near ","
LINE 1: ...ps_json IS NOT NULL AND m.runbook_steps_json <> '', AND m.ru...
                                                             ^
```

Erro **identico** ao de producao, mesma posicao relativa. Causa raiz confirmada
por reproducao independente.

### 3.3 O que o deploy das 18:45 realmente fez

O journal mostra que **M278 foi aplicada com sucesso** e **M279 abortou**:

```
$ psql -c 'SELECT "Version","Description","AppliedOn" AT TIME ZONE ... FROM "VersionInfo"
           WHERE "Version" >= 20270106000 ORDER BY "Version";'
   Version   |                 Description                 |         applied_local
-------------+---------------------------------------------+-------------------------------
 20270106271 | M271_SeedSensorReportTemplates              | 2026-11-05 22:13:39.773026-03
 20270107272 | M272_AddOsIdentityToAgentHardwareInfo       | 2026-11-07 01:09:51.834044-03
 20270108273 | M273_AddWindowsServerInventory              | 2026-11-07 01:09:51.96879-03
 20270110274 | M274_SeedRemoteSessionPermissionAndPersonas | 2026-11-07 19:52:00.206837-03
 20270110275 | M275_AddIdentityAuditColumns                | 2026-11-07 19:52:00.241165-03
 20270110276 | M276_AddIdentityHotPathIndexes              | 2026-11-07 19:52:00.288278-03
 20270110277 | M277_AddRemoteSessionRights                 | 2026-11-07 19:52:00.303949-03
 20270112278 | M278_SeedDepartmentTeamRoles                | 2026-11-07 21:45:33.482324-03
```

Interpretacao: o deploy falhou na **primeira** tentativa perto das 18:45 (M278
aplicada), depois ficou em loop. O AppliedOn de M278 e 21:45:33 - o relogio do
PostgreSQL esta em TZ America/Sao_Paulo (timezone = America/Sao_Paulo) e o
timestamp gravado e timestamp without time zone, entao ha um deslocamento de
exibicao. **Nao ha inconsistencia de relogio**: date do SO = 19:09:17 -03 e
SELECT now() do banco = 19:09:17 -03. O que parece "futuro" e apenas a coluna
ser naive.

---

## 4. O PONTO MAIS IMPORTANTE: VersionInfo e a coluna

Esta era a pergunta decisiva do enunciado. **Verifiquei, nao presumi.**

### 4.1 M279 esta registrada? NAO.

**Query (exatamente a pedida no enunciado):**

```sql
SELECT * FROM "VersionInfo" ORDER BY "Version" DESC LIMIT 5;
```

**Resultado literal:**

```
   Version   |         AppliedOn          |                 Description
-------------+----------------------------+---------------------------------------------
 20270112278 | 2026-11-07 21:45:33.482324 | M278_SeedDepartmentTeamRoles
 20270110277 | 2026-11-07 19:52:00.303949 | M277_AddRemoteSessionRights
 20270110276 | 2026-11-07 19:52:00.288278 | M276_AddIdentityHotPathIndexes
 20270110275 | 2026-11-07 19:52:00.241165 | M275_AddIdentityAuditColumns
 20270110274 | 2026-11-07 19:52:00.206837 | M274_SeedRemoteSessionPermissionAndPersonas
(5 rows)
```

**Query de confirmacao direta:**

```sql
SELECT COUNT(*) FROM "VersionInfo" WHERE "Version" = 20270112279;
```

```
 count
-------
     0
(1 row)
```

**M279 NAO esta registrada.** O topo e **M278** (20270112278).
Total de linhas em VersionInfo: **284**.

### 4.2 A coluna departments.can_execute_on_machines existe? NAO.

**Query via information_schema:**

```sql
SELECT column_name FROM information_schema.columns
WHERE table_schema='public' AND table_name='departments'
  AND column_name='can_execute_on_machines';
```

```
 column_name
-------------
(0 rows)
```

**Contagem explicita:**

```sql
SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema='public' AND table_name='departments'
  AND column_name='can_execute_on_machines';
```

```
 count
-------
     0
(1 row)
```

**Busca por qualquer variante em todo o banco:**

```sql
SELECT table_name, column_name, data_type FROM information_schema.columns
WHERE column_name LIKE '%can_execute%' ORDER BY table_name;
```

```
 table_name | column_name | data_type
------------+-------------+-----------
(0 rows)
```

Confirmado tambem por \d departments: a tabela tem **44 colunas** e
can_execute_on_machines **nao esta entre elas**.

### 4.3 Ha estado PARCIAL? NAO - e isso e uma boa noticia

O enunciado temia estado parcial. **Nao existe.** Tres evidencias:

1. **VersionInfo nao tem linha de M279** (acima) - o Up nao foi marcado.
2. **Nenhum artefato de constraint/indice** ligado a coluna:
```sql
SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid
WHERE t.relname='departments' AND conname LIKE '%can_execute%';
-- (0 rows)
```
3. **M277 (a ultima DDL aplicada) e M278 (seed) estao completas** - M278 criou o
   papel de verdade: 'Gestor do Departamento', is_system=t com **13 permissoes**
   em role_permissions.

**Conclusao:** a M279 falhou de forma **atomica**. Nao ha schema sujo, nao ha
coluna meio-criada, nao ha linha de VersionInfo enganosa. **Nao ha nada para
limpar antes de recuperar.** Isso descarta a opcao (b) do enunciado ("marcar M279
como aplicada a mao") como desnecessaria - mas tambem a torna **perigosa**, ver 7.

---

## 5. Releases: "sem release anterior" era FALSO

### 5.1 Existem 34 releases

```
$ ls -la /opt/discovery-api/
lrwxrwxrwx  1 discovery-api discovery-api 50 Nov  7 18:45 current -> /opt/discovery-api/releases/20261107184431-initial
drwxr-x--- 36 discovery-api discovery-api 36 Nov  7 18:44 releases

$ ls -1d /opt/discovery-api/releases/*/ | wc -l
34
```

Releases de 2026-10-31 ate 2026-11-07, incluindo a **imediatamente anterior ao
deploy quebrado**:

```
/opt/discovery-api/releases/20261107184431-initial/   <- current (QUEBRADA, tem M279)
/opt/discovery-api/releases/20261107165102-initial/   <- 17:51, ANTERIOR (sem M279)  ***CANDIDATA***
/opt/discovery-api/releases/20261107093803-initial/
/opt/discovery-api/releases/20261106220852-initial/
...
```

**A mensagem "Sem release anterior para rollback" e FALSA.** Havia alvo de sobra.

### 5.2 Por que o rollback falhou - o bug no deploy.sh

O deploy.sh atual **detecta corretamente** a release anterior. Eu reexecutei a
logica de deteccao **em modo leitura** (script replica, sem restart):

```
$ /tmp/replay_rollback.sh
current_release=[/opt/discovery-api/releases/20261107184431-initial]
releases_root=[/opt/discovery-api/releases]
previous_release=[/opt/discovery-api/releases/20261107165102-initial]
RESULT: rollback WOULD target /opt/discovery-api/releases/20261107165102-initial
```

Ou seja: **com o script de hoje, o rollback teria funcionado.** O comentario no
proprio script documenta o bug historico:

```
deploy.sh:1138:  # deste restart. Usar `current` como "release anterior" fazia o rollback
deploy.sh:1147:  # current_release resolver; se vier vazio, o rollback inteiro era pulado
                 # ("Sem release anterior") mesmo com dezenas de releases no diretorio.
deploy.sh:1152:  # `readlink -f`) contra a saida de `ls -1dt dir/*/` (COM barra final). A
deploy.sh:1155:  # falhar. Agora cada candidato e normalizado com `readlink -f` antes de
```

Ha **tres** clones do bootstrap em /tmp, todos com o deploy.sh **identico**
(md5 d4bf2daaaef03af6dcf33ee49ae2a6d4), inclusive o mais recente das 18:44
(/tmp/discovery-bootstrap.jDAFrN). **Nao consegui reproduzir** por que a mensagem
foi emitida, porque nao ha log preservado do instalador (ver 9). O que **posso
afirmar** e que a deteccao atual resolveria o alvo - entao ou (i) o caminho
executado foi outro, ou (ii) DISCOVERY_API_CURRENT chegou vazio, ou (iii) a
release foi publicada **depois** da checagem. Registro como lacuna.

### 5.3 A release 17:51 e valida e completa

```
$ ls /opt/discovery-api/releases/20261107165102-initial/ | wc -l
143        # mesma contagem da release quebrada (143) - release completa

$ ls -la .../20261107165102-initial/Discovery.Api
-rwxr-xr-x 1 discovery-api discovery-api 78192 Nov  7 16:51 Discovery.Api
```

Binario presente e executavel, mesmas 143 entradas (todos os .dll de dependencia).

---

## 6. COMPATIBILIDADE - a pergunta central do Lead

> "a release 17:51 e compativel com um banco que ja esta em M278?"

**Resposta: SIM, e compativel.** Evidencia abaixo, incluindo a refutacao que o
Lead pediu.

### 6.1 Delta de migrations embutidas

```
$ strings -a 20261107184431-initial/Discovery.Migrations.dll | grep -oE "M[0-9]{3}_[A-Za-z]+" | sort -u > m_1844.txt
$ strings -a 20261107165102-initial/Discovery.Migrations.dll  | grep -oE "M[0-9]{3}_[A-Za-z]+" | sort -u > m_1751.txt
$ comm -23 m_1844.txt m_1751.txt     # no 18:44, ausentes no 17:51
M278_SeedDepartmentTeamRoles
M279_AddCanExecuteOnMachinesToDepartments

$ comm -13 m_1844.txt m_1751.txt     # no 17:51, ausentes no 18:44
(nada)

counts: 18:44=282   17:51=280
```

**O delta e EXATAMENTE {M278, M279}. Nada mais.** A 17:51 nao perde nenhuma
migration que a 18:44 tem, exceto as duas novas.

- Ultima migration embutida em **17:51**: M277_AddRemoteSessionRights
- Ultima migration embutida em **18:44**: M279_AddCanExecuteOnMachinesToDepartments

### 6.2 REFUTACAO da ressalva do Lead (eu testei, nao aceitei de graca)

O Lead condicionou o veredito a "M278 nao ter DDL". **Meu scan encontrou
DELETE FROM em M278 - mas eu verifiquei ONDE:**

```
$ grep -oEi "INSERT INTO|UPDATE |DELETE FROM|ALTER TABLE|CREATE TABLE|DROP |TRUNCATE" M278_SeedDepartmentTeamRoles.cs | sort | uniq -c
      3 DELETE FROM
      2 INSERT INTO

$ grep -nEi "alter table|create table|addcolumn|delete\.column|drop table|create\.index|rename" M278_SeedDepartmentTeamRoles.cs
140:            DELETE FROM role_permissions rp
147:            DELETE FROM roles
158:            DELETE FROM role_permissions rp
```

Os tres DELETE FROM estao em **Down()** (linhas 140-158, dentro de
GrantDown/Down), **nao em Up()**. Um Down() so executa em rollback *de
migration* (MigrateDown), que **nao acontece** aqui - a API nao chama
MigrateDown no startup, apenas MigrateUp (Program.cs:439).

**Portanto: o Up() de M278 e seed puro (2 INSERT INTO, zero DDL).** O veredito do
Lead **se sustenta**, mas a afirmacao "a checagem devolveu 0" estava imprecisa:
ha DML no arquivo, apenas nao no caminho que roda. Registro isso porque e
exatamente o tipo de suposicao que poderia mascarar um risco.

### 6.3 M279 e a UNICA posterior a M278 - confirmado

```
$ grep -rhoE "\[Migration\(20270112[0-9]+\)\]" Migrations/ | sort -u
[Migration(20270112278)]
[Migration(20270112279)]
```

**Apenas M278 e M279** na faixa 20270112xxx. Nao ha migration escondida entre
elas. Confirmado tambem pela lista completa de IDs: o maior antes de M278 e
20270110277.

### 6.4 Scan DDL na janela M274-M279 (o intervalo que o rollback atravessa)

```
M274_SeedRemoteSessionPermissionAndPersonas  DDL verbs: 0   Sql: 4 DELETE FROM, 4 INSERT INTO
M275_AddIdentityAuditColumns                 DDL verbs: 8   Sql: 2 DROP
M276_AddIdentityHotPathIndexes               DDL verbs: 0   Sql: 5 DROP
M277_AddRemoteSessionRights                  DDL verbs: 3   Sql: (nenhum)
M278_SeedDepartmentTeamRoles                 DDL verbs: 0   Sql: 3 DELETE FROM, 2 INSERT INTO
M279_AddCanExecuteOnMachinesToDepartments    DDL verbs: 3   Sql: 1 UPDATE
```

Leitura correta: **M274-M277 sao as DDL, e TODAS ja estao aplicadas** (em
2026-11-07 19:52 local, antes do deploy das 18:44) e **todas estao embutidas na
17:51**. Entre a ultima DDL que a 17:51 conhece (M277) e o estado atual do banco
(M278) existe **apenas M278 = seed puro**.

### 6.5 O teste decisivo: EF model vs. coluna nova

Este e o risco real de rollback (o Lead pediu para confirmar): a release antiga
quebra ao ler uma tabela com coluna que ela desconhece?

```
$ for f in Discovery.Core.dll Discovery.Infrastructure.dll Discovery.Migrations.dll; do
    strings -a <release>/$f | grep -c "CanExecuteOnMachines"; done

release 20261107165102-initial (17:51):
   Discovery.Core.dll            -> CanExecuteOnMachines=0
   Discovery.Infrastructure.dll  -> CanExecuteOnMachines=0
   Discovery.Migrations.dll      -> CanExecuteOnMachines=0

release 20261107184431-initial (18:44):
   Discovery.Core.dll            -> CanExecuteOnMachines=3
   Discovery.Infrastructure.dll  -> CanExecuteOnMachines=1
   Discovery.Migrations.dll      -> CanExecuteOnMachines=1
```

A release 17:51 **nao referencia a propriedade em lugar nenhum** - seu modelo EF
nao conhece CanExecuteOnMachines. Isso e **seguro** por dois motivos tecnicos:

1. **A coluna nao existe no banco.** M279 nunca foi aplicada, entao o EF antigo
   consulta departments e encontra **exatamente** as colunas que conhece. Nao ha
   coluna "a mais" nem "a menos". **Compatibilidade perfeita.**
2. Mesmo que existisse, o EF Core projeta **colunas explicitas** por entidade
   (SELECT d.id, d.name, ...), nunca SELECT *. Uma coluna extra desconhecida no
   banco e inofensiva; o que quebra o EF e uma coluna **mapeada** que **falte** no
   banco - e e exatamente esse o caso da release 18:44.

**Nao existe nenhuma coluna mapeada pela 17:51 que esteja faltando no banco.** O
schema atual e um superconjunto estrito do que a 17:51 espera.

### 6.6 Risco residual: o papel Gestor do Departamento orfao

Apos o rollback, o banco continua com o papel 'Gestor do Departamento'
(is_system=true) e **13 grants** em role_permissions, criados por M278. A release
17:51 desconhece esse papel.

**Avaliacao: inofensivo.** O papel e uma **linha de dados**, nao estrutura. A API
antiga lista roles/role_permissions como tabelas genericas - ela nao tem
enum/allowlist de nomes de papel que rejeite desconhecidos. Nenhum JOIN da 17:51
depende de M278. Riscos concretos avaliados:

- **Nao ha erro de FK/constraint**: M278 inseriu apenas linhas; todas as FKs
  (role_permissions.role_id -> roles.id) estao satisfeitas.
- **Nao ha efeito de escalonamento de privilegio**: ninguem tem o papel atribuido
  ainda, e a 17:51 nao o usa em nenhuma policy.
- **Unico efeito visivel**: se houver uma tela de gestao de papeis, o novo papel
  aparece na lista. Como a 17:51 retorna o que esta no banco, o papel aparece mas
  **sem funcionalidade** - exatamente o cenario "inofensivo" que o Lead descreveu.

**Nao consegui testar isso em execucao** (nao posso subir a release antiga num
banco de teste). Registrado em 9 como limitacao.

---

## 7. Opcoes de recuperacao, ranqueadas por risco

### OPCAO A - Rollback do symlink para 20261107165102-initial + restart

| Dimensao | Avaliacao |
|---|---|
| **Risco** | **BAIXO** (o menor de todas as opcoes) |
| **Tempo ate o servico voltar** | ~10-30 segundos (troca de symlink + restart + health) |
| **Efeito colateral** | Perde-se a funcionalidade nova (D2 "executar na maquina" + M278 RBAC). Papel Gestor do Departamento fica orfao e inofensivo (6.6) |
| **Reversibilidade** | **ALTA** - e uma troca de symlink. Reapontar para 20261107184431-initial volta ao estado atual (quebrado). Ida e volta sao simetricos |
| **O que pode PIORAR** | (1) Se a 17:51 tambem falhar no boot por outro motivo, o servico continua fora - mas **nao piora**, porque ja esta fora. (2) O current muda para uma release que o cleanup_old_releases pode considerar removivel - **atencao**: com DISCOVERY_KEEP_RELEASES default 5, a 17:51 e a 2a mais recente, logo **nao seria podada**. (3) Perda de coleta durante a janela (ja perdida) |
| **Pre-condicao verificada** | Release completa (143 arquivos), binario executavel, delta = {M278,M279}, M278 = seed puro, coluna nao existe, EF antigo nao referencia a propriedade |

**Comando proposto (NAO executado - para o Lead):**

```bash
# 1. Guardar o alvo atual para poder voltar
readlink -f /opt/discovery-api/current

# 2. Reapontar para a release anterior
sudo -u discovery-api ln -sfn /opt/discovery-api/releases/20261107165102-initial /opt/discovery-api/current

# 3. Reiniciar e validar
sudo systemctl restart discovery-api
sleep 8
systemctl is-active discovery-api
curl -fsS --max-time 5 http://127.0.0.1:8080/health && echo " HEALTH OK"
journalctl -u discovery-api -n 30 --no-pager
```

**Validacao de aceite:** systemctl is-active = active **e**
curl http://127.0.0.1:8080/health responde 2xx.

---

### OPCAO B - Corrigir a SQL da M279 e republicar a release

| Dimensao | Avaliacao |
|---|---|
| **Risco** | **MEDIO** |
| **Tempo** | Minutos (build + publish + restart), mas o servico **so volta no fim** |
| **Efeito colateral** | **Nenhum** se a SQL estiver certa - o Up da M279 cria a coluna e roda o UPDATE |
| **Reversibilidade** | Media - a coluna passa a existir e o Down() a remove, mas voltar atras exige nova operacao |
| **O que pode PIORAR** | Ver alerta abaixo |

**ALERTA CRITICO que encontrei - o UPDATE da M279 e um no-op, e isso ENGANAVA:**

```
$ psql -c "SELECT (SELECT COUNT(*) FROM departments) AS depts,
                  (SELECT COUNT(*) FROM ticket_macros) AS macros;"
 depts | macros
-------+--------
     0 |      0
```

**departments tem 0 linhas e ticket_macros tem 0 linhas.** O UPDATE retroativo
que "habilita os departamentos que ja usam runbook" **nao afeta ninguem** - nao
porque o criterio e conservador, mas porque **o banco de producao nao tem
departamentos nem macros**. Consequencia pratica: depois da M279, todo
departamento futuro nasce com can_execute_on_machines = false (fail-closed), e
como nao ha departamento algum para habilitar, **o comportamento e o mesmo** -
mas a "rede de seguranca" que a migration prometia e **vazia por vacuidade**.

Isso **nao bloqueia** a opcao B, mas significa que o deploy de B **nao pode ser
validado por esse UPDATE**. E mais: se a intencao era preservar execucao para
departamentos existentes, **nao ha o que preservar**. Vale confirmar com o Lead se
isso e esperado (banco recem-limpo?) ou se aponta para um ambiente errado.

**Outros riscos de B:**

1. **Republish reexecuta M278.** Ela e idempotente (WHERE NOT EXISTS, comentario
   "Idempotente: pode ser reprocessada sem duplicar linhas", e meus dados mostram
   13 grants - nao 26). **Risco baixo**, mas verificar apos o boot.
2. **Republish reexecuta M279 do zero.** Como nao ha estado parcial (4.3), o
   AddColumn roda limpo. **Risco baixo.**
3. **O Down() de M278 contem DELETE FROM roles** - so roda em MigrateDown, que nao
   acontece no startup. **Risco nulo** no fluxo normal.
4. **Build novo pode trazer outras regressoes.** O commit aac4e82 mexeu em
   **33 arquivos** (3862 insercoes), incluindo TicketsController.cs (+244),
   TicketAccessPolicy.cs (+282) e controllers de AI/assignment. **Este e o risco
   real de B**: nao e a M279, e **todo o resto do commit** que entra junto.

---

### OPCAO C - Restaurar backup

| Dimensao | Avaliacao |
|---|---|
| **Risco** | **ALTO / INVIAVEL** |
| **Efeito colateral** | Perda de dados - o banco tem 140 MB de estado real |
| **Reversibilidade** | Baixa |

**INVIAVEL: nao existe backup algum.** Verifiquei exaustivamente:

```
$ crontab -l
no crontab for root

$ ls /var/backups/            # apenas packaging do dpkg/apt - nenhum dump
  alternatives.tar.0  apt.extended_states.0  dpkg.status.0 ...

$ ls /var/backups/discovery        -> No such file or directory
$ ls /opt/discovery-backups        -> No such file or directory
$ ls /var/lib/discovery-api/backups -> No such file or directory
$ ls /var/discovery/backups        -> No such file or directory

$ find / -maxdepth 5 -name '*discovery*.dump' -o -name '*discovery*.sql*' -o -name '*discovery*.backup'
(0 resultados)

$ psql -c "SHOW archive_mode;"      -> off
$ psql -c "SHOW archive_command;"   -> (disabled)
$ psql -c "SELECT * FROM pg_stat_archiver;"
 archived_count | last_archived_wal | ... | stats_reset
              0 |                   |     | 2026-10-18 13:41:34
```

**Zero backups, zero PITR, zero WAL archiving, zero cron.** Isso e um achado de
risco **independente do incidente atual** e deve virar acao separada.

---

### OPCAO D - Marcar M279 como aplicada a mao (UPDATE VersionInfo)

| Dimensao | Avaliacao |
|---|---|
| **Risco** | **ALTO - NAO RECOMENDADA** |
| **Por que** | Marcaria M279 como concluida **sem a coluna existir**. A release 18:44 mapeia Department.CanExecuteOnMachines; o EF consultaria can_execute_on_machines, receberia **42703 column does not exist**, e TicketAccessPolicy.cs:204 (.AnyAsync(d => d.Id == departmentId && d.CanExecuteOnMachines)) quebraria em runtime |
| **Efeito** | Troca um crash-loop **visivel** por um **erro silencioso e intermitente** em endpoints de tickets - muito pior de diagnosticar |
| **Reversibilidade** | Baixa |

**Descartada.** O enunciado a listava como hipotese, mas a verificacao de 4 mostra
que ela **nao e necessaria** (nao ha estado parcial) e **e ativamente perigosa**.

---

### Recomendacao final

> **Executar (A) agora**, validar o health, e **depois** decidir sobre (B) com
> calma, em janela controlada.
>
> **A e estritamente mais segura que B no momento**, porque:
> - recupera o servico em **segundos**, com **um** comando reversivel;
> - **nao** arrasta as outras 31 alteracoes do commit aac4e82;
> - tem pre-condicoes **verificadas por mim** (6), nao presumidas;
> - se falhar, o estado **nao piora** (o servico ja esta fora).
>
> **B nao deve ser tentada sob pressao.** O risco de B nao e a M279 - e o resto do
> commit. Fazer B depois de A, com o servico no ar, permite testar e reverter sem
> SLA em risco.

---

## 8. Ordem de execucao sugerida (para o Lead)

1. **(A)** rollback do symlink + restart + validar /health (7-A). ~30s.
2. Confirmar que os agents voltaram: tail -30 /var/log/nginx/error.log deve parar
   de acumular 502.
3. Confirmar VersionInfo intacto: SELECT COUNT(*) FROM "VersionInfo"; = **284**.
4. So depois, planejar **(B)** com janela: corrigir a SQL, subir, validar, e ter o
   plano de volta (7-A) a mao.
5. **Acao separada e urgente:** configurar backup/PITR. Hoje **nao existe nenhum**.

---

## 9. O QUE EU NAO CONSEGUI VERIFICAR

Registrado com honestidade - estes pontos sao **lacunas reais**, nao suposicoes:

1. **Por que a mensagem "Sem release anterior" foi emitida.** O deploy.sh atual
   **detecta corretamente** o alvo (5.2), e os tres clones em /tmp sao identicos
   por md5. **Nao ha log do instalador** das 18:44 preservado (/root/.install-*.log
   e de 14/10; grep -rl "Sem release anterior" so encontra o proprio script). Nao
   pude determinar se o caminho executado foi outro, se DISCOVERY_API_CURRENT
   chegou vazio, ou se houve corrida com a publicacao. **Nao consegui reproduzir.**

2. **Que a release 17:51 sobe de fato.** Verifiquei que ela e **compativel** (6) e
   completa (5.3), mas **nao a executei** - seria uma acao de producao. A prova
   definitiva e o /health apos o restart.

3. **Que a API antiga tolera o papel orfao em runtime.** Analisei estaticamente
   (6.6) e a conclusao e "inofensivo", mas **nao subi a 17:51 contra um banco em
   M278** para confirmar. Nao ha ambiente de staging.

4. **Historico exato de NRestarts.** Lido em **204**. Nao sei quantos desses
   restarts sao das 18:44 vs. reinicios administrativos anteriores (nao ha
   journalctl retido suficiente para separar).

5. **Se departments/ticket_macros vazios e estado esperado de producao.** 0 linhas
   em ambas (7-B) e surpreendente para um servico de producao com agents ativos.
   **Nao consegui determinar** se e ambiente intencionalmente limpo ou sintoma de
   outro problema. **Requer confirmacao do Lead/negocio.**

6. **Se o UPDATE da M279 esta semanticamente correto alem do erro de sintaxe.**
   Corrigi mentalmente a virgula, mas **nao validei** o EXPLAIN da versao corrigida
   (nao tenho o texto exato da correcao do Lead no servidor; a SQL corrigida nao
   esta na release atual). **Nao validei a logica de negocio** - apenas a sintaxe.

7. **Integridade do restante do commit aac4e82.** 33 arquivos, 3862 insercoes. Nao
   auditei TicketsController.cs (+244), TicketAccessPolicy.cs (+282), ScopeContext,
   nem os 4 controllers de AI/assignment. **Fora do escopo desta task** - se o Lead
   pretende ir para (B), isso merece auditoria propria.

8. **Estado do NATS/JetStream e RustFS.** Ambos tem listener ativo (:4222, :9000),
   mas **nao verifiquei** se acumularam backlog durante a indisponibilidade da API
   (~25 min). Pode haver fila de comandos represada.

---

## 10. Sumario executivo

| Item | Fato verificado |
|---|---|
| Servico | crash-loop, **204 restarts**, SIGABRT (status 6), activating (auto-restart) |
| API | **inteiramente 502** atras do nginx; porta 8080 sem listener; **agents falhando agora** |
| Causa raiz | M279: virgula no lugar de AND em Execute.Sql -> PostgreSQL **42601**, reproducido com EXPLAIN |
| **M279 em VersionInfo?** | **NAO** - topo e **M278** (20270112278); 284 linhas |
| **Coluna existe?** | **NAO** - information_schema count = **0**; \d departments tem 44 colunas, sem ela |
| Estado parcial | **NENHUM** - falha atomica; nada a limpar |
| Releases | **34**; "sem release anterior" era **FALSO**. Candidata 20261107165102-initial valida |
| Delta de migrations | **exatamente {M278, M279}**; 17:51 termina em **M277** |
| M278 tem DDL? | **NAO** no Up() (so 2 INSERT); tem DELETE FROM apenas no Down() - refutacao verificada |
| Compatibilidade 17:51 vs banco | **COMPATIVEL** - schema e superconjunto estrito; EF antigo nao referencia a propriedade |
| Backups | **NENHUM** - sem dump, sem PITR, archive_mode=off, sem cron |
| Disco/permissao | OK - 26% de uso, 2% de inodes |
| **Recomendacao** | **(A) rollback agora** (risco BAIXO, ~30s, reversivel); **(B) depois**, em janela |

---

### Como este relatorio foi produzido

Todas as evidencias vem de comandos **somente-leitura** executados via
ssh -o BatchMode=yes root@192.168.10.74 (sem -t, sem TTY). Nenhum servico foi
reiniciado, nenhum symlink alterado, nenhuma migration executada, e nenhum
UPDATE/DELETE/INSERT foi rodado no banco - apenas SELECT, \d e EXPLAIN (o EXPLAIN
foi usado para reproduzir o erro **sem executar** a escrita). A unica escrita em
disco foram arquivos temporarios de diagnostico em /tmp (/tmp/replay_rollback.sh,
/tmp/scan_m279.sh, /tmp/m_*.txt) e este relatorio.
