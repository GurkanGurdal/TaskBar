Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object Windows.Forms.Form
$f.Text = 'TestSabitsiz'; $f.StartPosition = 'Manual'; $f.Location = New-Object Drawing.Point(620, 300); $f.Size = New-Object Drawing.Size(220, 120)
$f.Show(); $f.Hide()
[Windows.Forms.Application]::Run($f)
