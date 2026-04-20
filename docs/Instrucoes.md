# Instruções — MariaDB Log Explorer

## 1. IDE e compilação

- **IDE recomendada:** Visual Studio 2022 (workload **Desenvolvimento para desktop com .NET** e **.NET Framework 4.8**), **JetBrains Rider**, ou **Visual Studio Code** com extensão C#.
- **SDK:** .NET 9 ou superior (para `dotnet build` / `dotnet publish` dos projetos SDK-style que visam **`net48`**). No Windows, o **Pacote de desenvolvimento do .NET Framework 4.8** (referências de compilação) costuma ser necessário para compilar projetos `net48`.

### Compilar em modo debug

Na raiz do repositório:

```powershell
dotnet build MesasLog.sln -c Debug
```

O executável de desenvolvimento fica em:

`src\MesasLog.Wpf\bin\Debug\net48\MariaDBLogExplorer.exe`

Em desenvolvimento, `appsettings.json` e `mesas.sql` são copiados para a pasta de saída. Os mesmos ficheiros estão **embutidos** no assembly para distribuição (cópia opcional removida na pasta `app\` pelo script de publicação).

### Publicar para distribuição (recomendado): launcher na raiz + conteúdo em `app\`

- **`MariaDBLogExplorer.Launcher.exe`** (só este ficheiro na **raiz** da pasta publicada): console (empacotado com **Costura.Fody**) que verifica se o **.NET Framework 4.8** está instalado (registro Windows, `Release >= 528040`). O ficheiro **`MariaDBLogExplorer.Launcher.exe.config`** é colocado em **`app\`**; o launcher aponta para ele no arranque.
- **`app\MariaDBLogExplorer.exe`**: interface WPF (.NET Framework 4.8), empacotada com **Costura.Fody** (dependências geridas fundidas no `.exe`). Na pasta **`app\`** ficam os `.exe.config` (launcher e WPF), o executável WPF e ficheiros opcionais (`bootstrapper.settings.json`, etc.).

Na raiz do repositório:

```powershell
.\publish-release.ps1
```

O script **limpa** a pasta de saída, publica o launcher na raiz, o WPF em **`app\`**, copia `bootstrapper.settings.json` (exemplo), remove cópias soltas de `appsettings.json` / `mesas.sql` em `app\` (o padrão continua **embutido** no assembly). **Distribua a pasta inteira** (raiz + **`app\`**). O utilizador deve abrir **`MariaDBLogExplorer.Launcher.exe`** na raiz. Opcional: **`app\appsettings.json`** para personalizar. Atalhos em **`app\`**: `Iniciar Maria DB Log Explorer.bat`, `Maria DB Log Explorer.lnk` (Windows; use `-NoShortcut` em CI).

**Visual Studio / MSBuild:** `dotnet msbuild MesasLog.Publish.proj -t:PublishRelease`. Parâmetros: `/p:PublishDir=...`.

**CI (GitHub Actions):** `publish-release.ps1 -NoShortcut`; o artefato é a pasta `publish` completa.

```powershell
.\publish-release.ps1 -Output C:\dist
.\publish-release.ps1 -NoShortcut
```

**Publicação manual equivalente:**

```powershell
dotnet publish src\MesasLog.Bootstrapper\MesasLog.Bootstrapper.csproj -c Release -p:DebugType=none -o publish
dotnet publish src\MesasLog.Wpf\MesasLog.Wpf.csproj -c Release -p:DebugType=none -o publish\app
```

O projeto **`MesasLog.StartupHook`** foi removido (era específico do host .NET Core e não era usado no fluxo de distribuição).

## 2. Configuração (`appsettings.json`)

Valores embutidos no `MariaDBLogExplorer.exe` (WPF em `app\`); um **`appsettings.json` opcional** na pasta **`app\`** (ao lado do executável) é carregado depois e **substitui** chaves iguais.

- **`MesasLog:Database`:** conexão com o banco **mesas_log** (ou outro nome configurado). Na primeira execução, o aplicativo executa `CREATE DATABASE IF NOT EXISTS` com esse nome (é necessário que o usuário MariaDB tenha permissão **CREATE** no servidor). Se preferir criar o banco manualmente, pode fazê-lo antes: `CREATE DATABASE mesas_log CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;`
- **`MesasLog:Binlog`:** `BinlogDirectory` (vazio tenta detecção), `MysqlBinlogPath` (vazio tenta PATH/instalação), `MysqlBinlogTimeoutSeconds`.
- **`MesasLog:Processing`:** tamanho de lote, dias de retenção, nome do banco cujo binlog ROW será ingerido (`TargetDatabase`, correspondência exata; predefinição `mesas`), política de conflito em INSERT (`InsertConflict`: `Ignore`, `Overwrite`, `Fail`) e validação (`OnInconsistency`: `Ignore`, `Abort`).
- **`MesasLog:Logging`:** pasta de logs (relativa ao diretório do executável) e nível mínimo (`Verbose`, `Debug`, `Information`, `Warning`, `Error`).
- **`MesasLog:Ui`:** `MaxPageSize` e `DefaultPageSize` para a grade de consulta.

**Segurança:** não versione senhas em repositórios públicos; use variáveis de ambiente ou cofre conforme a política da empresa.

## 3. Uso do executável

Na distribuição publicada, abra **`MariaDBLogExplorer.Launcher.exe`** na raiz da pasta (fluxo recomendado: verifica .NET Framework 4.8 antes do WPF).

1. Garanta que o **MariaDB/MySQL Client** esteja instalado e que `mysqlbinlog.exe` seja encontrado (ou informe o caminho completo em `MysqlBinlogPath`).
2. Configure o **diretório dos arquivos de binlog** (`BinlogDirectory`) se a detecção automática falhar.
3. Ajuste `mesas.sql` com os `CREATE TABLE` reais das tabelas `mesa*` e `a_caixa_delivery*` (snapshot inicial); na primeira execução o app cria `binlog_event_log`, checkpoint e tabelas `log_*` derivadas.
4. **Aba Processamento**
   - **Dry-run:** valida leitura/parse sem gravar no banco de log.
   - **Processar binlog:** lê a partir do checkpoint, grava eventos e atualiza checkpoint.
   - **Replay:** trunca tabelas `log_*` no banco configurado e reaplica os SQLs armazenados (com política de INSERT configurada). Use dry-run para simular.
   - **Reaplicar esquema / mesas.sql:** recria/aplica estruturas conforme o arquivo.
5. **Aba Consulta de eventos:** filtros, ordenação, paginação, cancelamento de consulta.

## 4. NuGet e rede

Foi adicionado `nuget.config` na raiz do repositório para usar apenas **nuget.org**, evitando falha de restauração quando feeds privados retornam 401. Se precisar de pacotes internos, ajuste o arquivo com cuidado.

## 5. Solução de problemas

- **mysqlbinlog não encontrado:** instale MariaDB Server/Client ou defina `MysqlBinlogPath`.
- **Nenhum binlog listado:** verifique `BinlogDirectory` e o padrão de nomes dos arquivos (`*-bin.NNNNNN` ou semelhante); ajuste o scanner se sua instalação usar outro padrão.
- **Conexão com o banco:** teste host/porta/firewall e usuário com permissão em `mesas_log`.
- **Segunda instância:** o app usa mutex global; apenas uma instância por usuário/sessão conforme implementado.
- **.NET Framework 4.8 em falta:** o launcher indica a página de download; em servidores sem browser, instale o pacote offline da Microsoft para o .NET Framework 4.8.
