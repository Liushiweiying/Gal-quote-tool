using System.Windows;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Views;

public partial class BlacklistWindow : Window
{
    private readonly UsageData _data;

    public BlacklistWindow(Window owner, UsageData data)
    {
        InitializeComponent();
        Owner = owner;
        _data = data;
        RefreshList();
    }

    private void RefreshList()
    {
        BlacklistBox.ItemsSource = null;
        BlacklistBox.Items.Clear();
        foreach (var item in _data.Blacklist)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = $"✕ {item}",
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btn.Click += (_, _) =>
            {
                _data.Blacklist.Remove(item);
                RefreshList();
            };
            BlacklistBox.Items.Add(btn);
        }
    }

    private void OnAdd(object s, RoutedEventArgs e)
    {
        var name = ProcessInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!name.EndsWith(".exe")) name += ".exe";
        if (!_data.Blacklist.Contains(name, StringComparer.OrdinalIgnoreCase))
            _data.Blacklist.Add(name);
        ProcessInput.Clear();
        RefreshList();
    }

    private void OnClose(object s, RoutedEventArgs e) => DialogResult = true;
}
