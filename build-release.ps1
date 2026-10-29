# build-release.ps1 — 一键构建发布包
#
# 用法：
#   .\build-release.ps1              # 构建 exe + 安装包
#   .\build-release.ps1 -SkipInstaller  # 只构建可执行文件
#
# 产物：
#   dist\DshHaloRing-Setup-<version>.exe   Inno Setup 安装包（自包含，用户零依赖）
#   publish-dir\                            自包含目录版（可直接运行/绿色部署）

param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    $version = (Select-String -Path 'DshHaloRing.csproj' -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
    Write-Host "构建 DSH HaloRing v$version" -ForegroundColor Cyan
    Write-Host ''

    # ── 1. 生成应用图标（若缺失）─────────────────────────
    if (-not (Test-Path 'assets\app.ico')) {
        Write-Host '[1/4] 生成应用图标…' -ForegroundColor Yellow
        New-Item -ItemType Directory -Force 'assets' | Out-Null
        New-Item -ItemType Directory -Force 'assets\icon-src' | Out-Null
        dotnet build -c Debug -v q | Out-Null
        & '.\bin\Debug\net8.0-windows\DshHaloRing.exe' --sample 'assets\icon-src' | Out-Null
        Copy-Item 'assets\icon-src\dark\completed_100_256.png' 'assets\icon-256.png' -Force -ErrorAction SilentlyContinue
        Write-Host '      提示：图标需手动转成 app.ico，或用 Inno Setup 的默认图标' -ForegroundColor DarkGray
    } else {
        Write-Host '[1/4] 图标已存在，跳过' -ForegroundColor Green
    }

    # ── 2. 自包含发布（目录形式）─────────────────────────
    # 注意：不用 PublishSingleFile —— 单文件版需要解压到 %TEMP%\.net，
    # 在受限环境下会因无法创建解压目录而启动失败。
    Write-Host '[2/4] 发布自包含目录版…' -ForegroundColor Yellow
    Remove-Item 'publish-dir' -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugType=none `
        -o publish-dir | Out-Null
    if (-not (Test-Path 'publish-dir\DshHaloRing.exe')) {
        throw '发布失败：publish-dir\DshHaloRing.exe 不存在'
    }
    $sizeMB = [math]::Round((Get-ChildItem 'publish-dir' -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "      完成：$sizeMB MB" -ForegroundColor Green

    # ── 3. 编译安装包 ────────────────────────────────────
    if ($SkipInstaller) {
        Write-Host '[3/4] 跳过安装包（-SkipInstaller）' -ForegroundColor DarkGray
    } else {
        Write-Host '[3/4] 编译安装包…' -ForegroundColor Yellow
        $iscc = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if (-not $iscc) {
            Write-Warning '未找到 Inno Setup。安装：winget install JRSoftware.InnoSetup'
            Write-Warning '跳过安装包编译。'
        } else {
            New-Item -ItemType Directory -Force 'dist' | Out-Null
            & $iscc 'installer.iss' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }
            Write-Host '      完成' -ForegroundColor Green
        }
    }

    # ── 4. 汇总 ──────────────────────────────────────────
    Write-Host ''
    Write-Host '[4/4] 产物清单' -ForegroundColor Yellow
    if (Test-Path 'dist') {
        Get-ChildItem 'dist' -File | ForEach-Object {
            Write-Host ("      {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
        }
    }
    Write-Host ("      publish-dir\  ({0} 个文件，可直接运行)" -f (Get-ChildItem 'publish-dir' -File).Count)
    Write-Host ''
    Write-Host '构建完成。' -ForegroundColor Cyan
}
finally {
    Pop-Location
}
