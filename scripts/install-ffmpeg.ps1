<#
.SYNOPSIS
  FFmpeg 공유 DLL을 저장소의 ffmpeg/ 폴더에 배치한다. 빌드하면 앱 출력 폴더의 ffmpeg/로 복사된다.

.DESCRIPTION
  NGvid는 FFmpeg 9.0.x 공유(shared) 빌드에 고정되어 있다. FFmpeg.AutoGen 9.0.x와 짝이 맞아야 한다.
  -Source에는 공유 빌드를 푼 폴더 또는 .zip 파일을 준다. 아래 주 버전의 DLL이 모두 있어야 한다.

.EXAMPLE
  .\scripts\install-ffmpeg.ps1 -Source C:\Downloads\ffmpeg-9.0.2-full_build-shared.zip
#>
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Destination = (Join-Path $PSScriptRoot '..\ffmpeg')
)

$ErrorActionPreference = 'Stop'

# FFmpeg 9.0.x의 라이브러리 주 버전 (소스의 version_major.h 기준)
$required = 'avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'avfilter-12.dll', 'swscale-10.dll', 'swresample-7.dll'

$temp = $null
try {
    if (-not (Test-Path $Source)) { throw "찾을 수 없습니다: $Source" }

    $root = $Source
    if ((Get-Item $Source).Extension -eq '.zip') {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("ngvid-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -Path $Source -DestinationPath $temp
        $root = $temp
    }
    elseif (-not (Get-Item $Source).PSIsContainer) {
        throw "폴더 또는 .zip 파일만 지원합니다. 다른 압축 형식은 먼저 풀어 주세요."
    }

    $anchor = Get-ChildItem $root -Recurse -File -Filter $required[0] | Select-Object -First 1
    if (-not $anchor) {
        $found = (Get-ChildItem $root -Recurse -File -Filter 'avcodec-*.dll' | Select-Object -ExpandProperty Name -Unique) -join ', '
        if ($found) { throw "FFmpeg 9.0.x가 아닙니다. $($required[0])이 필요한데 $found 이(가) 있습니다." }
        throw "$($required[0])을 찾을 수 없습니다. 정적(static) 빌드가 아니라 공유(shared) 빌드가 필요합니다."
    }

    $bin = $anchor.DirectoryName
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $bin $_)) }
    if ($missing) { throw "다음 DLL이 없습니다: $($missing -join ', ')" }

    New-Item -ItemType Directory -Force $Destination | Out-Null
    $copied = Get-ChildItem $bin -File -Filter '*.dll' | Copy-Item -Destination $Destination -Force -PassThru
    "FFmpeg DLL $($copied.Count)개를 배치했습니다: $((Resolve-Path $Destination).Path)"
}
finally {
    if ($temp -and (Test-Path $temp)) { Remove-Item $temp -Recurse -Force }
}
