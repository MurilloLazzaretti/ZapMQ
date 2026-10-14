#!/usr/bin/env bash
# Monta o zip do instalador com o que está publicado do ZapMQ e do Worker Control.
# Uso: installer/empacotar.sh [pasta do repositório do Worker Control]
set -euo pipefail
cd "$(dirname "$0")/.."
agente="${1:-../Worker-Control}"

zap_versao=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' src/ZapMQ.Server/ZapMQ.Server.csproj | head -1)
agente_versao=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' "$agente/src/WorkerControl.Service/WorkerControl.Service.csproj" | head -1)
for arquivo in publish/win-x64/ZapMQ.exe "$agente/publish/win-x64/WorkerControl.exe" "$agente/publish/win-x64/e_sqlite3.dll" "$agente/publish/win-x64/Microsoft.Data.SqlClient.SNI.dll"; do
  [ -f "$arquivo" ] || { echo "Falta $arquivo: publique antes (./publish.sh e o dotnet publish do Worker Control)." >&2; exit 1; }
done

trabalho=$(mktemp -d)
trap 'rm -rf "$trabalho"' EXIT
mkdir -p "$trabalho/ZapMQ" "$trabalho/WorkerControl"
cp installer/instalar.ps1 installer/LEIA-ME.txt "$trabalho/"
cp publish/win-x64/ZapMQ.exe "$trabalho/ZapMQ/"
cp "$agente/publish/win-x64/WorkerControl.exe" "$agente/publish/win-x64/e_sqlite3.dll" "$agente/publish/win-x64/Microsoft.Data.SqlClient.SNI.dll" "$trabalho/WorkerControl/"

destino="publish/ZapMQ-$zap_versao-WorkerControl-$agente_versao.zip"
rm -f "$destino"
(cd "$trabalho" && zip -qr -X - .) > "$destino"
echo "$destino"
