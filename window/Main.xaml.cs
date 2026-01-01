using Odin_Flash.Class;
using Odin_Flash.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static Odin_Flash.Util.Util;

namespace Odin_Flash.window
{
    /// <summary>
    /// Interaction logic for Main.xaml
    /// </summary>
    public partial class Main : Window
    {
        // Variable para rastrear el último tamaño reportado (evita saturar CPU)
        private long _lastReportedSize = 0;
        private const long PROGRESS_UPDATE_INTERVAL = 1024 * 1024; // Actualizar cada 1MB

        public Flash Flash;
        public Main()
        {
            InitializeComponent();
            Flash = new Flash();
            Flash.Log += Flash_Log;
            Flash.ProgressChanged += Flash_ProgressChanged;
            Flash.IsRunning += Flash_IsRunning;
            FrmMain.Navigate(Flash);
        }

        private void Flash_IsRunning(bool IsRunning, string process)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Flash.ControlsManage(IsRunning);
                BtnStop.IsEnabled = IsRunning;
            });
        }

        /// <summary>
        /// Maneja el cambio de progreso del flasheo
        /// Optimizado para archivos grandes: actualiza solo cada 1MB para no saturar el hilo de E/S
        /// Usa BeginInvoke en lugar de Invoke para no bloquear el hilo de envío
        /// </summary>
        private void Flash_ProgressChanged(string filename, long max, long value, long WritenSize)
        {
            // Actualizar solo cada 1MB para no saturar el hilo de E/S
            // Para archivos de 9GB, esto reduce las actualizaciones de ~9000 a ~9000/1024 = ~9 actualizaciones por segundo máximo
            long sizeDifference = WritenSize - _lastReportedSize;
            
            if (sizeDifference >= PROGRESS_UPDATE_INTERVAL || WritenSize == max || _lastReportedSize == 0)
            {
                _lastReportedSize = WritenSize;
                
                // Usar BeginInvoke en lugar de Invoke para no bloquear el hilo de envío
                // Esto es crítico para evitar que el hilo de E/S se detenga esperando la UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                    ProgBar.Maximum = max;
                    ProgBar.Value = value;
                    
                    // Formato mejorado: mostrar MB en lugar de bytes para mejor legibilidad
                    long writenMB = WritenSize / (1024 * 1024);
                    long maxMB = max / (1024 * 1024);
                    Events.Content = $"{filename} | {writenMB} MB / {maxMB} MB";
                }));
            }
        }

        private void Flash_Log(string Text, SharpOdinClient.util.utils.MsgType Color, bool IsError = false)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(Text))
                {
                    return;
                }
                TextRange rangeOfText1 = new TextRange(RichLog.Document.ContentEnd, RichLog.Document.ContentEnd);
                if (Color == SharpOdinClient.util.utils.MsgType.Message)
                {
                    Text = $"\n{Text}";
                }
                rangeOfText1.ApplyPropertyValue(TextBlock.TextAlignmentProperty, TextAlignment.Left);
                RichLog.FlowDirection = FlowDirection.LeftToRight;
                rangeOfText1.Text = Text;
                if(IsError)
                {
                    rangeOfText1.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.YellowGreen);
                }else if(Color == SharpOdinClient.util.utils.MsgType.Result)
                {
                    rangeOfText1.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Cyan);
                }
                else
                {
                    rangeOfText1.ApplyPropertyValue(TextElement.ForegroundProperty, (SolidColorBrush)new BrushConverter().ConvertFrom("#59b369"));
                }
                switch (Color)
                {
                    case SharpOdinClient.util.utils.MsgType.Result:
                        {
                            rangeOfText1.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
                            break;
                        }
                    case SharpOdinClient.util.utils.MsgType.Message:
                        {
                            rangeOfText1.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                            break;
                        }
                }
                RichLog.ScrollToEnd();
            });
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
            Application.Current.Shutdown();
            Process.GetCurrentProcess().Kill();
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

        private void BtnMinMaximizeWindow_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMinMaximizeWindow.Content = "□";
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMinMaximizeWindow.Content = "❐";
            }

        }
        private void Grid_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }
        private void BtnMinMaxWindow_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;

        }
       

        private void BtnClearRich_Click(object sender, RoutedEventArgs e)
        {
            RichLog.Document.Blocks.Clear();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            Flash.Odin.StopOperations();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Flash_Log("════════════════════════════════════════════",  SharpOdinClient.util.utils.MsgType.Message);
            Flash_Log("Odin Flash v" + System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString(),  SharpOdinClient.util.utils.MsgType.Message);
            Flash_Log("════════════════════════════════════════════",  SharpOdinClient.util.utils.MsgType.Message);
        }

        private void ScreenShot_Click(object sender, RoutedEventArgs e)
        {
            var image = ScreenCapture.CaptureActiveWindow();
            var SavePath = $"{Util.Util.MyPath}\\backup\\Screenshot\\{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")}.Jpeg";
            Util.Util.CreatFolder($"{Util.Util.MyPath}\\backup\\Screenshot");
            image.Save(SavePath, ImageFormat.Jpeg);
            Flash_Log("ScreenShot Saved : ", SharpOdinClient.util.utils.MsgType.Message);
            Flash_Log(SavePath, SharpOdinClient.util.utils.MsgType.Result);
        }
    }
}
