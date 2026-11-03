using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using ManagedShell.Common.Logging;

namespace IslandBar;

/// <summary>Belirli bir anda hatırlatılacak not.</summary>
public sealed class Reminder
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Text { get; init; } = string.Empty;

    public DateTime DueAt { get; set; }

    /// <summary>Bu çalışmada kartı gösterildi; kullanıcı Tamam ya da Ertele diyene kadar yeniden gösterilmez.</summary>
    [JsonIgnore]
    public bool Shown { get; set; }

    [JsonIgnore]
    public string TimeLabel => DueAt.ToString("t");
}

/// <summary>
/// Hatırlatıcılar (kullanıcının isteği: hızlı not yerine; takvim kartından eklenir). Bellekte değil diskte tutulur
/// (<c>%AppData%\IslandBar\reminders.json</c>), bilgisayar yeniden başlayınca da durur. Zamanı gelen ya da kapalıyken
/// geçen hatırlatıcı <see cref="Due"/> ile bildirilir; kullanıcı Tamam diyene kadar listeden çıkmaz.
/// </summary>
public sealed class ReminderStore
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "reminders.json");

    private readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromSeconds(1) };

    private ReminderStore()
    {
        _check.Tick += (_, _) => CheckDue();
    }

    public static ReminderStore Shared { get; } = new();

    /// <summary>Zamanı geldi; kartı gösterilmeli.</summary>
    public event Action<Reminder>? Due;

    /// <summary>Tarihe göre sıralı.</summary>
    public ObservableCollection<Reminder> Items { get; } = new();

    /// <summary>Kayıtlıları okur ve izlemeye başlar; kapalıyken zamanı geçenler ilk saniyede bildirilir.</summary>
    public void Start()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (Reminder reminder in JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? [])
                {
                    Insert(reminder);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"ReminderStore: Could not read {FilePath}: {e.Message}");
        }
        _check.Start();
    }

    public void Add(string text, DateTime dueAt)
    {
        Insert(new Reminder { Text = text.Trim(), DueAt = dueAt });
        Save();
    }

    public void Remove(Reminder reminder)
    {
        Items.Remove(reminder);
        Save();
    }

    public void Snooze(Reminder reminder, TimeSpan delay)
    {
        Items.Remove(reminder);
        reminder.DueAt = DateTime.Now + delay;
        reminder.Shown = false;
        Insert(reminder);
        Save();
    }

    public IEnumerable<Reminder> On(DateTime day)
    {
        return Items.Where(reminder => reminder.DueAt.Date == day.Date);
    }

    private void Insert(Reminder reminder)
    {
        int at = 0;
        while (at < Items.Count && Items[at].DueAt <= reminder.DueAt)
        {
            at++;
        }
        Items.Insert(at, reminder);
    }

    private void CheckDue()
    {
        DateTime now = DateTime.Now;
        foreach (Reminder reminder in Items.Where(item => !item.Shown && item.DueAt <= now).ToList())
        {
            reminder.Shown = true;
            Due?.Invoke(reminder);
        }
    }

    /// <summary>Önce yanına yazılır, sonra yer değiştirir: yarım dosya kayıtların hepsini silerdi.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"ReminderStore: Could not save {FilePath}: {e.Message}");
        }
    }
}
