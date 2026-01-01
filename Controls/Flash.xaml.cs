using Odin_Flash.Util;
using Odin_Flash.Class;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Odin_Flash.Controls
{
    /// <summary>
    /// Interaction logic for Flash.xaml
    /// </summary>
    public partial class Flash : UserControl
    {
        public FlashField BLPackage = new FlashField("BL (bootloader) file package [tar,md5]");
        public FlashField APPackage = new FlashField("AP (PDA) file package [tar,md5]");
        public FlashField CPPackage = new FlashField("CP (Modem) file package [tar,md5]");
        public FlashField CSCPackage = new FlashField("CSC file package [tar,md5]");
        public OdinEngine OdinEngine; // Motor de protocolo Odin basado en análisis original

        public event Action<long, long> ProgressChanged;
        public event Action<string, LogLevel> Log;
        public event Odin_Flash.Util.Util.IsRunningProcessDelegate IsRunning;

        public Flash()
        {
            InitializeComponent();

            Features.Children.Add(BLPackage);
            Features.Children.Add(APPackage);
            Features.Children.Add(CPPackage);
            Features.Children.Add(CSCPackage);
            BLPackage.PitDetect += BLPackage_PitDetect;
            APPackage.PitDetect += BLPackage_PitDetect;
            CPPackage.PitDetect += BLPackage_PitDetect;
            CSCPackage.PitDetect += BLPackage_PitDetect;

            RepartitionCheckBx.IsEnabled = false;
            BtnClearPit.Visibility = Visibility.Collapsed;

            // OdinEngine se inicializará cuando se detecte el puerto COM
            OdinEngine = null;
        }

        /// <summary>
        /// Inicializa OdinEngine con el puerto COM detectado
        /// </summary>
        private void InitializeOdinEngine(string portName)
        {
            if (OdinEngine == null && !string.IsNullOrEmpty(portName))
            {
                OdinEngine = new OdinEngine(portName);
                // Suscribirse al nuevo sistema de logging con LogLevel
                OdinEngine.OnLog += (text, level) =>
                {
                    Log?.Invoke(text, level);
                };
                OdinEngine.OnProgress += (current, total) =>
                {
                    ProgressChanged?.Invoke(current, total);
                };
            }
        }

        /// <summary>
        /// Convierte MsgType y bool isError a LogLevel para compatibilidad
        /// </summary>
        private LogLevel ConvertToLogLevel(MsgType messageType, bool isError)
        {
            if (isError)
            {
                return messageType == MsgType.Result ? LogLevel.Error : LogLevel.Warning;
            }
            return messageType == MsgType.Result ? LogLevel.Success : LogLevel.Info;
        }

        /// <summary>
        /// Detecta el puerto COM usando OdinEngine
        /// </summary>
        private string DetectComPort()
        {
            try
            {
                // Si OdinEngine ya está inicializado, verificar si el dispositivo está conectado
                if (OdinEngine != null)
                {
                    // Verificar conexión simple usando CheckDeviceConnected
                    if (OdinEngine.CheckDeviceConnected())
                    {
                        var port = OdinEngine.GetCurrentPort();
                        if (port != null && port.IsOpen)
                        {
                            return port.PortName;
                        }
                        // Si el puerto no está abierto pero el dispositivo está conectado, usar el nombre del puerto
                        // El nombre del puerto se almacena en OdinEngine durante la inicialización
                        return OdinEngine.GetCurrentPort()?.PortName;
                    }
                }
                
                // Usar detección automática de OdinEngine (detecta por VID/PID)
                var result = Task.Run(async () => await Odin_Flash.Class.OdinEngine.FindAndSetDownloadModeAsync()).Result;
                if (result.success && !string.IsNullOrEmpty(result.portName))
                {
                    return result.portName;
                }
                
                // Último fallback: buscar puertos COM disponibles
                return System.IO.Ports.SerialPort.GetPortNames().FirstOrDefault();
            }
            catch
            {
                try
                {
                    return System.IO.Ports.SerialPort.GetPortNames().FirstOrDefault();
                }
                catch
                {
                    return null;
                }
            }
        }


        public void ControlsManage(bool IsEnable)
        {
            BLPackage.IsEnabled = !IsEnable;
            APPackage.IsEnabled = !IsEnable;
            CPPackage.IsEnabled = !IsEnable;
            CSCPackage.IsEnabled = !IsEnable;
            AutoBoot.IsEnabled = !IsEnable;
            BootUpdate.IsEnabled = !IsEnable;
            EfsClear.IsEnabled = !IsEnable;
            TxtBxPit.IsEnabled = !IsEnable;
            BtnClearPit.IsEnabled = !IsEnable;
            BtnChoosePit.IsEnabled = !IsEnable;
            BtnFlash.IsEnabled = !IsEnable;
            BtnReadPit.IsEnabled = !IsEnable;
        }

        // Métodos Odin_Log y Odin_ProgressChanged eliminados - ya no se usan
        // Los eventos ahora vienen directamente de OdinEngine
        private async void BtnChoosePit_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                DefaultExt = ".tar",
                Filter = "Csc Or Pit file|*.tar;*.md5;*.pit"
            };
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                TxtBxPit.Clear();
                string filename = dlg.FileName;
                var ext = System.IO.Path.GetExtension(filename);
                if (ext == ".pit")
                {
                    BLPackage_PitDetect(System.IO.Path.GetFileName(filename), filename);
                }
                else
                {
                    // Usar TarProcessor para procesar archivos .tar
                    var item = await Odin_Flash.Class.TarProcessor.GetTarFileListAsync(filename);
                    if (item.Count > 0)
                    {
                        foreach (var Tiem in item)
                        {
                            var Extension = System.IO.Path.GetExtension(Tiem.Filename);
                            var file = new FileFlash
                            {
                                Enable = true,
                                FileName = Tiem.Filename,
                                FilePath = filename
                            };

                            if (Extension == ".pit")
                            {
                                BLPackage_PitDetect(Tiem.Filename, filename);
                                return;
                            }
                        }
                    }

                }


            }
        }
        private void BtnClearPit_Click(object sender, RoutedEventArgs e)
        {
            TxtBxPit.Clear();
            RepartitionCheckBx.IsChecked = false;
        }
        private async void BLPackage_PitDetect(string pitname, string path)
        {
            RepartitionCheckBx.IsChecked = false;
            var extension = System.IO.Path.GetExtension(path);
            if (extension.ToLower() == ".pit")
            {
                // Usar PitTool para descomprimir PIT
                if (Odin_Flash.Class.PitTool.UNPACK_PIT(File.ReadAllBytes(path)))
                {
                    TxtBxPit.Text = path;
                    RepartitionCheckBx.IsChecked = true;
                }
                else
                {
                    Log?.Invoke($"File Curreped : {pitname}", ConvertToLogLevel(MsgType.Message, true));
                    return;
                }
            }
            else
            {
                // Extraer PIT desde archivo .tar
                var pit = await Odin_Flash.Class.TarProcessor.ExtractFileFromTarAsync(path, pitname);
                if (pit.Length == 0 || !Odin_Flash.Class.PitTool.UNPACK_PIT(pit))
                {
                    Log?.Invoke($"File Curreped : {pitname}", ConvertToLogLevel(MsgType.Message, true));
                    return;
                }
                else
                {
                    TxtBxPit.Text = path;
                    RepartitionCheckBx.IsChecked = true;
                }
            }
        }

        public async Task DoFlash(long Size, List<FileFlash> ListFlash)
        {
            // Paso 1: Detectar modo Download usando OdinEngine
            Log?.Invoke("Detecting Download Mode...", LogLevel.Info);
            var downloadModeResult = await Odin_Flash.Class.OdinEngine.FindAndSetDownloadModeAsync();
            if (!downloadModeResult.success || string.IsNullOrEmpty(downloadModeResult.portName))
            {
                Log?.Invoke("Device is not in Download Mode", LogLevel.Error);
                return;
            }

            // Paso 2: Inicializar OdinEngine con el puerto detectado
            if (OdinEngine == null)
            {
                InitializeOdinEngine(downloadModeResult.portName);
            }
            else if (OdinEngine.GetCurrentPort()?.PortName != downloadModeResult.portName)
            {
                // Si el puerto cambió, recrear OdinEngine
                OdinEngine?.Dispose();
                InitializeOdinEngine(downloadModeResult.portName);
            }

            if (OdinEngine == null)
            {
                Log?.Invoke("Failed to initialize OdinEngine", LogLevel.Error);
                return;
            }

            // Paso 3: Imprimir información del dispositivo
            await Odin_Flash.Class.OdinEngineWrappers.PrintInfoAsync(OdinEngine);
            Log?.Invoke("Checking Download Mode : ", LogLevel.Info);
            
            // Paso 4: Verificar modo Odin
            if (await Odin_Flash.Class.OdinEngineWrappers.IsOdinAsync(OdinEngine))
            {
                Log?.Invoke("ODIN", LogLevel.Success);
                Log?.Invoke($"Initializing Device : ", LogLevel.Info);
                
                // Paso 5: Inicializar comunicación LOKE
                if (await Odin_Flash.Class.OdinEngineWrappers.LOKE_InitializeAsync(OdinEngine, Size))
                {
                    Log?.Invoke("Initialized", LogLevel.Success);
                    
                    // Buscar PIT automáticamente en los archivos tar según documentación
                    var findPit = ListFlash.Find(x => x.FileName.ToLower().EndsWith(".pit"));
                    if (findPit != null)
                    {
                        Log?.Invoke("Pit Found on your tar package", LogLevel.Info);
                        var res = MessageBox.Show("Pit Finded on your tar package, you want to repartition?", "Repartition", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (res == MessageBoxResult.Yes)
                        {
                            TxtBxPit.Text = findPit.FilePath;
                            RepartitionCheckBx.IsChecked = true;
                            Log?.Invoke("Repartition Device : ", LogLevel.Info);
                            var Repartition = await Odin_Flash.Class.OdinEngineWrappers.Write_PitAsync(OdinEngine, findPit.FilePath);
                            if (Repartition.status)
                            {
                                Log?.Invoke("Ok", LogLevel.Success);
                            }
                            else
                            {
                                Log?.Invoke(Repartition.error, LogLevel.Error);
                                return;
                            }
                        }
                    }
                    // Si el usuario seleccionó PIT manualmente (checkbox marcado)
                    else if (!string.IsNullOrEmpty(TxtBxPit.Text) && RepartitionCheckBx.IsChecked == true)
                    {
                        Log?.Invoke("Repartition Device : ", LogLevel.Info);
                        var Repartition = await Odin_Flash.Class.OdinEngineWrappers.Write_PitAsync(OdinEngine, TxtBxPit.Text);
                        if (Repartition.status)
                        {
                            Log?.Invoke("Ok", LogLevel.Success);
                        }
                        else
                        {
                            Log?.Invoke(Repartition.error, LogLevel.Error);
                            return;
                        }
                    }

                    Log?.Invoke("Reading Pit from device : ", LogLevel.Info);
                    var GetPit = await Odin_Flash.Class.OdinEngineWrappers.Read_PitAsync(OdinEngine);
                    if (GetPit.Result)
                    {
                        Log?.Invoke("Ok", LogLevel.Success);
                        var EfsClearInt = 0;
                        var BootUpdateInt = 0;
                        if (EfsClear.IsChecked == true)
                        {
                            EfsClearInt = 1;
                        }
                        if (BootUpdate.IsChecked == true)
                        {
                            BootUpdateInt = 1;
                        }

                        // Flashear archivos uno por uno con delays apropiados según el tamaño
                        // Migración: Ahora usamos OdinEngine para todos los archivos
                        bool allFilesFlashed = true;
                        int totalFiles = ListFlash.Count(f => f.Enable);
                        int currentFile = 0;
                        
                        foreach (var file in ListFlash)
                        {
                            if (!file.Enable)
                                continue;

                            currentFile++;
                            var fileSizeGB = file.RawSize / (1024.0 * 1024.0 * 1024.0);
                            
                            // Determinar el tipo de archivo y delay necesario
                            string fileNameLower = file.FileName.ToLower();
                            bool isVeryLargeFile = fileNameLower.Contains("super") || file.RawSize > 5368709120; // > 5GB
                            bool isLargeFile = fileNameLower.Contains("userdata") || 
                                             fileNameLower.Contains("system") || 
                                             (file.RawSize > 1073741824 && !isVeryLargeFile); // > 1GB pero < 5GB
                            
                            Log?.Invoke($"Flashing {file.FileName} ({currentFile}/{totalFiles}) [{fileSizeGB:F2} GB] : ", LogLevel.Info);
                            
                            bool success;
                            
                            // Usar OdinEngine para todos los archivos (migración completa)
                            if (OdinEngine != null)
                            {
                                // Verificar si el archivo existe
                                if (!File.Exists(file.FilePath))
                                {
                                    Log?.Invoke($"Archivo no encontrado: {file.FilePath}", LogLevel.Error);
                                    allFilesFlashed = false;
                                    continue;
                                }

                                // Usar SendFileWithLokeProtocol para enviar el archivo
                                success = await OdinEngine.SendFileWithLokeProtocol(file.FilePath, file.RawSize);
                            }
                            else
                            {
                                Log?.Invoke("OdinEngine no disponible", LogLevel.Error);
                                success = false;
                            }
                            
                            if (success)
                            {
                                Log?.Invoke("Ok", LogLevel.Success);
                                
                                // Calcular delay basado en el tamaño real del archivo
                                // Para archivos grandes, necesitamos más tiempo para que el dispositivo
                                // termine de escribir completamente antes del siguiente archivo
                                int delayMs = 500; // Default para archivos pequeños
                                
                                if (isVeryLargeFile)
                                {
                                    // Para archivos muy grandes (>5GB), calcular delay basado en tamaño
                                    // Aproximadamente 5-6 segundos por GB, mínimo 30 segundos
                                    delayMs = Math.Max(30000, (int)(fileSizeGB * 6000)); // 6 segundos por GB, mínimo 30s
                                    Log?.Invoke($"Waiting for very large file to complete write ({delayMs/1000}s)...", LogLevel.Info);
                                }
                                else if (isLargeFile)
                                {
                                    // Para archivos grandes (1-5GB), calcular delay basado en tamaño
                                    // Aproximadamente 8-10 segundos por GB, mínimo 15 segundos
                                    delayMs = Math.Max(15000, (int)(fileSizeGB * 10000)); // 10 segundos por GB, mínimo 15s
                                    Log?.Invoke($"Waiting for large file to complete write ({delayMs/1000}s)...", LogLevel.Info);
                                }
                                else if (file.RawSize > 104857600) // > 100MB
                                {
                                    delayMs = 3000; // 3 segundos para archivos medianos
                                }
                                else
                                {
                                    delayMs = 1000; // 1 segundo para archivos pequeños (aumentado de 500ms)
                                }
                                
                                await Task.Delay(delayMs);
                                
                                // Respiro y Limpieza después de archivos grandes (Ref: 00438342)
                                // Evita ERROR_IO_PENDING (0x3e5) cuando el puerto está bloqueado escribiendo a UFS/eMMC
                                if (isVeryLargeFile || isLargeFile)
                                {
                                    Log?.Invoke("Clearing port buffers after large file...", LogLevel.Info);
                                    try
                                    {
                                        // Usar OdinEngine para limpiar el puerto si está disponible
                                        // Limpiar buffers antes del siguiente archivo
                                        if (OdinEngine != null)
                                        {
                                            // Usar método async
                                            bool portReady = await OdinEngine.ClearPortAfterLargeFileAsync();
                                            if (portReady)
                                            {
                                                Log?.Invoke("Port cleared and ready for next file", LogLevel.Success);
                                            }
                                            else
                                            {
                                                Log?.Invoke("Warning: Port cleanup had issues, continuing anyway...", LogLevel.Warning);
                                            }
                                        }
                                        else
                                        {
                                            // Si no hay OdinEngine, hacer un delay adicional para archivos muy grandes
                                            if (isVeryLargeFile)
                                            {
                                                Log?.Invoke("Additional wait for device buffer to clear...", LogLevel.Info);
                                                await Task.Delay(2000); // 2 segundos adicionales para controladores MICRON/UFS
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log?.Invoke($"Error during port cleanup: {ex.Message}", LogLevel.Error);
                                        // Continuar de todas formas - el delay ya se aplicó
                                    }
                                }
                            }
                            else
                            {
                                Log?.Invoke("Failed", LogLevel.Error);
                                
                                // AQUÍ ESTÁ EL TRUCO DE ODIN (Ref: 00438342):
                                // Intentar recuperar el puerto usando funciones nativas de Windows
                                Log?.Invoke("Error detectado. Intentando recuperar puerto...", LogLevel.Info);
                                
                                try
                                {
                                    // Intentar recuperar el puerto
                                    var port = OdinEngine?.GetCurrentPort();
                                    if (port != null && port.IsOpen)
                                    {
                                        // Tenemos acceso directo al puerto - usar Win32Comm
                                        Win32Comm.ResetPort(port);
                                        Log?.Invoke("Puerto recuperado usando Win32Comm", LogLevel.Success);
                                    }
                                    else if (OdinEngine != null)
                                    {
                                        // Intentar recuperar usando OdinEngine (método async)
                                        bool recovered = await OdinEngine.RecoverPortAfterErrorAsync();
                                        if (recovered)
                                        {
                                            Log?.Invoke("Puerto recuperado y protocolo LOKE re-sincronizado", LogLevel.Success);
                                        }
                                        else
                                        {
                                            Log?.Invoke("Esperando estabilización del puerto...", LogLevel.Info);
                                            await Task.Delay(2000);
                                        }
                                    }
                                    else
                                    {
                                        // Si no hay OdinEngine, solo esperar
                                        Log?.Invoke("Esperando estabilización del puerto...", LogLevel.Info);
                                        await Task.Delay(2000);
                                    }
                                }
                                catch (Exception recoveryEx)
                                {
                                    Log?.Invoke($"Error durante recuperación: {recoveryEx.Message}", LogLevel.Error);
                                    // Continuar de todas formas
                                    await Task.Delay(2000);
                                }
                                
                                // Después de intentar recuperar, decidir si continuar o detener
                                // Para archivos grandes, es más probable que sea un error temporal
                                if (isVeryLargeFile || isLargeFile)
                                {
                                    Log?.Invoke("Archivo grande falló. Continuando con siguiente archivo...", LogLevel.Warning);
                                    // Continuar con el siguiente archivo en lugar de detener todo
                                }
                                else
                                {
                                    allFilesFlashed = false;
                                    break; // Para archivos pequeños, detener el proceso
                                }
                            }
                        }

                        if (allFilesFlashed)
                        {
                            // Espera final adicional
                            await Task.Delay(1000);
                            
                            if (AutoBoot.IsChecked == true)
                            {
                                Log?.Invoke("Rebooting Device To Normal Mode : ", LogLevel.Info);
                                if (OdinEngine != null && await Odin_Flash.Class.OdinEngineWrappers.PDAToNormalAsync(OdinEngine))
                                {
                                    Log?.Invoke("Ok", LogLevel.Success);
                                }
                                else
                                {
                                    Log?.Invoke("Failed", LogLevel.Error);
                                }
                            }
                            else
                            {
                                Log?.Invoke("Auto Reboot Disabled Try Manual", LogLevel.Info);
                            }
                        }
                        else
                        {
                            Log?.Invoke("Flash Failed - Some batches could not be flashed", LogLevel.Error);
                        }
                    }
                    else
                    {
                        Log?.Invoke("Failed to read PIT", LogLevel.Error);
                    }
                }
                else
                {
                    Log?.Invoke("Failed to Initialize", LogLevel.Error);
                }
            }
            else
            {
                Log?.Invoke("Device is not in ODIN mode", LogLevel.Error);
            }
        }

        private async void BtnFlash_Click(object sender, RoutedEventArgs e)
        {
            try
            {
               
                IsRunning?.Invoke(true, "Flash");
                var ListFlash = new List<FileFlash>();
                ListFlash.AddRange(BLPackage.Files);
                ListFlash.AddRange(APPackage.Files);
                ListFlash.AddRange(CPPackage.Files);
                ListFlash.AddRange(CSCPackage.Files);
                if (ListFlash.Count > 0)
                {
                    Log?.Invoke("Calculated Size : ", LogLevel.Info);
                    var Size = 0L;
                    foreach (var item in ListFlash)
                    {
                        if (item.Enable)
                        {
                            Size += item.RawSize;
                        }
                    }
                    if (Size > 0)
                    {
                        Log?.Invoke(Util.Util.GetBytesReadable(Size), LogLevel.Success);
                        await DoFlash(Size, ListFlash);
                    }
                    else
                    {
                        Log?.Invoke("Failed", LogLevel.Error);
                    }
                }
                else if (!string.IsNullOrEmpty(TxtBxPit.Text) && RepartitionCheckBx.IsChecked == true)
                {
                    await DoFlash(0, ListFlash);
                }
                else
                {
                    Log?.Invoke("Please Select Firmware Package and try again", LogLevel.Info);
                }

            }
            catch (Exception ee)
            {
                Log?.Invoke($"System Error : ", LogLevel.Info);
                Log?.Invoke(ee.Message, LogLevel.Error);

            }
            finally
            {
                IsRunning?.Invoke(false, "Flash");
            }
        }

        private void TxtBxPit_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtBxPit.Text))
            {
                RepartitionCheckBx.IsEnabled = false;
                BtnClearPit.Visibility = Visibility.Collapsed;
            }
            else
            {
                RepartitionCheckBx.IsEnabled = true;
                BtnClearPit.Visibility = Visibility.Visible;
            }

        }

        private async void BtnReadPit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IsRunning?.Invoke(true, "ReadPit");
                
                Log?.Invoke("Detecting Download Mode...", LogLevel.Info);
                var downloadModeResult = await Odin_Flash.Class.OdinEngine.FindAndSetDownloadModeAsync();
                if (!downloadModeResult.success || string.IsNullOrEmpty(downloadModeResult.portName))
                {
                    Log?.Invoke("Device is not in Download Mode", LogLevel.Error);
                    return;
                }

                // Inicializar OdinEngine
                if (OdinEngine == null)
                {
                    InitializeOdinEngine(downloadModeResult.portName);
                }
                else if (OdinEngine.GetCurrentPort()?.PortName != downloadModeResult.portName)
                {
                    OdinEngine?.Dispose();
                    InitializeOdinEngine(downloadModeResult.portName);
                }

                if (OdinEngine == null)
                {
                    Log?.Invoke("Failed to initialize OdinEngine", LogLevel.Error);
                    return;
                }

                await Odin_Flash.Class.OdinEngineWrappers.PrintInfoAsync(OdinEngine);
                Log?.Invoke("Checking Download Mode : ", LogLevel.Info);
                
                if (await Odin_Flash.Class.OdinEngineWrappers.IsOdinAsync(OdinEngine))
                {
                    Log?.Invoke("ODIN", LogLevel.Success);
                    Log?.Invoke("Initializing Device : ", LogLevel.Info);
                    if (await Odin_Flash.Class.OdinEngineWrappers.LOKE_InitializeAsync(OdinEngine, 0))
                    {
                        Log?.Invoke("Initialized", LogLevel.Success);
                        Log?.Invoke("Reading Pit : ", LogLevel.Info);
                        var GetPit = await Odin_Flash.Class.OdinEngineWrappers.Read_PitAsync(OdinEngine);
                        if (GetPit.Result)
                        {
                            Log?.Invoke("Ok", LogLevel.Success);
                            Log?.Invoke($"SavedPath : ", LogLevel.Info);
                            var fpath = $"{Util.Util.MyPath}\\backup\\samsung\\pit\\{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")}.pit";
                            Util.Util.CreatFolder(System.IO.Path.GetDirectoryName(fpath));
                            System.IO.File.WriteAllBytes(fpath, GetPit.data);
                            Log?.Invoke(fpath, LogLevel.Success);
                            if (AutoBoot.IsChecked == true)
                            {
                                Log?.Invoke("Rebooting Device To Normal Mode : ", LogLevel.Info);
                                if (await Odin_Flash.Class.OdinEngineWrappers.PDAToNormalAsync(OdinEngine))
                                {
                                    Log?.Invoke("Ok", LogLevel.Success);
                                }
                                else
                                {
                                    Log?.Invoke("Failed", LogLevel.Error);
                                }
                            }
                            else
                            {
                                Log?.Invoke("Auto Reboot Disabled Try Manual", LogLevel.Info);
                            }
                        }
                        else
                        {
                            Log?.Invoke("Failed", LogLevel.Error);
                        }
                    }
                    else
                    {
                        Log?.Invoke("Failed", LogLevel.Error);
                    }
                }
                else
                {
                    Log?.Invoke("Failed", LogLevel.Error);
                }
            }
            catch (Exception ee)
            {
                Log?.Invoke($"System Error : ", LogLevel.Info);
                Log?.Invoke(ee.Message, LogLevel.Error);

            }
            finally
            {
                IsRunning?.Invoke(false, "ReadPit");
            }
        }
    }
}
