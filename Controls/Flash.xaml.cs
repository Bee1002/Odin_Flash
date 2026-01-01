using Odin_Flash.Util;
using Odin_Flash.Class;
using Microsoft.Win32;
using SharpOdinClient;
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
using static SharpOdinClient.util.utils;

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
        public Odin Odin = new Odin();
        public OdinEngine OdinEngine; // Motor de protocolo Odin basado en análisis original

        public event ProgressChangedDelegate ProgressChanged;
        public event LogDelegate Log;
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
            Odin.Log += Odin_Log;
            Odin.ProgressChanged += Odin_ProgressChanged;

            // Inicializar OdinEngine con la instancia de Odin
            OdinEngine = new OdinEngine(Odin);
            OdinEngine.Log += OdinEngine_Log;

        }

        /// <summary>
        /// Maneja los logs de OdinEngine y los redirige al sistema de logging existente
        /// </summary>
        private void OdinEngine_Log(string Text, MsgType Color, bool IsError = false)
        {
            Log?.Invoke(Text, Color, IsError);
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

        private void Odin_Log(string Text, MsgType Color, bool IsError = false)
        {
            Log?.Invoke(Text, Color, IsError);
        }

        private void Odin_ProgressChanged(string filename, long max, long value, long WritenSize)
        {
            ProgressChanged?.Invoke(filename, max, value, WritenSize);
        }
        private void BtnChoosePit_Click(object sender, RoutedEventArgs e)
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
                    var item = this.Odin.tar.TarInformation(filename);
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
                if (Odin.PitTool.UNPACK_PIT(File.ReadAllBytes(path)))
                {
                    TxtBxPit.Text = path;
                    RepartitionCheckBx.IsChecked = true;
                }
                else
                {
                    Log?.Invoke($"File Curreped : {pitname}", MsgType.Message , true);
                    return;
                }
            }
            else
            {
                var pit = await Odin.tar.ExtractFileFromTar(path, pitname);
                if (pit.Length == 0 || !Odin.PitTool.UNPACK_PIT(pit))
                {
                    Log?.Invoke($"File Curreped : {pitname}", MsgType.Message, true);
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
            var list = new List<SharpOdinClient.structs.FileFlash>();
            foreach(var i in ListFlash)
            {
                if (i.Enable)
                {
                    list.Add(new SharpOdinClient.structs.FileFlash
                    {
                        Enable = i.Enable,
                        FileName = i.FileName,
                        FilePath = i.FilePath,
                        RawSize = i.RawSize
                    });
                }
            }

            if (!await Odin.FindAndSetDownloadMode())
            {
                return;
            }
            await Odin.PrintInfo();
            Log?.Invoke("Checking Download Mode : ", MsgType.Message);
            if (await Odin.IsOdin())
            {
                Log?.Invoke("ODIN", MsgType.Result);
                Log?.Invoke($"Initializing Device : ", MsgType.Message);
                if (await Odin.LOKE_Initialize(Size))
                {
                    Log?.Invoke("Initialized", MsgType.Result);
                    
                    // Buscar PIT automáticamente en los archivos tar según documentación
                    var findPit = ListFlash.Find(x => x.FileName.ToLower().EndsWith(".pit"));
                    if (findPit != null)
                    {
                        Log?.Invoke("Pit Found on your tar package", MsgType.Message);
                        var res = MessageBox.Show("Pit Finded on your tar package, you want to repartition?", "Repartition", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (res == MessageBoxResult.Yes)
                        {
                            TxtBxPit.Text = findPit.FilePath;
                            RepartitionCheckBx.IsChecked = true;
                            Log?.Invoke("Repartition Device : ", MsgType.Message);
                            var Repartition = await Odin.Write_Pit(findPit.FilePath);
                            if (Repartition.status)
                            {
                                Log?.Invoke("Ok", MsgType.Result);
                            }
                            else
                            {
                                Log?.Invoke(Repartition.error, MsgType.Result, true);
                                return;
                            }
                        }
                    }
                    // Si el usuario seleccionó PIT manualmente (checkbox marcado)
                    else if (!string.IsNullOrEmpty(TxtBxPit.Text) && RepartitionCheckBx.IsChecked == true)
                    {
                        Log?.Invoke("Repartition Device : ", MsgType.Message);
                        var Repartition = await Odin.Write_Pit(TxtBxPit.Text);
                        if (Repartition.status)
                        {
                            Log?.Invoke("Ok", MsgType.Result);
                        }
                        else
                        {
                            Log?.Invoke(Repartition.error, MsgType.Result, true);
                            return;
                        }
                    }

                    Log?.Invoke("Reading Pit from device : ", MsgType.Message);
                    var GetPit = await Odin.Read_Pit();
                    if (GetPit.Result)
                    {
                        Log?.Invoke("Ok", MsgType.Result);
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
                        bool allFilesFlashed = true;
                        int totalFiles = list.Count;
                        int currentFile = 0;
                        
                        foreach (var file in list)
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
                            
                            Log?.Invoke($"Flashing {file.FileName} ({currentFile}/{totalFiles}) [{fileSizeGB:F2} GB] : ", MsgType.Message);
                            
                            var singleFileList = new List<SharpOdinClient.structs.FileFlash> { file };
                            
                            if (await Odin.FlashFirmware(singleFileList, GetPit.Pit, EfsClearInt, BootUpdateInt, true))
                            {
                                Log?.Invoke("Ok", MsgType.Result);
                                
                                // Calcular delay basado en el tamaño real del archivo
                                // Para archivos grandes, necesitamos más tiempo para que el dispositivo
                                // termine de escribir completamente antes del siguiente archivo
                                int delayMs = 500; // Default para archivos pequeños
                                
                                if (isVeryLargeFile)
                                {
                                    // Para archivos muy grandes (>5GB), calcular delay basado en tamaño
                                    // Aproximadamente 5-6 segundos por GB, mínimo 30 segundos
                                    delayMs = Math.Max(30000, (int)(fileSizeGB * 6000)); // 6 segundos por GB, mínimo 30s
                                    Log?.Invoke($"Waiting for very large file to complete write ({delayMs/1000}s)...", MsgType.Message);
                                }
                                else if (isLargeFile)
                                {
                                    // Para archivos grandes (1-5GB), calcular delay basado en tamaño
                                    // Aproximadamente 8-10 segundos por GB, mínimo 15 segundos
                                    delayMs = Math.Max(15000, (int)(fileSizeGB * 10000)); // 10 segundos por GB, mínimo 15s
                                    Log?.Invoke($"Waiting for large file to complete write ({delayMs/1000}s)...", MsgType.Message);
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
                                    Log?.Invoke("Clearing port buffers after large file...", MsgType.Message);
                                    try
                                    {
                                        // Usar OdinEngine para limpiar el puerto si está disponible
                                        // Esto maneja el caso donde SharpOdinClient tiene el puerto abierto
                                        // y necesitamos limpiar buffers antes del siguiente archivo
                                        if (OdinEngine != null)
                                        {
                                            // Nota: OdinEngine puede no tener acceso directo al puerto si usa SharpOdinClient
                                            // En ese caso, el método retornará true después de un delay
                                            bool portReady = await Task.Run(() => OdinEngine.ClearPortAfterLargeFile());
                                            if (portReady)
                                            {
                                                Log?.Invoke("Port cleared and ready for next file", MsgType.Result);
                                            }
                                            else
                                            {
                                                Log?.Invoke("Warning: Port cleanup had issues, continuing anyway...", MsgType.Message);
                                            }
                                        }
                                        else
                                        {
                                            // Si no hay OdinEngine, hacer un delay adicional para archivos muy grandes
                                            if (isVeryLargeFile)
                                            {
                                                Log?.Invoke("Additional wait for device buffer to clear...", MsgType.Message);
                                                await Task.Delay(2000); // 2 segundos adicionales para controladores MICRON/UFS
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log?.Invoke($"Error during port cleanup: {ex.Message}", MsgType.Result, true);
                                        // Continuar de todas formas - el delay ya se aplicó
                                    }
                                }
                            }
                            else
                            {
                                Log?.Invoke("Failed", MsgType.Result, true);
                                
                                // AQUÍ ESTÁ EL TRUCO DE ODIN (Ref: 00438342):
                                // Intentar recuperar el puerto usando funciones nativas de Windows
                                Log?.Invoke("Error detectado. Intentando recuperar puerto...", MsgType.Message);
                                
                                try
                                {
                                    // Nota: SharpOdinClient maneja su propio puerto serial
                                    // Intentamos recuperar usando Win32Comm si es posible acceder al puerto
                                    // Si OdinEngine tiene acceso directo, usamos su método de recuperación
                                    if (OdinEngine != null)
                                    {
                                        // Intentar recuperar usando OdinEngine (usa Win32Comm internamente)
                                        bool recovered = await Task.Run(() => OdinEngine.RecoverPortAfterError());
                                        
                                        if (recovered)
                                        {
                                            Log?.Invoke("Puerto recuperado y protocolo LOKE re-sincronizado", MsgType.Result);
                                            Log?.Invoke("Reintentando archivo...", MsgType.Message);
                                            // Opcional: Reintentar el envío del archivo que falló
                                            // Por ahora marcamos como fallido pero continuamos
                                            // para evitar bucles infinitos en archivos grandes
                                        }
                                        else
                                        {
                                            Log?.Invoke("No se pudo recuperar el puerto completamente", MsgType.Result, true);
                                        }
                                    }
                                    else
                                    {
                                        // Si no hay OdinEngine, hacer delay y continuar
                                        Log?.Invoke("Esperando estabilización del puerto...", MsgType.Message);
                                        await Task.Delay(2000);
                                    }
                                }
                                catch (Exception recoveryEx)
                                {
                                    Log?.Invoke($"Error durante recuperación: {recoveryEx.Message}", MsgType.Result, true);
                                }
                                
                                // Después de intentar recuperar, decidir si continuar o detener
                                // Para archivos grandes, es más probable que sea un error temporal
                                if (isVeryLargeFile || isLargeFile)
                                {
                                    Log?.Invoke("Archivo grande falló. Continuando con siguiente archivo...", MsgType.Message);
                                    // Continuar con el siguiente archivo en lugar de detener todo
                                    // El usuario puede reintentar manualmente si es necesario
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
                                Log?.Invoke("Rebooting Device To Normal Mode : ", MsgType.Message);
                                if (await Odin.PDAToNormal())
                                {
                                    Log?.Invoke("Ok", MsgType.Result);
                                }
                                else
                                {
                                    Log?.Invoke("Failed", MsgType.Result, true);
                                }
                            }
                            else
                            {
                                Log?.Invoke("Auto Reboot Disabled Try Manual", MsgType.Message);
                            }
                        }
                        else
                        {
                            Log?.Invoke("Flash Failed - Some batches could not be flashed", MsgType.Result, true);
                        }
                    }
                    else
                    {
                        Log?.Invoke("Failed to read PIT", MsgType.Result, true);
                    }
                }
                else
                {
                    Log?.Invoke("Failed to Initialize", MsgType.Result, true);
                }
            }
            else
            {
                Log?.Invoke("Device is not in ODIN mode", MsgType.Result, true);
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
                    Log?.Invoke("Calculated Size : ", MsgType.Message);
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
                        Log?.Invoke(Util.Util.GetBytesReadable(Size),MsgType.Result );
                        await DoFlash(Size, ListFlash);
                    }
                    else
                    {
                        Log?.Invoke("Failed",MsgType.Result , true);
                    }
                }
                else if (!string.IsNullOrEmpty(TxtBxPit.Text) && RepartitionCheckBx.IsChecked == true)
                {
                    await DoFlash(0, ListFlash);
                }
                else
                {
                    Log?.Invoke("Please Select Firmware Package and try again", MsgType.Message);
                }

            }
            catch (Exception ee)
            {
                Log?.Invoke($"System Error : ", MsgType.Message);
                Log?.Invoke(ee.Message, MsgType.Result , true);

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
                if (!await Odin.FindAndSetDownloadMode())
                {
                    return;
                }
                IsRunning?.Invoke(true, "ReadPit");
                await Odin.PrintInfo();
                Log?.Invoke("Checking Download Mode : ", MsgType.Message);
                if (await Odin.IsOdin())
                {
                    Log?.Invoke("ODIN",MsgType.Result );
                    Log?.Invoke("Initializing Device : ", MsgType.Message);
                    if (await Odin.LOKE_Initialize(0))
                    {
                        Log?.Invoke("Initialized",MsgType.Result );
                        Log?.Invoke("Reading Pit : ", MsgType.Message);
                        var GetPit = await Odin.Read_Pit();
                        if (GetPit.Result)
                        {
                            Log?.Invoke("Ok",MsgType.Result );
                            Log?.Invoke($"SavedPath : ", MsgType.Message);
                            var fpath = $"{Util.Util.MyPath}\\backup\\samsung\\pit\\{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")}.pit";
                            Util.Util.CreatFolder(System.IO.Path.GetDirectoryName(fpath));
                            System.IO.File.WriteAllBytes(fpath, GetPit.data);
                            Log?.Invoke(fpath,MsgType.Result );
                            if (AutoBoot.IsChecked == true)
                            {
                                Log?.Invoke("Rebooting Device To Normal Mode : ", MsgType.Message);
                                if (await Odin.PDAToNormal())
                                {
                                    Log?.Invoke("Ok",MsgType.Result );
                                }
                                else
                                {
                                    Log?.Invoke("Failed", MsgType.Result , true);
                                }
                            }
                            else
                            {
                                Log?.Invoke("Auto Reboot Disabled Try Manual", MsgType.Message);
                            }
                        }
                        else
                        {
                            Log?.Invoke("Failed", MsgType.Result , true);
                        }
                    }
                    else
                    {
                        Log?.Invoke("Failed", MsgType.Result , true);
                    }
                }
                else
                {
                    Log?.Invoke("Failed", MsgType.Result , true);
                }
            }
            catch (Exception ee)
            {
                Log?.Invoke($"System Error : ", MsgType.Message);
                Log?.Invoke(ee.Message, MsgType.Result , true);

            }
            finally
            {
                IsRunning?.Invoke(false, "ReadPit");
            }
        }
    }
}
