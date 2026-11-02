# Üç temada (cam, grafit, beyaz) çubuğun görüntüsünü alır: sabitlenmemiş test penceresi etkinken, üzerindeyken ve kapandıktan sonra.
# Ayarları yedekleyip geri koyar, sonunda Release'i yeniden başlatır. Görüntüler %TEMP%\IslandBarTest içinde; arkadaki pencereler girebilir, işin bitince sil.
. (Join-Path $PSScriptRoot 'lib.ps1')
if (-not ('FG' -as [type])) { Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FG { [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h); }' }
$form = Join-Path $PSScriptRoot 'test_form.ps1'
function Forms { @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'test_form' }) }
function Region($b, $name) { [D]::Shot("$s\tema_$name.png", $b.X - 230, $b.T - 10, 460, $b.H + 20); "$s\tema_$name.png" }
try {
  "fare basili (once)=$([D]::Held())"
  StopBar
  Copy-Item $cfg $bak -Force
  $files = @()
  foreach ($theme in 'Glass', 'Graphite', 'White') {
    WriteCfg $theme $false
    $script:id = StartBar $debugExe
    $p = Start-Process powershell.exe "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$form`"" -PassThru
    Pump 4000
    $h = (Get-Process -Id $p.Id).MainWindowHandle
    [void][FG]::SetForegroundWindow($h); [D]::Move(960, 400); Pump 1200
    $b = (TaskButtons $script:id) | Where-Object { $_.Name -match 'TestSabitsiz' } | Select-Object -First 1
    if (-not $b) { "$theme : dugme yok"; continue }
    $files += Region $b "${theme}_etkin"
    # etkin tusun uzerine gelince (yay buyumesi tusla birlikte)
    [D]::Move($b.X, $b.Y); Pump 300
    $files += Region $b "${theme}_uzerinde"
    [D]::Move(960, 400); Pump 600
    Stop-Process -Id $p.Id -Force; Pump 900
    # test penceresi kapandi: baska uygulama etkin olur ya da hicbiri
    $files += Region $b "${theme}_sonra"
    "$theme canli=$(@(Mains).Count) crash.log=$(Test-Path $log)"
    StopBar
  }
  [D]::Stack($files, "$s\tema_all.png", 3)
}
catch { "HATA: $_ @ $($_.InvocationInfo.ScriptLineNumber)" }
finally {
  [D]::Up(); [D]::Move(960, 500)
  "fare basili (sonra)=$([D]::Held())"
  Forms | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  StopBar
  Copy-Item $bak $cfg -Force
  "ayar geri=$((Get-FileHash $cfg).Hash -eq (Get-FileHash $bak).Hash)"
  Start-Process $releaseExe; Start-Sleep -Seconds 6
  "son: surec=$(@(Get-Process IslandBar).Count) cubuk=$(@(Mains).Count)"
}
