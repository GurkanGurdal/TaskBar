using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;

namespace GrupBar;

/// <summary>
/// Bildirim alanındaki GrupBar ikonu: Ayarlar ve Çıkış.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _image;
    private readonly ContextMenuStrip _menu;
    private readonly Action _openSettings;

    public TrayIcon(Action openSettings, Action exit)
    {
        _openSettings = openSettings;

        using (Stream stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/GrupBar.ico")).Stream)
        {
            _image = new Icon(stream, SystemInformation.SmallIconSize);
        }

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Ayarlar...", null, (_, _) => RunAfterMenu(openSettings));
        _menu.Items.Add("Çıkış", null, (_, _) => RunAfterMenu(exit));

        _notifyIcon = new NotifyIcon
        {
            Icon = _image,
            Text = "GrupBar",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
    }

    public void Dispose()
    {
        // Gizlemeden kapanırsa ikon, fare üstüne gelene kadar tepside hayalet olarak kalır.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _openSettings();
        }
    }

    private static void RunAfterMenu(Action action)
    {
        // Menü kapanmasını bitirsin; Çıkış bu menüyü de yok ediyor.
        Dispatcher.CurrentDispatcher.BeginInvoke(action);
    }
}
