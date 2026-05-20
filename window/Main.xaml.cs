using Odin_Flash.Controls;
using OdinFlash.Protocol.Port;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using MsgType = OdinFlash.Protocol.util.utils.MsgType;

namespace Odin_Flash.window
{
    /// <summary>
    /// Ventana principal: Flash embebido, log y Stop → Odin.StopOperations.
    /// </summary>
    public partial class Main : Window
    {
        public Flash Flash;
        private readonly DispatcherTimer DeviceStatusTimer;
        private bool IsCheckingDeviceStatus;
        private bool IsRunningOperation;
        private string LastDetectedComLabel;

        public Main()
        {
            InitializeComponent();

            Flash = new Flash();
            Flash.Log += Flash_Log;
            Flash.ProgressChanged += Flash_ProgressChanged;
            Flash.IsRunning += Flash_IsRunning;
            FrmMain.Navigate(Flash);

            DeviceStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            DeviceStatusTimer.Tick += DeviceStatusTimer_Tick;
            DeviceStatusTimer.Start();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Flash_IsRunning(bool IsRunning, string process)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsRunningOperation = IsRunning;
                Flash.ControlsManage(IsRunning);
                BtnFlash.IsEnabled = !IsRunning;
                BtnReadPit.IsEnabled = !IsRunning;
                BtnStop.IsEnabled = IsRunning;
            });
        }

        private async void DeviceStatusTimer_Tick(object sender, EventArgs e)
        {
            if (IsCheckingDeviceStatus || IsRunningOperation)
                return;

            try
            {
                IsCheckingDeviceStatus = true;
                var device = await PortComm.FindDownloadModePort();
                if (!string.IsNullOrEmpty(device.COM))
                {
                    LastDetectedComLabel = FormatComPort(device.COM);
                    SetDeviceStatus("Download Mode", BuildReadyStatusText(), true);
                }
                else
                {
                    LastDetectedComLabel = null;
                    SetDeviceStatus("Disconnected", "Ready", false);
                }
            }
            finally
            {
                IsCheckingDeviceStatus = false;
            }
        }

        private void Flash_ProgressChanged(string filename, long max, long value, long WritenSize)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ProgBar.Maximum = max;
                ProgBar.Value = value;
                Events.Text = $"{filename} | {WritenSize:###,###,###}";
                Events.Foreground = GetBrush("FileTextBrush", Brushes.DodgerBlue);
            });
        }

        private void Flash_Log(string Text, MsgType Color, bool IsError = false)
        {
            if (string.IsNullOrEmpty(Text))
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateDeviceStatusFromLog(Text, IsError);

                var paragraph = new Paragraph { Margin = new Thickness(0) };
                var run = new Run(Text) { FontSize = 10.5 };

                if (IsError)
                    run.Foreground = Brushes.YellowGreen;
                else if (Color == MsgType.Result)
                {
                    run.Foreground = Brushes.Cyan;
                    run.FontWeight = FontWeights.Bold;
                }
                else
                    run.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#A0A0A0");

                paragraph.Inlines.Add(run);
                RichLog.Document.Blocks.Add(paragraph);
                RichLog.ScrollToEnd();
            });
        }

        private void UpdateDeviceStatusFromLog(string text, bool isError)
        {
            var normalizedText = text.Trim();
            if (normalizedText.IndexOf("Not found", StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedText.IndexOf("Download Mode Port", StringComparison.OrdinalIgnoreCase) >= 0 && isError
                || normalizedText.IndexOf("LOKE handshake not detected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LastDetectedComLabel = null;
                SetDeviceStatus("Disconnected", "Ready", false);
                return;
            }

            if (normalizedText.IndexOf("Checking Download Mode", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(normalizedText, "ODIN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedText, "Initialized", StringComparison.OrdinalIgnoreCase))
            {
                SetDeviceStatus("Download Mode", BuildReadyStatusText(), true);
            }
        }

        private string BuildReadyStatusText()
        {
            return string.IsNullOrWhiteSpace(LastDetectedComLabel)
                ? "Ready"
                : $"Ready {LastDetectedComLabel}";
        }

        private static string FormatComPort(string com)
        {
            if (string.IsNullOrWhiteSpace(com))
                return string.Empty;

            com = com.Trim().ToUpperInvariant();
            return com.StartsWith("COM", StringComparison.Ordinal)
                ? $"COM: {com.Substring(3)}"
                : com;
        }

        private void SetDeviceStatus(string deviceState, string eventState, bool connected)
        {
            ConnectedName.Text = deviceState;
            ConnectedName.Foreground = connected
                ? GetBrush("PrimaryHueDarkForegroundBrush", Brushes.White)
                : GetBrush("ErrorBrush", Brushes.IndianRed);

            Events.Text = eventState;
            Events.Foreground = connected
                ? GetBrush("ReadyBrush", Brushes.LightGreen)
                : GetBrush("TabColorForegroundBrush", Brushes.LightGray);
        }

        private static Brush GetBrush(string key, Brush fallback)
        {
            return Application.Current.TryFindResource(key) as Brush ?? fallback;
        }

        private void RichLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            BtnClearRich.Visibility = RichLog.Document.Blocks.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnClearRich_Click(object sender, RoutedEventArgs e)
        {
            RichLog.Document.Blocks.Clear();
        }

        private void BtnFlash_Click(object sender, RoutedEventArgs e)
        {
            Flash.BtnFlash_Click(sender, e);
        }

        private void BtnReadPit_Click(object sender, RoutedEventArgs e)
        {
            Flash.BtnReadPit_Click(sender, e);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            Flash.Odin.StopOperations();
        }

        private void PhonePartsLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RichLog.Document.Blocks.Clear();
            Flash_Log("Odin Flash 1.0.1", MsgType.Message);
            Flash_Log("------------------------------------", MsgType.Message);
        }
    }
}
