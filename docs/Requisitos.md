# Requisitos do projeto Mesas Log (MariaDB binlog)

Este documento resume o escopo definido para o leitor de binary log do MariaDB, armazenamento em banco de log e interface WPF.

## 1. Leitura e processamento do binlog (formato ROW)

- Utilizar o utilitário `mysqlbinlog` instalado com o MariaDB/MySQL, com parâmetros que produzam saída legível (`--verbose`, `--base64-output=DECODE-ROWS`).
- Detectar automaticamente o caminho do `mysqlbinlog` e, opcionalmente, o diretório de binlogs; permitir configuração manual. Avisar se não forem encontrados.
- Controlar arquivo e posição do binlog; persistir checkpoint em banco; evitar reprocessamento (unicidade por `binlog_file`, `binlog_position`, `banco`, `tabela`).
- Parser da saída textual: tipo de operação, banco, tabela, dados before/after; reconstruir SQL quando possível; caso contrário, manter JSON estruturado (colunas e valores tipados).
- Filtrar bancos cujo nome atende ao prefixo configurado (padrão equivalente a `mesas%`).
- Tratar transações (`BEGIN`/`COMMIT`) e persistência em lote coerente com o binlog; suportar modo dry-run.
- Tratar erros: falha ao executar `mysqlbinlog`, binlog corrompido/ilegível, falha de conexão com o banco de destino.
- Timeout configurável para execução do `mysqlbinlog`, com cancelamento seguro.
- Ignorar eventos DDL na camada de linhas ROW; validar imagens INSERT/UPDATE/DELETE conforme regras de consistência before/after.
- Conflitos de INSERT na reconstrução/replay configuráveis: ignorar, sobrescrever ou falhar.
- Normalizar nomes de banco e tabela (case) no armazenamento.

## 2. Banco de log (MariaDB)

- Configuração via `appsettings.json` (host, porta, banco, usuário, senha).
- Tabela `binlog_event_log` com campos: id, data_evento, banco, tabela, tipo_operacao, dados_antes (JSON), dados_depois (JSON), query_sql, binlog_file, binlog_position.
- Índices em data_evento, banco, tabela; restrição de unicidade na combinação indicada.
- Conexão via **MySqlConnector**.
- Campo `data_evento` a partir do timestamp do evento no binlog quando disponível.

## 3. Snapshot de tabelas (`mesas.sql`)

- Arquivo `mesas.sql` ao lado do executável, apenas `CREATE TABLE`.
- Criar no banco de log tabelas equivalentes com prefixo `log_` para suportar reconstrução dos dados das famílias `mesa*` e `a_caixa_delivery*`.

## 4. Reconstrução / replay

- Permitir reconstruir estado atual aplicando eventos em ordem; processo idempotente (ex.: truncar `log_*` e reaplicar a partir do log persistido).
- Respeitar política de conflito de INSERT configurada na reescrita de SQL (INSERT IGNORE / REPLACE / INSERT simples).

## 5. Aplicação desktop (WPF, MVVM)

- Abas ou áreas para: análise de logs (sub-abas de processamento passo a passo e consulta à tabela de log).
- Consulta com filtros: banco, tabela, intervalo de datas, tipo de operação, texto em `query_sql`; ordenação por data_evento, banco, tabela; paginação com tamanho máximo configurável; total de registros; indicador de carregamento; cancelamento de consulta longa.

## 6. Arquitetura e implantação

- Camadas: **Core** (modelos/opções), **Infrastructure** (dados, binlog, logging em arquivo), **Application** (orquestração, validação, replay), **UI** (WPF).
- Destino de compilação: **.NET Framework 4.8** (Windows). Distribuição: pasta com **launcher** na raiz (verifica instalação do .NET Framework 4.8) e aplicação WPF em **`app\`** (executáveis com dependências geridas fundidas via Costura.Fody; ver [Instrucoes.md](Instrucoes.md) e `publish-release.ps1` na raiz do repositório). O runtime .NET Framework 4.8 é um pré-requisito comum em Windows 10/11 ou instalável separadamente em versões anteriores.

## 7. Operação

- Processamento sob demanda (sem agendador interno).
- Processamento em lote; ordem cronológica e por posição de binlog.
- Instância única do processo (mutex global).
- Retenção configurável de registros antigos na tabela de log (limpeza por idade).
- Logs da aplicação (informação, erro, debug) em diretório configurável.

## Limitações conhecidas da implementação inicial

- O parser depende do formato textual do `mysqlbinlog` na versão do servidor; variações de saída podem exigir ajustes.
- Colunas nos eventos ROW aparecem como `@1`, `@2`, … na reconstrução SQL, salvo quando o formato do binlog trouxer nomes reais.
- Replay baseia-se em `query_sql` reescrita para tabelas `log_*`; eventos sem SQL reconstruído não são aplicados no replay automático.
- Consultas que carregam todos os eventos para replay podem ser pesadas em bases muito grandes (carregamento completo em memória).
