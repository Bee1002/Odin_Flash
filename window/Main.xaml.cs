using Odin_Flash.Controls;
using Odin_Flash.Util;
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

        static readonly SolidColorBrush SamFwLinkBrush =
            new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
        private string LastDetectedComLabel;
        private DateTime LastTransferSampleUtc;
        private long LastTransferWrittenBytes;
        private long LastBatchTotalBytes;
        private bool HasTransferSample;

        private readonly object ProgressSync = new object();
        private int ProgressThrottleMs;
        private int LastProgressUiTick;
        private bool ProgressUiDirty;
        private string PendingFilename;
        private long PendingPartitionMax;
        private long PendingPartitionValue;
        private long PendingWrittenSize;
        private long PendingBatchTotal;
        private long PendingBatchWritten;

        // Throttle progreso WPF (Ui:ProgressThrottleMs): evita Dispatcher.Invoke por cada trozo NAND (~34k en 8 GB).
        public Main()
        {
            InitializeComponent();
            AppIconHelper.ApplyTo(this);

            ProgressThrottleMs = LokePerformanceSettings.ProgressThrottleMs;

            Flash = new Flash();
            Flash.Log += Flash_Log;
            Flash.ProgressChanged += Flash_ProgressChanged;
            Flash.IsRunning += Flash_IsRunning;
            Flash.FlashCompleted += Flash_FlashCompleted;
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

                if (IsRunning)
                    ResetProgressUi();
                else
                    FlushPendingProgressUi();
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

        private void Flash_ProgressChanged(
            string filename,
            long partitionMax,
            long partitionValue,
            long writtenSize,
            long batchTotal,
            long batchWritten)
        {
            lock (ProgressSync)
            {
                PendingFilename = filename;
                PendingPartitionMax = partitionMax;
                PendingPartitionValue = partitionValue;
                PendingWrittenSize = writtenSize;
                PendingBatchTotal = batchTotal;
                PendingBatchWritten = batchWritten;
                ProgressUiDirty = true;
            }

            if (ProgressThrottleMs <= 0)
            {
                ApplyProgressUi(force: true);
                return;
            }

            var now = Environment.TickCount;
            lock (ProgressSync)
            {
                if (!ProgressUiDirty)
                    return;
                if (unchecked(now - LastProgressUiTick) < ProgressThrottleMs)
                    return;
            }

            ApplyProgressUi(force: false);
        }

        private void FlushPendingProgressUi()
        {
            ApplyProgressUi(force: true);
        }

        private void ApplyProgressUi(bool force)
        {
            string filename;
            long partitionMax;
            long partitionValue;
            long writtenSize;
            long batchTotal;
            long batchWritten;

            lock (ProgressSync)
            {
                if (!ProgressUiDirty && !force)
                    return;

                filename = PendingFilename;
                partitionMax = PendingPartitionMax;
                partitionValue = PendingPartitionValue;
                writtenSize = PendingWrittenSize;
                batchTotal = PendingBatchTotal;
                batchWritten = PendingBatchWritten;
                ProgressUiDirty = false;
                LastProgressUiTick = Environment.TickCount;
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                var partitionPercent = ToPercent(partitionValue, partitionMax);
                var batchPercent = ToPercent(batchWritten, batchTotal);

                ProgBarPartition.Value = partitionPercent;
                ProgBarBatch.Value = batchPercent;
                TxtPartitionPercent.Text = FormatPercentLabel(partitionPercent);
                TxtBatchPercent.Text = FormatPercentLabel(batchPercent);

                LastBatchTotalBytes = batchTotal;
                TxtTotalSize.Text = $"Total Size : {Util.Util.GetBytesReadable(batchTotal)}";
                TxtWrittenSize.Text = $"Written Size : {Util.Util.GetBytesReadable(batchWritten)}";
                UpdateTransferRate(batchWritten);

                Events.Text = string.IsNullOrEmpty(filename)
                    ? writtenSize.ToString("###,###,###")
                    : $"{filename} | {writtenSize:###,###,###}";
                Events.Foreground = GetBrush("FileTextBrush", Brushes.DodgerBlue);
            });
        }

        private void ResetProgressUi()
        {
            ProgBarPartition.Value = 0;
            ProgBarBatch.Value = 0;
            TxtPartitionPercent.Text = "0%";
            TxtBatchPercent.Text = "0%";
            TxtTotalSize.Text = "Total Size : 0 B";
            TxtWrittenSize.Text = "Written Size : 0 B";
            TxtTransferRate.Text = "Transfer Rate : 0 B/s";
            LastTransferSampleUtc = DateTime.UtcNow;
            LastTransferWrittenBytes = 0;
            LastBatchTotalBytes = 0L;
            HasTransferSample = false;
            lock (ProgressSync)
            {
                ProgressUiDirty = false;
                LastProgressUiTick = 0;
            }
        }

        /// <summary>Flash OK: barras y Written = Total (estilo Odin al terminar).</summary>
        private void FinalizeProgressUiOnSuccess(long batchWritten, TimeSpan elapsed)
        {
            ProgBarPartition.Value = 100;
            ProgBarBatch.Value = 100;
            TxtPartitionPercent.Text = "100%";
            TxtBatchPercent.Text = "100%";

            if (LastBatchTotalBytes > 0L)
            {
                var readable = Util.Util.GetBytesReadable(LastBatchTotalBytes);
                TxtTotalSize.Text = $"Total Size : {readable}";
                TxtWrittenSize.Text = $"Written Size : {readable}";
                if (elapsed.TotalSeconds > 0.1 && batchWritten > 0L)
                {
                    var avgRate = batchWritten / elapsed.TotalSeconds;
                    TxtTransferRate.Text = $"Transfer Rate : {Util.Util.GetBytesReadable((long)avgRate)}/s";
                }
            }
            else if (TxtTotalSize.Text.StartsWith("Total Size : ", StringComparison.Ordinal))
            {
                TxtWrittenSize.Text = "Written Size : " + TxtTotalSize.Text.Substring("Total Size : ".Length);
            }
        }

        private static string FormatPercentLabel(double percent)
        {
            if (percent <= 0)
                return "0%";
            if (percent >= 100)
                return "100%";
            return $"{percent:0}%";
        }

        private static double ToPercent(long value, long max)
        {
            if (max <= 0L)
                return 0;

            var percent = value * 100.0 / max;
            if (percent < 0)
                return 0;
            if (percent > 100)
                return 100;
            return percent;
        }

        private void UpdateTransferRate(long batchWritten)
        {
            var now = DateTime.UtcNow;
            if (HasTransferSample)
            {
                var elapsedSeconds = (now - LastTransferSampleUtc).TotalSeconds;
                if (elapsedSeconds >= 0.25)
                {
                    var delta = batchWritten - LastTransferWrittenBytes;
                    if (delta >= 0)
                    {
                        var bytesPerSecond = delta / elapsedSeconds;
                        TxtTransferRate.Text = $"Transfer Rate : {Util.Util.GetBytesReadable((long)bytesPerSecond)}/s";
                    }

                    LastTransferSampleUtc = now;
                    LastTransferWrittenBytes = batchWritten;
                }
            }
            else
            {
                HasTransferSample = true;
                LastTransferSampleUtc = now;
                LastTransferWrittenBytes = batchWritten;
            }
        }

        private void Flash_FlashCompleted(TimeSpan elapsed)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                FinalizeProgressUiOnSuccess(LastBatchTotalBytes, elapsed);
                AppendCompletedLog("All Tasks Is Completed", elapsed);

                Events.Text = "All Tasks Is Completed";
                Events.Foreground = Brushes.White;
            });
        }

        private void AppendCompletedLog(string title, TimeSpan elapsed)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 0) };
            paragraph.Inlines.Add(new Run(title)
            {
                FontSize = 10.5,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            });
            paragraph.Inlines.Add(new Run($" - Elapsed Time : {Util.Util.FormatElapsedOdin(elapsed)}")
            {
                FontSize = 10.5,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF9800")
            });
            RichLog.Document.Blocks.Add(paragraph);
            RichLog.ScrollToEnd();
        }

        private void Flash_Log(string Text, MsgType Color, bool IsError = false, string navigateUri = null)
        {
            if (string.IsNullOrEmpty(Text))
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateDeviceStatusFromLog(Text, IsError);

                var paragraph = new Paragraph { Margin = new Thickness(0) };

                if (!IsError
                    && Color == MsgType.Result
                    && !string.IsNullOrWhiteSpace(navigateUri)
                    && Uri.TryCreate(navigateUri, UriKind.Absolute, out var uri))
                {
                    var run = new Run(Text)
                    {
                        FontSize = 10.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = SamFwLinkBrush
                    };
                    var link = new Hyperlink(run)
                    {
                        NavigateUri = uri,
                        TextDecorations = TextDecorations.Underline,
                        Foreground = SamFwLinkBrush
                    };
                    link.Click += LogHyperlink_Click;
                    ToolTipService.SetToolTip(link, $"Abrir firmware en SamFW\n{uri}");
                    ToolTipService.SetInitialShowDelay(link, 150);
                    paragraph.Inlines.Add(link);
                }
                else
                {
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
                }

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

        private void RichLog_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryOpenLogHyperlink(e.GetPosition(RichLog)))
                e.Handled = true;
        }

        private void RichLog_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            RichLog.Cursor = FindLogHyperlink(e.GetPosition(RichLog)) != null
                ? Cursors.Hand
                : null;
        }

        private void LogHyperlink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Hyperlink link && link.NavigateUri != null)
            {
                OpenExternalUri(link.NavigateUri);
                e.Handled = true;
            }
        }

        private bool TryOpenLogHyperlink(Point point)
        {
            var link = FindLogHyperlink(point);
            if (link?.NavigateUri == null)
                return false;

            OpenExternalUri(link.NavigateUri);
            return true;
        }

        private Hyperlink FindLogHyperlink(Point point)
        {
            var position = RichLog.GetPositionFromPoint(point, true);
            if (position == null)
                return null;

            var element = position.Parent as TextElement;
            while (element != null)
            {
                if (element is Hyperlink hyperlink)
                    return hyperlink;
                element = element.Parent as TextElement;
            }

            return null;
        }

        private static void OpenExternalUri(Uri uri)
        {
            if (uri == null)
                return;

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
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
            OpenExternalUri(e.Uri);
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
