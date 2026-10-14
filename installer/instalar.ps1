<#
.SYNOPSIS
    Instala ou atualiza o ZapMQ 2 e o Worker Control 2 nesta máquina, e troca uma instalação 1.x
    que esteja em uso.

.DESCRIPTION
    Para cada um dos dois, o que já existe decide o que é feito:

    - nada instalado: cria a pasta, copia os arquivos, gera a configuração inicial, registra o
      serviço e inicia;
    - 1.x instalado: o ZapMQ 2 ganha um serviço próprio (ZapMQServer) e o 1.x fica parado, em
      início manual, para poder voltar; o Worker Control mantém o mesmo serviço
      (WorkerControlService), apontado para o executável novo, com o mesmo ConfigWorkers.json. O
      desinstalador do 1.x nunca é usado: ele apagaria o serviço do Worker Control 2, que tem o
      mesmo nome;
    - 2.x instalado: guarda o executável atual com a versão no nome, coloca o novo e inicia. A
      configuração e os dados ficam como estão.

    Depois de iniciar, confere que respondeu. Se não respondeu, volta ao que estava antes e diz o
    que aconteceu. Tudo o que faz fica num log ao lado deste script.

.PARAMETER Pasta
    Onde ficam as pastas ZapMQ e WorkerControl numa instalação nova (ou na troca do 1.x).
    Numa atualização, vale a pasta em que cada um já está.

.PARAMETER Ambiente
    Nome do ambiente nos pacotes de transporte: DEV, QAS, PRD. Exigido ao instalar o ZapMQ pela
    primeira vez.

.PARAMETER PortaFila
    Porta da fila. Em branco: a do ZapMQ 1.x, se houver, ou 5679.

.PARAMETER PortaPainel
    Porta do painel, só local, atrás do proxy reverso. Padrão: 5680.

.PARAMETER Somente
    Tudo (padrão), ZapMQ ou WorkerControl.

.PARAMETER Simular
    Mostra o que seria feito, sem mudar nada.

.EXAMPLE
    .\instalar.ps1 -Simular -Pasta D:\OjiTec -Ambiente QAS

.EXAMPLE
    .\instalar.ps1 -Pasta D:\OjiTec -Ambiente QAS
#>
[CmdletBinding()]
param(
    [string]$Pasta = 'D:\Apps',
    [string]$Ambiente = '',
    [int]$PortaFila = 0,
    [int]$PortaPainel = 5680,
    [ValidateSet('Tudo', 'ZapMQ', 'WorkerControl')]
    [string]$Somente = 'Tudo',
    [switch]$Simular
)

$ErrorActionPreference = 'Stop'
$Script:Simulando = [bool]$Simular
$Script:Origem = $PSScriptRoot
$Script:Feito = New-Object System.Collections.Generic.List[string]

$ZapMQServico = 'ZapMQServer'
$ZapMQServico1 = 'ZapMQservice'
$AgenteServico = 'WorkerControlService'
$AgenteArquivos = @('WorkerControl.exe', 'e_sqlite3.dll', 'Microsoft.Data.SqlClient.SNI.dll')

# ---------------------------------------------------------------- o que toca o sistema
# Cada função abaixo é o único lugar em que o script consulta ou muda a máquina.

function Get-Servico([string]$Nome) {
    $item = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Nome'" -ErrorAction SilentlyContinue
    if (-not $item) { return $null }
    # O caminho registrado pode vir entre aspas e com argumentos.
    $caminho = $item.PathName
    if ($caminho -match '^"([^"]+)"') { $exe = $Matches[1] } else { $exe = ($caminho -split ' ')[0] }
    [pscustomobject]@{ Nome = $item.Name; Exe = $exe; Estado = $item.State; Inicio = $item.StartMode }
}

function Get-Versao([string]$Arquivo) {
    if (-not (Test-Path -LiteralPath $Arquivo)) { return $null }
    $info = (Get-Item -LiteralPath $Arquivo).VersionInfo
    $texto = $info.ProductVersion
    if (-not $texto) { $texto = $info.FileVersion }
    if (-not $texto) { return $null }
    # "2.11.1+abc" ou "2.11.1.0": fica o que interessa.
    $texto = ($texto -split '[+ ]')[0]
    $partes = $texto.Split('.')
    if ($partes.Count -ge 3) { return ($partes[0..2] -join '.') }
    return $texto
}

function Invoke-Sc([string[]]$Argumentos) {
    $saida = & sc.exe @Argumentos 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Argumentos -join ' '): $($saida.Trim())" }
}

function Stop-Servico([string]$Nome) {
    Stop-Service -Name $Nome -Force -ErrorAction Stop
    (Get-Service -Name $Nome).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(4))
}

function Start-Servico([string]$Nome) {
    Start-Service -Name $Nome -ErrorAction Stop
}

function Set-Inicio([string]$Nome, [string]$Modo) {
    Set-Service -Name $Nome -StartupType $Modo
}

function Test-ZapMQ([int]$Porta) {
    for ($tentativa = 0; $tentativa -lt 30; $tentativa++) {
        try {
            $resposta = Invoke-RestMethod -Uri "http://localhost:$Porta/health" -TimeoutSec 3
            if ($resposta.status -eq 'ok' -or "$resposta" -match 'ok') { return $true }
        }
        catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Test-Agente([string]$PastaAgente, [datetime]$Desde) {
    $logs = Join-Path $PastaAgente 'logs'
    for ($tentativa = 0; $tentativa -lt 45; $tentativa++) {
        $servico = Get-Service -Name $AgenteServico -ErrorAction SilentlyContinue
        if ($servico -and $servico.Status -eq 'Running' -and (Test-Path $logs)) {
            $ultimo = Get-ChildItem $logs -Filter 'workercontrol-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($ultimo -and $ultimo.LastWriteTime -ge $Desde -and (Select-String -Path $ultimo.FullName -Pattern 'ServiceStarted' -Quiet)) { return $true }
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Read-PortaDo1x([string]$Exe) {
    $ini = Join-Path (Split-Path $Exe) 'ZapMQ.ini'
    if (Test-Path $ini) {
        $linha = Select-String -Path $ini -Pattern '^\s*Port\s*=\s*(\d+)' | Select-Object -First 1
        if ($linha) { return [int]$linha.Matches[0].Groups[1].Value }
    }
    return 0
}

# ---------------------------------------------------------------- passos

function Passo([string]$Descricao, [scriptblock]$Acao) {
    if ($Script:Simulando) {
        Write-Host "  [simulação] $Descricao" -ForegroundColor DarkGray
        return
    }
    Write-Host "  $Descricao"
    & $Acao
    $Script:Feito.Add($Descricao) | Out-Null
}

function Copiar([string]$De, [string]$Para) {
    Passo "copiar $(Split-Path $De -Leaf) para $Para" { Copy-Item -LiteralPath $De -Destination $Para -Force }
}

function Guardar([string]$Exe, [string]$Versao) {
    # O executável que sai fica ao lado, com a versão no nome, para voltar renomeando.
    $nome = [IO.Path]::GetFileNameWithoutExtension($Exe)
    $pasta = Split-Path $Exe
    $destino = Join-Path $pasta "$nome.$Versao.exe"
    if (Test-Path -LiteralPath $destino) { $destino = Join-Path $pasta ("$nome.$Versao." + (Get-Date -Format 'yyyyMMddHHmmss') + '.exe') }
    Passo "guardar o executável atual como $(Split-Path $destino -Leaf)" { Rename-Item -LiteralPath $Exe -NewName (Split-Path $destino -Leaf) }
    return $destino
}

function Escrever([string]$Arquivo, [string]$Texto) {
    Passo "criar $Arquivo" { [IO.File]::WriteAllText($Arquivo, $Texto, (New-Object System.Text.UTF8Encoding($false))) }
}

function Criar-Pasta([string]$Caminho) {
    if (-not (Test-Path -LiteralPath $Caminho)) { Passo "criar a pasta $Caminho" { New-Item -ItemType Directory -Path $Caminho | Out-Null } }
}

# ---------------------------------------------------------------- ZapMQ

function Instalar-ZapMQ {
    $novo = Join-Path $Script:Origem 'ZapMQ\ZapMQ.exe'
    $versaoNova = Get-Versao $novo
    Write-Host ''
    Write-Host "ZapMQ $versaoNova" -ForegroundColor Cyan

    $atual = Get-Servico $ZapMQServico
    if ($atual) {
        $versaoAtual = Get-Versao $atual.Exe
        if ($versaoAtual -eq $versaoNova) { Write-Host "  já está na $versaoNova em $($atual.Exe)"; return }
        Write-Host "  atualizar de $versaoAtual para $versaoNova em $(Split-Path $atual.Exe)"
        $porta = Ler-PortaConfigurada (Split-Path $atual.Exe)
        Passo "parar $ZapMQServico" { Stop-Servico $ZapMQServico }
        $guardado = Guardar $atual.Exe $versaoAtual
        Copiar $novo $atual.Exe
        Passo "iniciar $ZapMQServico" { Start-Servico $ZapMQServico }
        if (-not $Script:Simulando -and -not (Test-ZapMQ $porta)) {
            Write-Warning "O ZapMQ $versaoNova não respondeu: voltando à $versaoAtual."
            Stop-Servico $ZapMQServico
            Remove-Item -LiteralPath $atual.Exe -Force
            Rename-Item -LiteralPath $guardado -NewName (Split-Path $atual.Exe -Leaf)
            Start-Servico $ZapMQServico
            throw "O ZapMQ $versaoNova não respondeu em http://localhost:$porta/health. A $versaoAtual voltou ao ar. Veja o log em $(Join-Path (Split-Path $atual.Exe) 'logs')."
        }
        return
    }

    # Primeira vez: ao lado de um 1.x, ou sozinho.
    if (-not $Ambiente) { throw 'Informe -Ambiente (DEV, QAS, PRD): é o nome que este ambiente terá nos pacotes de transporte.' }
    $antigo = Get-Servico $ZapMQServico1
    $porta = $PortaFila
    if ($porta -eq 0 -and $antigo) { $porta = Read-PortaDo1x $antigo.Exe }
    if ($porta -eq 0) { $porta = 5679 }
    $pastaZap = Join-Path $Pasta 'ZapMQ'
    $exe = Join-Path $pastaZap 'ZapMQ.exe'
    if ($antigo) { Write-Host "  o ZapMQ 1.x ($($antigo.Exe)) fica parado, em início manual, para poder voltar" }
    Write-Host "  instalar em $pastaZap, fila na porta $porta, painel na $PortaPainel, ambiente $Ambiente"

    Criar-Pasta $pastaZap
    Copiar $novo $exe
    $config = Join-Path $pastaZap 'appsettings.json'
    if (-not (Test-Path -LiteralPath $config)) {
        Escrever $config (@"
{
  "ZapMQ": {
    "Port": $porta,
    "Panel": { "Port": $PortaPainel, "BasePath": "/zapmq" },
    "Transport": { "Environment": "$Ambiente" }
  }
}
"@)
    }
    Passo "registrar o serviço $ZapMQServico" {
        Invoke-Sc @('create', $ZapMQServico, 'binPath=', "`"$exe`"", 'start=', 'auto', 'DisplayName=', 'ZapMQ 2.0')
        Invoke-Sc @('description', $ZapMQServico, 'Broker de mensagens ZapMQ e o painel')
        Invoke-Sc @('failure', $ZapMQServico, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/60000')
    }
    if ($antigo) {
        if ($antigo.Estado -eq 'Running') { Passo "parar o ZapMQ 1.x ($ZapMQServico1)" { Stop-Servico $ZapMQServico1 } }
        Passo "deixar o ZapMQ 1.x em início manual" { Set-Inicio $ZapMQServico1 'Manual' }
    }
    Passo "iniciar $ZapMQServico" { Start-Servico $ZapMQServico }
    if (-not $Script:Simulando -and -not (Test-ZapMQ $porta)) {
        Write-Warning 'O ZapMQ 2 não respondeu: voltando ao que estava.'
        Stop-Servico $ZapMQServico
        Set-Inicio $ZapMQServico 'Manual'
        if ($antigo) { Set-Inicio $ZapMQServico1 'Automatic'; Start-Servico $ZapMQServico1 }
        throw "O ZapMQ 2 não respondeu em http://localhost:$porta/health. $(if ($antigo) { 'O 1.x voltou ao ar. ' })Veja o log em $(Join-Path $pastaZap 'logs')."
    }
    $Script:Nginx = $PortaPainel
}

function Ler-PortaConfigurada([string]$PastaZap) {
    $config = Join-Path $PastaZap 'appsettings.json'
    if (Test-Path -LiteralPath $config) {
        try {
            $lido = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
            if ($lido.ZapMQ.Port) { return [int]$lido.ZapMQ.Port }
        }
        catch { }
    }
    return 5679
}

# ---------------------------------------------------------------- Worker Control

function Instalar-Agente {
    $origem = Join-Path $Script:Origem 'WorkerControl'
    $novo = Join-Path $origem 'WorkerControl.exe'
    $versaoNova = Get-Versao $novo
    Write-Host ''
    Write-Host "Worker Control $versaoNova" -ForegroundColor Cyan

    $atual = Get-Servico $AgenteServico
    $versaoAtual = if ($atual) { Get-Versao $atual.Exe } else { $null }
    $ehNovo = $versaoAtual -and ([int]($versaoAtual.Split('.')[0]) -ge 2)

    if ($ehNovo) {
        if ($versaoAtual -eq $versaoNova) { Write-Host "  já está na $versaoNova em $($atual.Exe)"; return }
        $pastaAgente = Split-Path $atual.Exe
        Write-Host "  atualizar de $versaoAtual para $versaoNova em $pastaAgente"
        Passo "parar $AgenteServico (os micro serviços param junto)" { Stop-Servico $AgenteServico }
        $guardado = Guardar $atual.Exe $versaoAtual
        foreach ($arquivo in $AgenteArquivos) { Copiar (Join-Path $origem $arquivo) (Join-Path $pastaAgente $arquivo) }
        $inicio = Get-Date
        Passo "iniciar $AgenteServico" { Start-Servico $AgenteServico }
        if (-not $Script:Simulando -and -not (Test-Agente $pastaAgente $inicio)) {
            Write-Warning "O Worker Control $versaoNova não iniciou: voltando à $versaoAtual."
            Stop-Servico $AgenteServico
            Remove-Item -LiteralPath $atual.Exe -Force
            Rename-Item -LiteralPath $guardado -NewName (Split-Path $atual.Exe -Leaf)
            Start-Servico $AgenteServico
            throw "O Worker Control $versaoNova não iniciou. A $versaoAtual voltou ao ar. Veja o log em $(Join-Path $pastaAgente 'logs')."
        }
        return
    }

    $pastaAgente = Join-Path $Pasta 'WorkerControl'
    $exe = Join-Path $pastaAgente 'WorkerControl.exe'
    Criar-Pasta $pastaAgente
    foreach ($arquivo in $AgenteArquivos) { Copiar (Join-Path $origem $arquivo) (Join-Path $pastaAgente $arquivo) }
    $config = Join-Path $pastaAgente 'ConfigWorkers.json'

    if ($atual) {
        # 1.x em uso: o mesmo serviço passa a rodar o executável novo, com a mesma configuração.
        $exeAntigo = $atual.Exe
        Write-Host "  trocar o Worker Control 1.x ($($atual.Exe)) pelo 2 em $pastaAgente, no mesmo serviço"
        Write-Host '  o desinstalador do 1.x NÃO é usado: ele apagaria o serviço do 2'
        $configAntiga = Join-Path (Split-Path $atual.Exe) 'ConfigWorkers.json'
        if (-not (Test-Path -LiteralPath $config)) {
            if (-not (Test-Path -LiteralPath $configAntiga)) { throw "Não encontrei o ConfigWorkers.json do 1.x em $configAntiga." }
            Copiar $configAntiga $config
        }
        Passo "parar $AgenteServico 1.x (os micro serviços param junto)" { if ($atual.Estado -eq 'Running') { Stop-Servico $AgenteServico } }
        Passo "apontar $AgenteServico para $exe" {
            Invoke-Sc @('config', $AgenteServico, 'binPath=', "`"$exe`"", 'start=', 'auto')
            Invoke-Sc @('failure', $AgenteServico, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/60000')
        }
        $inicio = Get-Date
        Passo "iniciar $AgenteServico" { Start-Servico $AgenteServico }
        if (-not $Script:Simulando -and -not (Test-Agente $pastaAgente $inicio)) {
            Write-Warning 'O Worker Control 2 não iniciou: voltando ao 1.x.'
            Stop-Servico $AgenteServico
            Invoke-Sc @('config', $AgenteServico, 'binPath=', "`"$exeAntigo`"")
            Start-Servico $AgenteServico
            throw "O Worker Control 2 não iniciou. O 1.x voltou ao ar. Veja o log em $(Join-Path $pastaAgente 'logs')."
        }
        return
    }

    # Nada instalado.
    $porta = $PortaFila
    if ($porta -eq 0) { $porta = 5679 }
    Write-Host "  instalar em $pastaAgente, falando com o ZapMQ em localhost:$porta"
    if (-not (Test-Path -LiteralPath $config)) {
        Escrever $config (@"
{
  "ZapMQHost": "localhost",
  "ZapMQPort": $porta,
  "WorkerGroups": []
}
"@)
    }
    Passo "registrar o serviço $AgenteServico" {
        Invoke-Sc @('create', $AgenteServico, 'binPath=', "`"$exe`"", 'start=', 'auto', 'DisplayName=', 'WorkerControl')
        Invoke-Sc @('description', $AgenteServico, 'Mantém no ar os micro serviços configurados')
        Invoke-Sc @('failure', $AgenteServico, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/60000')
    }
    $inicio = Get-Date
    Passo "iniciar $AgenteServico" { Start-Servico $AgenteServico }
    if (-not $Script:Simulando -and -not (Test-Agente $pastaAgente $inicio)) {
        throw "O Worker Control não iniciou. Veja o log em $(Join-Path $pastaAgente 'logs')."
    }
}

# ---------------------------------------------------------------- execução

function Executar {
    $log = Join-Path $Script:Origem ('instalar-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    if (-not $Script:Simulando) { Start-Transcript -Path $log | Out-Null }
    try {
        $identidade = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not $identidade.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Abra o PowerShell como administrador.'
        }
        foreach ($arquivo in @('ZapMQ\ZapMQ.exe', 'WorkerControl\WorkerControl.exe')) {
            if (-not (Test-Path (Join-Path $Script:Origem $arquivo))) { throw "Falta $arquivo ao lado deste script: extraia o zip inteiro." }
        }
        if ($Script:Simulando) { Write-Host 'SIMULAÇÃO: nada é alterado.' -ForegroundColor Yellow }

        # O broker antes: o agente fala por ele.
        if ($Somente -ne 'WorkerControl') { Instalar-ZapMQ }
        if ($Somente -ne 'ZapMQ') { Instalar-Agente }

        Write-Host ''
        Write-Host 'Concluído.' -ForegroundColor Green
        if ($Script:Nginx) {
            Write-Host @"

Próximos passos:
  1. Publicar o painel no proxy reverso (NGINX), no site que já existe:

     location = /zapmq { return 301 /zapmq/; }
     location ^~ /zapmq/ {
         proxy_pass http://127.0.0.1:$($Script:Nginx)/zapmq/;
         proxy_http_version 1.1;
         proxy_set_header Host `$host;
         proxy_set_header X-Forwarded-For `$proxy_add_x_forwarded_for;
         proxy_set_header Connection "";
         proxy_buffering off;
         proxy_read_timeout 1h;
         client_max_body_size 600m;
     }

  2. Entrar no painel com admin / admin e trocar a senha.
  3. Em Processos e serviços, clicar em Proteger agora.
"@
        }
    }
    finally {
        if (-not $Script:Simulando) {
            Stop-Transcript | Out-Null
            Write-Host "Log: $log"
        }
    }
}

# Carregado por um teste (". .\instalar.ps1"), só define as funções.
if ($MyInvocation.InvocationName -ne '.') { Executar }
