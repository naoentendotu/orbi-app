# Gera o Orbi.exe e instala em %LOCALAPPDATA%\Programs\Orbi, com atalho no Menu Iniciar.
# Rode de novo sempre que quiser atualizar o app instalado depois de mudar o código.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Orbi'

# Fecha o Orbi aberto para poder substituir o executável.
Get-Process Orbi -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Um único Orbi.exe que usa o .NET Desktop Runtime já instalado (por isso fica pequeno).
dotnet publish "$PSScriptRoot\src\Orbi\Orbi.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=None -o $dest
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o Orbi.exe' }

$link = Join-Path ([Environment]::GetFolderPath('Programs')) 'Orbi.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
$shortcut.TargetPath = Join-Path $dest 'Orbi.exe'
$shortcut.WorkingDirectory = $dest
$shortcut.Description = 'Orbi: ferramentas rápidas em volta do cursor'
$shortcut.Save()

Start-Process (Join-Path $dest 'Orbi.exe')
Write-Host "Orbi instalado em $dest e aberto. Atalho criado no Menu Iniciar."
