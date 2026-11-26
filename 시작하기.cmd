@echo off
cd /d "%~dp0"
start "" powershell -NoProfile -WindowStyle Hidden -Command "for($i=0;$i -lt 90;$i++){try{Invoke-WebRequest 'http://127.0.0.1:5187/api/inspections' -UseBasicParsing -ErrorAction Stop | Out-Null; Start-Process 'http://127.0.0.1:5187'; break}catch{Start-Sleep -Seconds 1}}"
dotnet run --project QualityMate.csproj
pause

