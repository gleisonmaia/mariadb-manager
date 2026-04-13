# Instruções — MariaDB Log Explorer

## 1. IDE e compilação

- **IDE recomendada:** Visual Studio 2022 (workload **Desenvolvimento para desktop com .NET**) ou **JetBrains Rider**, ou ainda **Visual Studio Code** com extensão C#.
- **SDK:** .NET 8 ou superior (o repositório usa `net8.0-windows` para o WPF).

### Compilar em modo debug

Na raiz do repositório:

```powershell
dotnet build MesasLog.sln -c Debug
```

O executável de desenvolvimento fica em:

`src\MesasLog.Wpf\bin\Debug\net8.0-windows\MariaDBLogExplorer.exe`

Em desenvolvimento, `appsettings.json` e `mesas.sql` são copiados para a pasta de saída. Os mesmos ficheiros estão **embutidos** no assembly para o executável único de distribuição.

### Publicar para distribuição (recomendado): launcher + WPF single-file sem runtime embutido

- **`MariaDBLogExplorer.Launcher.exe`** (na **raiz** da pasta publicada): console **self-contained** em arquivo único (~tamanho de dezenas de MB). Verifica se existe o **Windows Desktop Runtime .NET 8**; se não houver, oferece **baixar e instalar** o instalador oficial da Microsoft e só então inicia o WPF.
- **`app\MariaDBLogExplorer.exe`**: **single-file** em modo **framework-dependent** (interface WPF) — as **DLLs da aplicação e dependências NuGet** vão empacotadas no `.exe` (~ordem de **4–6 MB**), mas o **runtime .NET (WPF)** **não** é embutido; usa o instalado no Windows (o launcher garante isso).

**Limitação do SDK:** compressão do single-file (`EnableCompressionInSingleFile`) **só** é permitida com **self-contained**; por isso o WPF FDD publica single-file **sem** essa compressão.

Na raiz do repositório:

```powershell
.\publish-release.ps1
```

O script **limpa** a pasta de saída, publica o launcher na raiz, o WPF em **`app\`**, copia `bootstrapper.settings.json` (exemplo), remove cópias soltas de `appsettings.json` / `mesas.sql` em `app\` (o padrão continua **embutido** no assembly). **Distribua a pasta inteira** (raiz + **`app\`**). O utilizador deve abrir **`MariaDBLogExplorer.Launcher.exe`** na raiz. Opcional: **`app\appsettings.json`** para personalizar. Atalhos em **`app\`**: `Iniciar Maria DB Log Explorer.bat`, `Maria DB Log Explorer.lnk` (Windows; use `-NoShortcut` em CI).

**Visual Studio / MSBuild:** `dotnet msbuild MesasLog.Publish.proj -t:PublishRelease`. Parâmetros: `/p:PublishDir=...`, `/p:MesasLogRid=win-x64`.

**CI (GitHub Actions):** `publish-release.ps1 -NoShortcut`; o artefato é a pasta `publish` completa.

```powershell
.\publish-release.ps1 -Output C:\dist -Rid win-x64
.\publish-release.ps1 -NoShortcut
```

**Publicação manual equivalente:**

```powershell
dotnet publish src\MesasLog.Bootstrapper\MesasLog.Bootstrapper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
dotnet publish src\MesasLog.Wpf\MesasLog.Wpf.csproj -c Release -r win-x64 --self-contained false -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o publish\app
```

O projeto **`MesasLog.StartupHook`** não é usado neste fluxo (era para DLLs em subpasta com host FDD antigo).

## 2. Configuração (`appsettings.json`)

Valores embutidos no `MariaDBLogExplorer.exe` (WPF em `app\`); um **`appsettings.json` opcional** na pasta **`app\`** (ao lado do executável) é carregado depois e **substitui** chaves iguais.

- **`MesasLog:Database`:** conexão com o banco **mesas_log** (ou outro nome configurado). Na primeira execução, o aplicativo executa `CREATE DATABASE IF NOT EXISTS` com esse nome (é necessário que o usuário MariaDB tenha permissão **CREATE** no servidor). Se preferir criar o banco manualmente, pode fazê-lo antes: `CREATE DATABASE mesas_log CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;`
- **`MesasLog:Binlog`:** `BinlogDirectory` (vazio tenta detecção), `MysqlBinlogPath` (vazio tenta PATH/instalação), `MysqlBinlogTimeoutSeconds`.
- **`MesasLog:Processing`:** tamanho de lote, dias de retenção, nome do banco cujo binlog ROW será ingerido (`TargetDatabase`, correspondência exata; predefinição `mesas`), política de conflito em INSERT (`InsertConflict`: `Ignore`, `Overwrite`, `Fail`) e validação (`OnInconsistency`: `Ignore`, `Abort`).
- **`MesasLog:Logging`:** pasta de logs (relativa ao diretório do executável) e nível mínimo (`Verbose`, `Debug`, `Information`, `Warning`, `Error`).
- **`MesasLog:Ui`:** `MaxPageSize` e `DefaultPageSize` para a grade de consulta.

**Segurança:** não versione senhas em repositórios públicos; use variáveis de ambiente ou cofre conforme a política da empresa.

## 3. Uso do executável

Na distribuição publicada, abra **`MariaDBLogExplorer.Launcher.exe`** na raiz da pasta (não use só `app\MariaDBLogExplorer.exe` diretamente a menos que o Desktop Runtime .NET 8 já esteja instalado — o launcher na raiz é o fluxo recomendado).

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
