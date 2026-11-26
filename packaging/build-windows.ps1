# Gera o LenoShot para Windows (x64) em dist/: pasta, .zip e, com -Installer, o instalador .exe.
# Uso: powershell -ExecutionPolicy Bypass -File packaging/build-windows.ps1 [-NoAot] [-Installer]
#
# Por padrão compila com Native AOT, que requer as ferramentas de C++ do Visual Studio (carga de
# trabalho "Desenvolvimento para desktop com C++") para ligar o executável. Sem elas, use -NoAot:
# gera um executável único autocontido (maior e inicia um pouco mais devagar, mas não precisa de C++).
#
# -Installer gera também o instalador com o Inno Setup 6 (winget install JRSoftware.InnoSetup).
#
# O ffmpeg (usado na gravação de tela) vai junto do LenoShot.exe: o script baixa o build
# "essentials" do Gyan (uma vez só, guardado em dist/cache) e confere o SHA256. -NoFfmpeg deixa de
# fora; aí o usuário precisa instalar o ffmpeg por conta própria.
param([switch]$NoAot, [switch]$Installer, [switch]$NoFfmpeg)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # a barra de progresso deixa o download muito lento no PowerShell 5

# Versão do ffmpeg embutido. Ao trocar, atualize o hash (campo "digest" do arquivo na página da versão:
# https://github.com/GyanD/codexffmpeg/releases)
$ffmpegVersion = '9.0.2'
$ffmpegSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'LenoShot/LenoShot.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "LenoShot_${version}_win-x64"
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'publish-win'
$out = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"
$setup = Join-Path $dist "$name-setup.exe"

# Procura o compilador do Inno Setup antes de compilar, para falhar logo se ele não existir
$iscc = $null
if ($Installer) {
    $iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup' }
}

Write-Host "==> LenoShot $version"
Remove-Item -Recurse -Force $publish, $out, $zip, $setup -ErrorAction SilentlyContinue

if ($NoAot) {
    Write-Host '==> Compilando (autocontido, sem Native AOT)'
    dotnet publish $project -c Release -r win-x64 -o $publish --nologo -v quiet --self-contained `
        -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
} else {
    Write-Host '==> Compilando (Native AOT)'
    dotnet publish $project -c Release -r win-x64 -o $publish --nologo -v quiet
}
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

# Executável e bibliotecas nativas (sem os símbolos de depuração .pdb)
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $publish 'LenoShot.exe') $out
Get-ChildItem $publish -Filter '*.dll' | Copy-Item -Destination $out
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')

if (-not $NoFfmpeg) {
    $ffmpegName = "ffmpeg-$ffmpegVersion-essentials_build"
    $cache = Join-Path $dist 'cache'
    $ffmpegZip = Join-Path $cache "$ffmpegName.zip"
    New-Item -ItemType Directory -Force $cache | Out-Null

    if (-not (Test-Path $ffmpegZip) -or (Get-FileHash $ffmpegZip -Algorithm SHA256).Hash -ne $ffmpegSha256) {
        Write-Host "==> Baixando o ffmpeg $ffmpegVersion"
        $url = "https://github.com/GyanD/codexffmpeg/releases/download/$ffmpegVersion/$ffmpegName.zip"
        Invoke-WebRequest -Uri $url -OutFile "$ffmpegZip.part" -UseBasicParsing
        if ((Get-FileHash "$ffmpegZip.part" -Algorithm SHA256).Hash -ne $ffmpegSha256) {
            Remove-Item "$ffmpegZip.part"
            throw "O ffmpeg baixado não confere com o SHA256 esperado ($url)"
        }
        Move-Item -Force "$ffmpegZip.part" $ffmpegZip
    }

    # Só o executável e a licença (o ffmpeg é GPL: a licença tem de ir junto)
    Write-Host '==> Incluindo o ffmpeg'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ffmpegZip)
    try {
        $wanted = @{ "$ffmpegName/bin/ffmpeg.exe" = 'ffmpeg.exe'; "$ffmpegName/LICENSE" = 'ffmpeg-LICENSE.txt' }
        foreach ($entry in $archive.Entries) {
            if ($wanted.ContainsKey($entry.FullName)) {
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $out $wanted[$entry.FullName]), $true)
            }
        }
    } finally {
        $archive.Dispose()
    }
    if (-not (Test-Path (Join-Path $out 'ffmpeg.exe'))) { throw "ffmpeg.exe não encontrado em $ffmpegZip" }
}

Write-Host '==> Empacotando (.zip)'
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host ('    {0} ({1:N1} MB)' -f $zip, ((Get-Item $zip).Length / 1MB))

if ($Installer) {
    Write-Host '==> Gerando o instalador (Inno Setup)'
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$dist" (Join-Path $PSScriptRoot 'LenoShot.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falhou' }
    Write-Host ('    {0} ({1:N1} MB)' -f $setup, ((Get-Item $setup).Length / 1MB))
}

Write-Host '==> Pronto'
