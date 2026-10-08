using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SshKa.App.Localization;

namespace SshKa.App.Views;

/// <summary>Окно «О программе». Текст описания будет дополнен позже.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = Loc.T("О программе");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://SshKa.App/Assets/sshka.png"));
            panel.Children.Add(new Image { Source = new Bitmap(stream), Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center });
        }
        catch
        {
            // без картинки окно остаётся рабочим
        }

        var version = typeof(AboutWindow).Assembly.GetName().Version;
        panel.Children.Add(new TextBlock { Text = "SSH-ka", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = version is null ? "" : $"{Loc.T("Версия")} {version.Major}.{version.Minor}.{version.Build}",
            Opacity = 0.65,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Бесплатный клиент SSH и Telnet."),
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);
        Content = panel;
    }
}
