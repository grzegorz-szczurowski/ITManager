param(
  [string]$Root = ".",
  [string]$Name = "",               # domyœlnie brak suffixu -> bez "_ai_"
  [string]$OutDir = "U:\99_Archives\OldRepos\",
  [switch]$KeepStaging
)

$ErrorActionPreference = "Stop"

function Log($msg, [ConsoleColor]$color = [ConsoleColor]::Cyan) {
  $ts = Get-Date -Format "HH:mm:ss"
  Write-Host "[$ts] $msg" -ForegroundColor $color
}

function Quote-Arg([string]$a) {
  if ($null -eq $a) { return '""' }
  if ($a -match '[\s"]') {
    return '"' + ($a -replace '"','\"') + '"'
  }
  return $a
}

function Try-RunCommand([string]$FilePath, [string[]]$Args) {
  try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath

    $quotedArgs = @()
    foreach ($a in $Args) { $quotedArgs += (Quote-Arg $a) }
    $psi.Arguments = ($quotedArgs -join " ")

    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    [void]$p.Start()

    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    return [pscustomobject]@{
      ExitCode = $p.ExitCode
      StdOut   = $stdout
      StdErr   = $stderr
      Ok       = ($p.ExitCode -eq 0)
    }
  } catch {
    return [pscustomobject]@{
      ExitCode = -1
      StdOut   = ""
      StdErr   = $_.Exception.Message
      Ok       = $false
    }
  }
}

function Get-DotNetPath {
  try {
    $cmd = Get-Command dotnet -ErrorAction Stop
    return $cmd.Source
  } catch {
    return $null
  }
}

function Get-CsprojMeta([string]$csprojPath) {
  $tfm = ""
  $tfms = ""
  try {
    [xml]$xml = Get-Content -Path $csprojPath -Raw
    $nodes = $xml.SelectNodes("//*[local-name()='TargetFramework']")
    if ($nodes -and $nodes.Count -gt 0) {
      $tfm = ($nodes | Select-Object -First 1).InnerText.Trim()
    }
    $nodes2 = $xml.SelectNodes("//*[local-name()='TargetFrameworks']")
    if ($nodes2 -and $nodes2.Count -gt 0) {
      $tfms = ($nodes2 | Select-Object -First 1).InnerText.Trim()
    }
  } catch {
    # ignoruj
  }

  $framework = if (-not [string]::IsNullOrWhiteSpace($tfms)) { $tfms } else { $tfm }
  if ([string]::IsNullOrWhiteSpace($framework)) { $framework = "(unknown)" }

  return [pscustomobject]@{
    TargetFramework = $framework
  }
}

function Build-Tree([string]$basePath, [string[]]$excludeDirs, [int]$maxDepth = 6) {
  $excludeSet = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
  foreach ($d in $excludeDirs) { [void]$excludeSet.Add($d) }

  $lines = New-Object System.Collections.Generic.List[string]

  function Recurse([string]$path, [int]$depth) {
    if ($depth -gt $maxDepth) { return }

    $items = Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue |
      Sort-Object -Property @{ Expression = { -not $_.PSIsContainer } }, Name

    foreach ($it in $items) {
      if ($it.PSIsContainer) {
        if ($excludeSet.Contains($it.Name)) { continue }

        $rel = $it.FullName.Substring($basePath.Length).TrimStart('\','/')
        $indent = ("  " * $depth)
        $lines.Add("$indent- $rel/")

        Recurse -path $it.FullName -depth ($depth + 1)
      } else {
        $rel = $it.FullName.Substring($basePath.Length).TrimStart('\','/')
        $indent = ("  " * $depth)
        $lines.Add("$indent- $rel")
      }
    }
  }

  Recurse -path $basePath -depth 0
  return ($lines -join "`r`n")
}

# Œcie¿ki i meta
$RootPath    = (Resolve-Path $Root).Path
$OutPath     = (Resolve-Path $OutDir).Path
$ProjectName = Split-Path -Path $RootPath -Leaf
$SafeProject = ($ProjectName -replace '[\\/:*?"<>| ]+', '_')
$stamp       = Get-Date -Format "yyyyMMdd-HHmm"

# <Projekt>[_<Suffix>]
$zipBaseName = if ([string]::IsNullOrWhiteSpace($Name)) { $SafeProject } else { "$SafeProject" + "_" + "$Name" }

$staging     = Join-Path $env:TEMP "$zipBaseName`_$stamp"
$dest        = Join-Path $OutPath "$zipBaseName`_$stamp.zip"

# Nag³ówki informacyjne
Log "Start pakowania."
Log "Projekt: $ProjectName"
Log "Katalog projektu: $RootPath"
Log "Docelowa lokalizacja ZIP: $dest"
Log "Katalog tymczasowy: $staging"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Co pakujemy
$allowedExt  = @(
  ".sln",".csproj",".razor",".cs",".cshtml",".css",".scss",".js",".ts",".json",".md",".sql",
  ".resx"
)
$excludeDirs = @("bin","obj",".git",".vs","node_modules","wwwroot/_content",".idea",".vscode")

# Regex wykluczeñ
$escaped = ($excludeDirs | ForEach-Object { [regex]::Escape($_) })
$ex = "(\\|/)(?:$($escaped -join '|'))(\\|/|$)"

# Skan plików
Write-Progress -Activity "Skanowanie" -Status "Wyszukiwanie plików..." -PercentComplete 5
$files = Get-ChildItem -Path $RootPath -Recurse -File |
  Where-Object { $allowedExt -contains $_.Extension } |
  Where-Object { $_.FullName -notmatch $ex }

$total = $files.Count
if ($total -eq 0) {
  Log "Brak plików do spakowania (sprawdŸ rozszerzenia/wykluczenia)." ([ConsoleColor]::Yellow)
  exit 1
}
Log "Znaleziono $total plików do spakowania."

# Kopiowanie + sanityzacja
$i = 0
foreach ($f in $files) {
  $i++
  $rel = $f.FullName.Substring($RootPath.Length).TrimStart('\','/')
  $pct = [int](($i / $total) * 100)
  Write-Progress -Activity "Kopiowanie i sanityzacja" -Status "${i}/${total}: $rel" -PercentComplete $pct

  $target    = Join-Path $staging $rel
  $targetDir = Split-Path $target
  if (!(Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

  if ($f.Name -like "appsettings*.json") {
    $json = Get-Content $f.FullName -Raw
    $json = $json -replace '(?i)("ConnectionStrings"\s*:\s*)\{[^}]*\}', '$1{"DefaultConnection":"Server=SERVER;Database=DB;Trusted_Connection=True;"}'
    $json = $json -replace '(?i)("ApiKey"\s*:\s*)"(.*?)"', '$1"***REDACTED***"'
    $json = $json -replace '(?i)("ClientSecret"\s*:\s*)"(.*?)"', '$1"***REDACTED***"'
    $json = $json -replace '(?i)("Password"\s*:\s*)"(.*?)"', '$1"***REDACTED***"'
    $json | Set-Content -Path $target -Encoding UTF8
  } else {
    Copy-Item -Path $f.FullName -Destination $target
  }
}

# README
Write-Progress -Activity "Przygotowanie metadanych" -Status "Tworzenie README_AI.md" -PercentComplete 92
@"
# AI package - Blazor

Zawiera najwa¿niejsze pliki: .sln/.csproj, .cs/.razor, style, migracje, zasoby .resx.
Wykluczenia: bin/obj, .git, node_modules, wwwroot/_content, IDE.
Sekrety w appsettings* zast¹pione placeholderami.
Projekt: $ProjectName
"@ | Set-Content -Path (Join-Path $staging "README_AI.md") -Encoding UTF8

# AI_MANIFEST.md
Write-Progress -Activity "Przygotowanie metadanych" -Status "Tworzenie AI_MANIFEST.md" -PercentComplete 95

$tree = Build-Tree -basePath $RootPath -excludeDirs $excludeDirs -maxDepth 6

$csprojs = Get-ChildItem -Path $RootPath -Recurse -File -Filter *.csproj |
  Where-Object { $_.FullName -notmatch $ex } |
  Sort-Object FullName

$csprojLines = New-Object System.Collections.Generic.List[string]
foreach ($p in $csprojs) {
  $meta = Get-CsprojMeta -csprojPath $p.FullName
  $relp = $p.FullName.Substring($RootPath.Length).TrimStart('\','/')
  $csprojLines.Add("- $relp  |  TargetFramework: $($meta.TargetFramework)")
}
if ($csprojLines.Count -eq 0) { $csprojLines.Add("- (brak .csproj znalezionych)") }

$dotnetPath = Get-DotNetPath
$dotnetInfo = ""
if ($null -ne $dotnetPath) {
  $r = Try-RunCommand -FilePath $dotnetPath -Args @("--info")
  if ($r.Ok) { $dotnetInfo = $r.StdOut } else { $dotnetInfo = "ERROR: $($r.StdErr)" }
} else {
  $dotnetInfo = "ERROR: dotnet nie znaleziony w PATH"
}

$pkgText = New-Object System.Collections.Generic.List[string]
if ($null -ne $dotnetPath -and $csprojs.Count -gt 0) {
  foreach ($p in $csprojs) {
    $relp = $p.FullName.Substring($RootPath.Length).TrimStart('\','/')
    $pkgText.Add("### $relp")
    $r = Try-RunCommand -FilePath $dotnetPath -Args @("list",$p.FullName,"package")
    $pkgText.Add('```')
    if ($r.Ok -and -not [string]::IsNullOrWhiteSpace($r.StdOut)) {
      $pkgText.Add($r.StdOut.TrimEnd())
    } else {
      $msg = $r.StdErr
      if ([string]::IsNullOrWhiteSpace($msg)) { $msg = "Nie uda³o siê pobraæ listy paczek (brak outputu)." }
      $pkgText.Add("ERROR: $msg")
    }
    $pkgText.Add('```')
    $pkgText.Add("")
  }
} else {
  $pkgText.Add("dotnet list package pominiête. Brak dotnet albo brak .csproj.")
}

$manifestLines = New-Object System.Collections.Generic.List[string]
$manifestLines.Add("# AI_MANIFEST")
$manifestLines.Add("")
$manifestLines.Add("Projekt: $ProjectName")
$manifestLines.Add("Root: $RootPath")
$manifestLines.Add("Utworzono: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")")
$manifestLines.Add("")
$manifestLines.Add("## 1) Struktura katalogów (bez wykluczeñ, depth <= 6)")
$manifestLines.Add("")
$manifestLines.Add($tree)
$manifestLines.Add("")
$manifestLines.Add("## 2) Projekty i TargetFramework")
$manifestLines.Add("")
$manifestLines.Add(($csprojLines -join "`r`n"))
$manifestLines.Add("")
$manifestLines.Add("## 3) dotnet --info")
$manifestLines.Add("")
$manifestLines.Add('```')
$manifestLines.Add(($dotnetInfo.TrimEnd()))
$manifestLines.Add('```')
$manifestLines.Add("")
$manifestLines.Add("## 4) NuGet dependencies (dotnet list package)")
$manifestLines.Add("")
$manifestLines.Add(($pkgText -join "`r`n"))

($manifestLines -join "`r`n") | Set-Content -Path (Join-Path $staging "AI_MANIFEST.md") -Encoding UTF8

# Archiwizacja
Write-Progress -Activity "Archiwizacja" -Status "Tworzenie archiwum ZIP" -PercentComplete 97
if (Test-Path $dest) { Remove-Item $dest -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $dest -Force

Write-Progress -Activity "Zakoñczono" -Completed
$sizeMB = (Get-Item $dest).Length / 1MB

Log "Zakoñczono pakowanie projektu: $ProjectName" ([ConsoleColor]::Green)
Log ("Plik ZIP zapisany: {0}  ({1:N1} MB)" -f $dest, $sizeMB) ([ConsoleColor]::Green)

if (-not $KeepStaging) {
  Remove-Item $staging -Recurse -Force
  Log "Wyczyszczono katalog tymczasowy."
} else {
  Log "Pozostawiono staging: $staging" ([ConsoleColor]::Yellow)
}
