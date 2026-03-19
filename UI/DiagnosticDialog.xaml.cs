using System.Windows;
using System.Windows.Data;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Markup;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

using AroniumBridge.Models;
using AroniumBridge.Services;

namespace AroniumBridge.UI;

public partial class DiagnosticDialog : Window
{
    private readonly List<DiagnosticResult> _results;
    private readonly AppSettings _settings;

    public DiagnosticDialog(List<DiagnosticResult> results, AppSettings settings)
    {
        InitializeComponent();
        _results = results;
        _settings = settings;
        RefreshUI();
    }

    private void RefreshUI()
    {
        ResultsList.ItemsSource = null;
        ResultsList.ItemsSource = _results;

        var hasErrors = _results.Any(r => r.Status == DiagnosticStatus.Error);
        var com0comMissing = _results.Any(r => r.Message.Contains("com0com is not installed"));
        var driverStopped = _results.Any(r => r.Message.Contains("driver is Stopped"));
        var noPairs = _results.Any(r => r.Message.Contains("no virtual port pairs"));

        SecondaryBtn.Visibility = Visibility.Collapsed;
        SecondaryBtn.IsEnabled = true;

        if (com0comMissing)
        {
            SecondaryBtn.Visibility = Visibility.Visible;
            SecondaryBtn.Content = "Install com0com";
        }
        else if (driverStopped)
        {
            SecondaryBtn.Visibility = Visibility.Visible;
            SecondaryBtn.Content = "Start Driver";
        }
        else if (hasErrors && noPairs)
        {
            SecondaryBtn.Visibility = Visibility.Visible;
            SecondaryBtn.Content = "Create Port Pair";
        }

        PrimaryBtn.Content = hasErrors ? "Close" : "Start Services";
        if (!hasErrors)
        {
            PrimaryBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x48, 0xBB, 0x78));
        }
        else
        {
            PrimaryBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x31, 0x82, 0xCE));
        }
    }

    private void RefreshDiagnostics()
    {
        _results.Clear();
        _results.AddRange(Com0ComService.PerformComprehensiveDiagnostic(_settings.VirtualComPort));
        _results.Add(HardwareService.VerifyHardwarePort(_settings.ComPort));
        RefreshUI();
    }

    private void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryBtn.Content.ToString() == "Start Services")
        {
            DialogResult = true;
        }
        Close();
    }

    private async void SecondaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SecondaryBtn.Content.ToString() == "Install com0com")
        {
            SecondaryBtn.IsEnabled = false;
            SecondaryBtn.Content = "Installing...";
            
            var progress = new Progress<int>(p => {
                SecondaryBtn.Content = $"Installing ({p}%)";
            });

            var (success, error) = await Com0ComService.DownloadAndInstallAsync(progress);
            
            if (success)
            {
                MessageBox.Show("com0com installed successfully! Please restart the application.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
            }
            else
            {
                MessageBox.Show($"Installation failed: {error}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SecondaryBtn.IsEnabled = true;
                SecondaryBtn.Content = "Install com0com";
            }
        }
        else if (SecondaryBtn.Content.ToString() == "Create Port Pair")
        {
            var (success, error) = Com0ComService.CreatePair(_settings.VirtualComPort, "COM4");
            if (success)
            {
                MessageBox.Show("Port pair created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshDiagnostics();
            }
            else
            {
                MessageBox.Show($"Failed to create pair: {error}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (SecondaryBtn.Content.ToString() == "Start Driver")
        {
            SecondaryBtn.IsEnabled = false;
            SecondaryBtn.Content = "Starting...";
            var (success, error) = await Com0ComService.StartDriverAsync();
            if (success)
            {
                RefreshDiagnostics();
            }
            else
            {
                MessageBox.Show($"Failed to start driver: {error}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshUI();
            }
        }
    }
}

public class StringNullOrEmptyToVisibilityConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
