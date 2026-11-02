# IslandBar test kitaplığı: betiğin başında . (Join-Path $PSScriptRoot 'lib.ps1') ile yükle. Ayrıntılar docs/notlar.md (Test notu).
$s = Join-Path $env:TEMP 'IslandBarTest'; New-Item -ItemType Directory -Force $s | Out-Null
$cfg = "$env:APPDATA\IslandBar\settings.json"; $bak = "$s\settings_backup.json"; $log = "$env:LOCALAPPDATA\IslandBar\crash.log"
$debugExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\IslandBar\bin\Debug\net10.0-windows\IslandBar.exe'))
$releaseExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\IslandBar\bin\Release\net10.0-windows\IslandBar.exe'))
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
if (-not ('D' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Threading; using System.Drawing; using System.Runtime.InteropServices;
public static class D {
  [DllImport("user32")] static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  [DllImport("user32")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32")] static extern short GetAsyncKeyState(int k);
  [DllImport("user32")] static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Down() { mouse_event(2,0,0,0,IntPtr.Zero); }
  public static void Up() { mouse_event(4,0,0,0,IntPtr.Zero); }
  public static bool Held() { return (GetAsyncKeyState(1) & 0x8000) != 0; }
  public static void Esc() { keybd_event(0x1B,0,0,IntPtr.Zero); keybd_event(0x1B,0,2,IntPtr.Zero); }
  public static void Shot(string path, int x, int y, int w, int h) {
    using (var b = new Bitmap(w, h)) using (var g = Graphics.FromImage(b)) { g.CopyFromScreen(x, y, 0, 0, new Size(w, h)); b.Save(path); }
  }
  public static void Stack(string[] files, string target, int scale) {
    Bitmap first = new Bitmap(files[0]); int w = first.Width, h = first.Height; first.Dispose();
    using (var o = new Bitmap(w * scale, (h * scale + 4) * files.Length)) using (var g = Graphics.FromImage(o)) {
      g.Clear(Color.Magenta); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
      for (int i = 0; i < files.Length; i++) using (var b = new Bitmap(files[i])) g.DrawImage(b, 0, i * (h * scale + 4), w * scale, h * scale);
      o.Save(target);
    }
  }
}
"@
}
function Pump($ms) { $w = [Diagnostics.Stopwatch]::StartNew(); while ($w.ElapsedMilliseconds -lt $ms) { [Windows.Forms.Application]::DoEvents(); [Threading.Thread]::Sleep(8) } }
function Mains { @(Get-CimInstance Win32_Process -Filter "Name='IslandBar.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine -notmatch '--watchdog' }) }
function StopBar { Get-Process IslandBar -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800 }
function WriteCfg($theme, $autohide) {
  $c = Get-Content $bak -Raw -Encoding UTF8 | ConvertFrom-Json
  $c.AutoHideBar = $autohide; $c.Theme = $theme
  [IO.File]::WriteAllText($cfg, ($c | ConvertTo-Json -Depth 8))
}
function StartBar($exe) { [D]::Move(960, 500); $null = Start-Process $exe; Pump 6000; (Mains)[0].ProcessId }
function TaskButtons($id) {
  $pidCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$id)
  $listCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'TaskList')
  $btnCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
  foreach ($t in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $pidCond)) {
    foreach ($l in $t.FindAll([Windows.Automation.TreeScope]::Descendants, $listCond)) {
      $r = $l.Current.BoundingRectangle
      if ($r.Left -ge 0 -and $r.Left -lt 1920 -and $r.Top -ge 0) {
        return @($l.FindAll([Windows.Automation.TreeScope]::Descendants, $btnCond) | ForEach-Object { $b = $_.Current.BoundingRectangle; [pscustomobject]@{ Name = $_.Current.Name; L = [int]$b.Left; T = [int]$b.Top; W = [int]$b.Width; H = [int]$b.Height; X = [int]($b.Left + $b.Width / 2); Y = [int]($b.Top + $b.Height / 2) } })
      }
    }
  }
  @()
}
function Show-Buttons($id) { ((TaskButtons $id) | ForEach-Object { "$($_.Name.Substring(0,[Math]::Min(10,$_.Name.Length)))@$($_.L)" }) -join ' | ' }
function Pins { ((Get-Content $cfg -Raw -Encoding UTF8 | ConvertFrom-Json).Pinned | ForEach-Object { $_.Name }) -join ' > ' }
function Glide($x0, $y0, $x1, $y1, $steps, $delay) { for ($i = 1; $i -le $steps; $i++) { [D]::Move([int]($x0 + ($x1 - $x0) * $i / $steps), [int]($y0 + ($y1 - $y0) * $i / $steps)); Pump $delay } }
function Frames($prefix, $count, $x, $y, $w, $h) {
  $f = @()
  for ($i = 0; $i -lt $count; $i++) { $p = "$s\$prefix-$i.png"; [D]::Shot($p, $x, $y, $w, $h); $f += $p; Pump 25 }
  $f
}
