using System.Windows;
using System.Windows.Controls;

namespace GrupBar;

public partial class NameDialog : Window
{
    public NameDialog(string name)
    {
        InitializeComponent();

        NameBox.Text = name;
        NameBox.SelectAll();
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(name);
    }

    public string GroupName => NameBox.Text.Trim();

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
