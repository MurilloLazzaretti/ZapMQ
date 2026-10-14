<#
.SYNOPSIS
    Testa as decisões do instalar.ps1 sem Windows: o que consulta ou muda a máquina (serviços,
    sc.exe, versões, respostas) é trocado por uma máquina de mentira, e os arquivos vão para uma
    pasta temporária. Roda em qualquer PowerShell 7: pwsh ./installer/testar.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'instalar.ps1')

$Script:Falhas = 0
function Confere([bool]$Condicao, [string]$Texto) {
    if ($Condicao) { Write-Host "  ok    $Texto" -ForegroundColor Green }
    else { Write-Host "  FALHA $Texto" -ForegroundColor Red; $Script:Falhas++ }
}

# ---------------------------------------------------------------- a máquina de mentira

function Nova-Maquina {
    $raiz = Join-Path ([IO.Path]::GetTempPath()) ('instalar-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $raiz | Out-Null
    # O que vem no zip.
    $zip = Join-Path $raiz 'zip'
    New-Item -ItemType Directory -Path (Join-Path $zip 'ZapMQ'), (Join-Path $zip 'WorkerControl') | Out-Null
    Set-Content (Join-Path $zip 'ZapMQ\ZapMQ.exe') 'zapmq 2.11.1'
    foreach ($arquivo in $AgenteArquivos) { Set-Content (Join-Path $zip "WorkerControl\$arquivo") "agente 2.25.0 $arquivo" }
    $Script:Origem = $zip
    $Script:Maquina = @{ Raiz = $raiz; Servicos = @{}; Sc = New-Object System.Collections.Generic.List[string]; Responde = $true; Inicia = $true }
    $Script:Simulando = $false
    $Script:Nginx = $null
    return $raiz
}

function Get-Servico([string]$Nome) { $Script:Maquina.Servicos[$Nome] }

# A versão de um executável de mentira é o que está escrito nele.
function Get-Versao([string]$Arquivo) {
    if (-not (Test-Path -LiteralPath $Arquivo)) { return $null }
    $texto = Get-Content -LiteralPath $Arquivo -Raw
    if ($texto -match '(\d+\.\d+\.\d+)') { return $Matches[1] }
    return $null
}

function Invoke-Sc([string[]]$Argumentos) {
    $Script:Maquina.Sc.Add($Argumentos -join ' ')
    $nome = $Argumentos[1]
    switch ($Argumentos[0]) {
        'create' { $Script:Maquina.Servicos[$nome] = [pscustomobject]@{ Nome = $nome; Exe = $Argumentos[3].Trim('"'); Estado = 'Stopped'; Inicio = 'Auto' } }
        'config' { $Script:Maquina.Servicos[$nome].Exe = $Argumentos[3].Trim('"') }
    }
}

function Stop-Servico([string]$Nome) { $Script:Maquina.Servicos[$Nome].Estado = 'Stopped' }
function Start-Servico([string]$Nome) { $Script:Maquina.Servicos[$Nome].Estado = 'Running' }
function Set-Inicio([string]$Nome, [string]$Modo) { $Script:Maquina.Servicos[$Nome].Inicio = $Modo }
function Test-ZapMQ([int]$Porta) { $Script:Maquina.PortaTestada = $Porta; $Script:Maquina.Responde }
function Test-Agente([string]$PastaAgente, [datetime]$Desde) { $Script:Maquina.Inicia }

function Servico([string]$Nome, [string]$Exe, [string]$Estado = 'Running') {
    $Script:Maquina.Servicos[$Nome] = [pscustomobject]@{ Nome = $Nome; Exe = $Exe; Estado = $Estado; Inicio = 'Auto' }
}

# ---------------------------------------------------------------- casos

Write-Host 'Instalação nova'
$raiz = Nova-Maquina
$Pasta = Join-Path $raiz 'Apps'; $Ambiente = 'QAS'; $PortaFila = 0; $PortaPainel = 5680
Instalar-ZapMQ
Instalar-Agente
Confere ((Get-Servico 'ZapMQServer').Exe -eq (Join-Path $Pasta 'ZapMQ\ZapMQ.exe')) 'o serviço do ZapMQ aponta para a pasta nova'
Confere ((Get-Servico 'ZapMQServer').Estado -eq 'Running') 'o ZapMQ foi iniciado'
$config = Get-Content (Join-Path $Pasta 'ZapMQ\appsettings.json') -Raw | ConvertFrom-Json
Confere ($config.ZapMQ.Port -eq 5679 -and $config.ZapMQ.Panel.Port -eq 5680 -and $config.ZapMQ.Panel.BasePath -eq '/zapmq' -and $config.ZapMQ.Transport.Environment -eq 'QAS') 'appsettings.json com porta, painel e ambiente'
Confere ((Get-Servico 'WorkerControlService').Exe -eq (Join-Path $Pasta 'WorkerControl\WorkerControl.exe')) 'o serviço do agente aponta para a pasta nova'
Confere (($AgenteArquivos | Where-Object { -not (Test-Path (Join-Path $Pasta "WorkerControl\$_")) }).Count -eq 0) 'o agente tem as DLLs ao lado'
Confere ((Get-Content (Join-Path $Pasta 'WorkerControl\ConfigWorkers.json') -Raw | ConvertFrom-Json).ZapMQPort -eq 5679) 'ConfigWorkers.json inicial'
Confere ($Script:Maquina.Sc | Where-Object { $_ -like 'failure *' }).Count -eq 2 'os dois serviços reiniciam sozinhos depois de uma falha'
Confere ($Script:Nginx -eq 5680) 'mostra o trecho do NGINX'

Write-Host 'Instalação nova sem ambiente'
$raiz = Nova-Maquina
$Pasta = Join-Path $raiz 'Apps'; $Ambiente = ''
$recusou = $false; try { Instalar-ZapMQ } catch { $recusou = $_.Exception.Message -match 'Ambiente' }
Confere $recusou 'pede o nome do ambiente'
Confere ($Script:Maquina.Sc.Count -eq 0 -and -not (Test-Path (Join-Path $Pasta 'ZapMQ'))) 'e não muda nada'

Write-Host 'Troca do 1.x'
$raiz = Nova-Maquina
$Pasta = Join-Path $raiz 'OjiTec'; $Ambiente = 'QAS'
$velho = Join-Path $raiz 'Program Files (x86)'
New-Item -ItemType Directory -Path (Join-Path $velho 'ZapMQ'), (Join-Path $velho 'WorkerControl') | Out-Null
Set-Content (Join-Path $velho 'ZapMQ\ZapMQ.exe') 'zapmq 1.4.0'
Set-Content (Join-Path $velho 'ZapMQ\ZapMQ.ini') "[Config]`r`nPort=5700"
Set-Content (Join-Path $velho 'WorkerControl\WorkerControl.exe') 'agente 1.1.1'
Set-Content (Join-Path $velho 'WorkerControl\ConfigWorkers.json') '{ "ZapMQHost": "localhost", "ZapMQPort": 5700, "WorkerGroups": [ { "Name": "Pedidos" } ] }'
Servico 'ZapMQservice' (Join-Path $velho 'ZapMQ\ZapMQ.exe')
Servico 'WorkerControlService' (Join-Path $velho 'WorkerControl\WorkerControl.exe')
Instalar-ZapMQ
Instalar-Agente
Confere ((Get-Servico 'ZapMQservice').Estado -eq 'Stopped' -and (Get-Servico 'ZapMQservice').Inicio -eq 'Manual') 'o ZapMQ 1.x fica parado, em início manual'
Confere (Test-Path (Join-Path $velho 'ZapMQ\ZapMQ.exe')) 'e os arquivos dele continuam lá, para voltar'
Confere ((Get-Content (Join-Path $Pasta 'ZapMQ\appsettings.json') -Raw | ConvertFrom-Json).ZapMQ.Port -eq 5700) 'a porta da fila é a do ZapMQ.ini'
Confere ($Script:Maquina.PortaTestada -eq 5700) 'e é nela que a resposta é conferida'
Confere ((Get-Servico 'WorkerControlService').Exe -eq (Join-Path $Pasta 'WorkerControl\WorkerControl.exe')) 'o mesmo serviço do agente passa a rodar o 2'
Confere (-not ($Script:Maquina.Sc | Where-Object { $_ -like 'create WorkerControlService*' -or $_ -like 'delete*' })) 'sem criar nem apagar o serviço do agente'
Confere ((Get-Content (Join-Path $Pasta 'WorkerControl\ConfigWorkers.json') -Raw) -match 'Pedidos') 'com o ConfigWorkers.json do 1.x'

Write-Host 'Troca do 1.x que não sobe'
$raiz = Nova-Maquina
$Pasta = Join-Path $raiz 'OjiTec'; $Ambiente = 'QAS'
$velho = Join-Path $raiz 'old'
New-Item -ItemType Directory -Path $velho | Out-Null
Set-Content (Join-Path $velho 'WorkerControl.exe') 'agente 1.1.1'
Set-Content (Join-Path $velho 'ConfigWorkers.json') '{}'
Servico 'WorkerControlService' (Join-Path $velho 'WorkerControl.exe')
$Script:Maquina.Inicia = $false
$voltou = $false; try { Instalar-Agente } catch { $voltou = $_.Exception.Message -match 'voltou' }
Confere $voltou 'diz que voltou ao 1.x'
Confere ((Get-Servico 'WorkerControlService').Exe -eq (Join-Path $velho 'WorkerControl.exe') -and (Get-Servico 'WorkerControlService').Estado -eq 'Running') 'e o 1.x está no ar de novo'

Write-Host 'Atualização'
$raiz = Nova-Maquina
$Pasta = Join-Path $raiz 'ignorada'
$zap = Join-Path $raiz 'OjiTec\ZapMQ'; $agente = Join-Path $raiz 'OjiTec\WorkerControl'
New-Item -ItemType Directory -Path $zap, $agente | Out-Null
Set-Content (Join-Path $zap 'ZapMQ.exe') 'zapmq 2.10.6'
Set-Content (Join-Path $zap 'appsettings.json') '{ "ZapMQ": { "Port": 5679, "Transport": { "Environment": "DEV" } } }'
Set-Content (Join-Path $agente 'WorkerControl.exe') 'agente 2.24.1'
Servico 'ZapMQServer' (Join-Path $zap 'ZapMQ.exe')
Servico 'WorkerControlService' (Join-Path $agente 'WorkerControl.exe')
Instalar-ZapMQ
Instalar-Agente
Confere ((Get-Versao (Join-Path $zap 'ZapMQ.exe')) -eq '2.11.1') 'o ZapMQ fica na 2.11.1'
Confere (Test-Path (Join-Path $zap 'ZapMQ.2.10.6.exe')) 'a 2.10.6 fica guardada ao lado'
Confere ((Get-Content (Join-Path $zap 'appsettings.json') -Raw) -match 'DEV') 'a configuração não é tocada'
Confere ((Get-Versao (Join-Path $agente 'WorkerControl.exe')) -eq '2.25.0' -and (Test-Path (Join-Path $agente 'WorkerControl.2.24.1.exe'))) 'o agente fica na 2.25.0, com a 2.24.1 guardada'
Confere ($Script:Maquina.Sc.Count -eq 0) 'sem mexer no registro dos serviços'
Confere (-not (Test-Path (Join-Path $raiz 'ignorada'))) 'na pasta em que cada um já está'

Write-Host 'Já atualizado'
$Script:Maquina.Sc.Clear()
Instalar-ZapMQ
Confere (-not (Test-Path (Join-Path $zap 'ZapMQ.2.11.1.exe'))) 'nada é trocado'

Write-Host 'Atualização que não responde'
$raiz = Nova-Maquina
$zap = Join-Path $raiz 'ZapMQ'
New-Item -ItemType Directory -Path $zap | Out-Null
Set-Content (Join-Path $zap 'ZapMQ.exe') 'zapmq 2.10.6'
Servico 'ZapMQServer' (Join-Path $zap 'ZapMQ.exe')
$Script:Maquina.Responde = $false
$voltou = $false; try { Instalar-ZapMQ } catch { $voltou = $_.Exception.Message -match '2.10.6 voltou' }
Confere $voltou 'diz que a versão anterior voltou'
Confere ((Get-Versao (Join-Path $zap 'ZapMQ.exe')) -eq '2.10.6' -and -not (Test-Path (Join-Path $zap 'ZapMQ.2.10.6.exe'))) 'e o executável anterior está de volta no lugar'
Confere ((Get-Servico 'ZapMQServer').Estado -eq 'Running') 'no ar'

Write-Host 'Simulação'
$raiz = Nova-Maquina
$Pasta = Join-Path $raiz 'Apps'; $Ambiente = 'QAS'
$Script:Simulando = $true
Instalar-ZapMQ
Instalar-Agente
Confere ($Script:Maquina.Sc.Count -eq 0 -and -not (Test-Path $Pasta)) 'não muda nada'

Write-Host ''
if ($Script:Falhas -gt 0) { Write-Host "$($Script:Falhas) falha(s)" -ForegroundColor Red; exit 1 }
Write-Host 'Tudo certo' -ForegroundColor Green
