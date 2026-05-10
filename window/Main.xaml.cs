using Odin_Flash.Controls;
using OdinProtocolAtack.Port;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using MsgType = OdinProtocolAtack.util.utils.MsgType;

namespace Odin_Flash.window
{
    /// <summary>
    /// Equivalente a Freya.window.Main: Flash embebido, log enriquecido, Stop → Odin.StopOperations.
    /// MsgType viene de OdinProtocolAtack (antes SharpOdinClient.util.utils.MsgType).
    /// </summary>
    public partial class Main : FluentWindow
    {

        public Flash Flash;
        private readonly DispatcherTimer DeviceStatusTimer;
        private bool IsCheckingDeviceStatus;
        private bool IsRunningOperation;
        private string LastDetectedComLabel;
        public Main()
        {
            InitializeComponent();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);

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

        private void Flash_IsRunning(bool IsRunning, string process)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsRunningOperation = IsRunning;
                Flash.ControlsManage(IsRunning);
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
            Application.Current.Dispatcher.Invoke(() => {
                ProgBar.Maximum = max;
                ProgBar.Value = value;
                Events.Text = $"{filename} | {WritenSize:###,###,###}";
                Events.Foreground = GetBrush("FileTextBrush", Brushes.DodgerBlue);
            });
        }

        private void Flash_Log(string Text, MsgType Color, bool IsError = false)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(Text))
                {
                    return;
                }
                UpdateDeviceStatusFromLog(Text, IsError);
                if (Color == MsgType.Message)
                    Text = "\n" + Text;
                TextRange rangeOfText1 = new TextRange(RichLog.Document.ContentEnd, RichLog.Document.ContentEnd);
                rangeOfText1.ApplyPropertyValue(TextBlock.TextAlignmentProperty, TextAlignment.Left);
                RichLog.FlowDirection = FlowDirection.LeftToRight;
                rangeOfText1.Text = Text;
                if (IsError)
                {
                    rangeOfText1.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.YellowGreen);
                }
                else if (Color == MsgType.Result)
                {
                    rangeOfText1.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Cyan);
                }
                else
                {
                    rangeOfText1.ApplyPropertyValue(TextElement.ForegroundProperty, (SolidColorBrush)new BrushConverter().ConvertFrom("#59b369"));
                }
                switch (Color)
                {
                    case MsgType.Result:
                        rangeOfText1.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
                        break;
                    case MsgType.Message:
                        rangeOfText1.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                        break;
                }
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
            if (RichLog.Document.Blocks.Count == 0)
            {
                BtnClearRich.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnClearRich.Visibility = Visibility.Visible;
            }
        }

        private void BtnClearRich_Click(object sender, RoutedEventArgs e)
        {
            RichLog.Document.Blocks.Clear();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            Flash.Odin.StopOperations();
        }

        private void PhonePartsLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Flash_Log("════════════════════════════════════════════", MsgType.Message);
            Flash_Log("Odin Flash v" + System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString(), MsgType.Message);
            Flash_Log("════════════════════════════════════════════", MsgType.Message);
        }

    }
}
