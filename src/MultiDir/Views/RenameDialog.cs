using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MultiDir.Views;

/// <summary>Small dark rename box (F2 / 右鍵 →「重新命名」). Pre-selects the name without its extension, like Explorer.</summary>
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;

    private RenameDialog(string name, bool isFile)
    {
        Title = "重新命名";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Surface");

        _box = new TextBox { Text = name, Style = (Style)Application.Current.FindResource("FieldBox"), Height = 26, Margin = new Thickness(12, 12, 12, 8) };
        var ok = new Button { Content = "確定", IsDefault = true, Width = 76, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 76, Height = 26 };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel();
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            int dot = isFile ? name.LastIndexOf('.') : -1;
            _box.Select(0, dot > 0 ? dot : name.Length);
        };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    /// <summary>Asks for a new name and renames the file or folder. Returns the new full path, or null.</summary>
    public static string? Run(Window owner, string fullPath)
    {
        bool isFile = File.Exists(fullPath);
        var oldName = Path.GetFileName(fullPath.TrimEnd('\\'));
        var dialog = new RenameDialog(oldName, isFile) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;

        var newName = dialog._box.Text.Trim();
        if (newName.Length == 0 || newName == oldName) return null;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(owner, "檔名不能包含下列字元：\\ / : * ? \" < > |", "重新命名", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var target = Path.Combine(Path.GetDirectoryName(fullPath.TrimEnd('\\'))!, newName);
        try
        {
            if (isFile) File.Move(fullPath, target);
            else Directory.Move(fullPath, target);
            return target;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "無法重新命名", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }
}
