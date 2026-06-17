using Odin_Flash.Util;
using Microsoft.Win32;
using OdinFlash.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using static OdinFlash.Protocol.util.utils;

namespace Odin_Flash.Controls
{
    public partial class Flash : UserControl
    {
        public FlashField BLPackage = new FlashField("BL (bootloader) file package [tar,md5]");
        public FlashField APPackage = new FlashField("AP (PDA) file package [tar,md5]");
        public FlashField CPPackage = new FlashField("CP (Modem) file package [tar,md5]");
        public FlashField CSCPackage = new FlashField("CSC file package [tar,md5]");
        public Odin Odin = new Odin();

        public event ProgressChangedDelegate ProgressChanged;
        public event LogDelegate Log;
        public event IsRunningProcessDelegate IsRunning;
        public event FlashCompletedDelegate FlashCompleted;

        public Flash()
        {
            InitializeComponent();

            Features.Children.Add(BLPackage);
            Grid.SetRow(BLPackage, 0);
            Features.Children.Add(APPackage);
            Grid.SetRow(APPackage, 1);
            Features.Children.Add(CPPackage);
            Grid.SetRow(CPPackage, 2);
            Features.Children.Add(CSCPackage);
            Grid.SetRow(CSCPackage, 3);

            RepartitionCheckBx.IsEnabled = false;
            BtnClearPit.Visibility = Visibility.Collapsed;
            Odin.Log += Odin_Log;
            Odin.ProgressChanged += Odin_ProgressChanged;
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
            AllowDrop = !IsEnable;
        }

        private void Flash_DragOver(object sender, DragEventArgs e)
        {
            if (!BLPackage.IsEnabled)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            e.Effects = paths != null && paths.Any(p => CollectFirmwarePathsFromDropEntry(p).Any())
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Flash_Drop(object sender, DragEventArgs e)
        {
            if (!BLPackage.IsEnabled || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (dropped == null || dropped.Length == 0)
                return;

            var firmwarePaths = FirmwarePackageClassifier
                .SelectPreferredPathPerSlot(
                    dropped.SelectMany(CollectFirmwarePathsFromDropEntry))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (firmwarePaths.Count == 0)
            {
                Log?.Invoke("No firmware .tar / .tar.md5 found in drop", MsgType.Message, true);
                return;
            }

            var assigned = 0;
            var skipped = new List<string>();
            var slotTaken = new HashSet<FirmwarePackageSlot>();

            foreach (var path in firmwarePaths)
            {
                var slot = FirmwarePackageClassifier.Classify(path);
                if (slot == FirmwarePackageSlot.Unknown)
                {
                    skipped.Add(Path.GetFileName(path));
                    continue;
                }

                if (slotTaken.Contains(slot))
                {
                    Log?.Invoke($"Replacing {slot} package : ", MsgType.Message);
                    Log?.Invoke(Path.GetFileName(path), MsgType.Result);
                }

                var field = GetFieldForSlot(slot);
                if (field == null || !field.TryLoadTarPackage(path))
                {
                    skipped.Add(Path.GetFileName(path));
                    continue;
                }

                slotTaken.Add(slot);
                assigned++;
            }

            if (assigned == 0)
                Log?.Invoke("Could not assign dropped files to BL/AP/CP/CSC slots", MsgType.Message, true);
            else if (skipped.Count > 0)
                Log?.Invoke($"Skipped {skipped.Count} unrecognized file(s)", MsgType.Message);

            e.Handled = true;
        }

        private static IEnumerable<string> CollectFirmwarePathsFromDropEntry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                yield break;

            if (Directory.Exists(path))
            {
                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                        .Where(FirmwarePackageClassifier.IsFirmwareArchive)
                        .ToList();
                }
                catch
                {
                    yield break;
                }

                if (files.Count == 0)
                    yield break;

                // Una carpeta suelta (BL/AP/CP/CSC) con un solo TAR → ese archivo.
                if (files.Count == 1)
                {
                    yield return files[0];
                    yield break;
                }

                // Carpeta con varios TAR: un archivo por slot (CSC_ gana sobre HOME_CSC_).
                foreach (var file in FirmwarePackageClassifier.SelectPreferredPathPerSlot(files))
                    yield return file;

                yield break;
            }

            if (FirmwarePackageClassifier.IsFirmwareArchive(path))
                yield return path;
        }

        private FlashField GetFieldForSlot(FirmwarePackageSlot slot)
        {
            switch (slot)
            {
                case FirmwarePackageSlot.BL:
                    return BLPackage;
                case FirmwarePackageSlot.AP:
                    return APPackage;
                case FirmwarePackageSlot.CP:
                    return CPPackage;
                case FirmwarePackageSlot.CSC:
                    return CSCPackage;
                default:
                    return null;
            }
        }

        private void Odin_Log(string Text, MsgType Color, bool IsError = false, string navigateUri = null)
        {
            if (string.IsNullOrEmpty(navigateUri)
                && Color == MsgType.Result
                && !IsError
                && !string.IsNullOrEmpty(Odin.DeviceBuildNumber)
                && string.Equals(Text?.Trim(), Odin.DeviceBuildNumber, StringComparison.OrdinalIgnoreCase))
            {
                navigateUri = SamFwLinkBuilder.BuildFirmwareUrl(
                    Odin.DeviceModel, Odin.DeviceSalesCode, Odin.DeviceBuildNumber);
            }

            Log?.Invoke(Text, Color, IsError, navigateUri);
        }

        private void Odin_ProgressChanged(
            string filename,
            long partitionMax,
            long partitionValue,
            long writtenSize,
            long batchTotal,
            long batchWritten)
        {
            ProgressChanged?.Invoke(filename, partitionMax, partitionValue, writtenSize, batchTotal, batchWritten);
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
                var ext = Path.GetExtension(filename);
                if (ext == ".pit")
                {
                    BLPackage_PitDetect(Path.GetFileName(filename), filename);
                }
                else
                {
                    var item = Odin.tar.TarInformation(filename);
                    if (item.Count > 0)
                    {
                        foreach (var Tiem in item)
                        {
                            var Extension = Path.GetExtension(Tiem.Filename);
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
            var extension = Path.GetExtension(path);
            if (extension.ToLower() == ".pit")
            {
                if (Odin.PitTool.UNPACK_PIT(File.ReadAllBytes(path)))
                {
                    TxtBxPit.Text = path;
                }
                else
                {
                    Log?.Invoke($"File corrupted : {pitname}", MsgType.Message, true);
                    return;
                }
            }
            else
            {
                var pit = await Odin.tar.ExtractFileFromTar(path, pitname);
                if (pit.Length == 0 || !Odin.PitTool.UNPACK_PIT(pit))
                {
                    Log?.Invoke($"File corrupted : {pitname}", MsgType.Message, true);
                    return;
                }

                TxtBxPit.Text = path;
            }
        }

        /// <summary>Flujo: conectar → LOKE init (variante) → PIT → SetFlashTotal una vez → flash plan PIT.</summary>
        public async Task<bool> DoFlash(List<FileFlash> ListFlash)
        {
            var list = new List<OdinFlash.Protocol.structs.FileFlash>();
            foreach (var i in ListFlash)
            {
                list.Add(new OdinFlash.Protocol.structs.FileFlash
                {
                    Enable = i.Enable,
                    FileName = i.FileName,
                    FilePath = i.FilePath,
                    RawSize = i.RawSize
                });
            }

            if (!await Odin.FindAndSetDownloadMode())
            {
                Log?.Invoke("Download Mode Port : ", MsgType.Message);
                Log?.Invoke("Not found", MsgType.Result, true);
                return false;
            }

            await Odin.PrintInfo();
            LokePerformanceSettings.ApplyCapaRuntimeProfile(Odin.LastReportedCapa);
            Log?.Invoke("Checking Download Mode : ", MsgType.Message);
            if (!await Odin.IsOdin())
            {
                Log?.Invoke("Failed - LOKE handshake not detected", MsgType.Result, true);
                return false;
            }

            Log?.Invoke("ODIN", MsgType.Result);

            // Sesión LOKE sin total; SetFlashTotal una sola vez tras PIT (evita doble 0x64/0x02 en variantes 4/5).
            long phoneTotal = 0L;

            Log?.Invoke($"Initializing Device : ", MsgType.Message);
            if (!await Odin.LOKE_Initialize(0))
            {
                Log?.Invoke("Failed - LOKE initialize rejected", MsgType.Result, true);
                return false;
            }

            Log?.Invoke("Initialized", MsgType.Result);

            if (!string.IsNullOrEmpty(TxtBxPit.Text) && RepartitionCheckBx.IsChecked == true)
            {
                Log?.Invoke("Repartition Device : ", MsgType.Message);
                var Repartition = await Odin.Write_Pit(TxtBxPit.Text);
                if (Repartition.status)
                    Log?.Invoke("Ok", MsgType.Result);
                else
                {
                    Log?.Invoke(string.IsNullOrEmpty(Repartition.error) ? "Failed - PIT write rejected" : Repartition.error, MsgType.Result, true);
                    return false;
                }
            }

            Log?.Invoke("Reading Pit from device : ", MsgType.Message);
            var GetPit = await Odin.Read_Pit();
            if (!GetPit.Result)
            {
                Log?.Invoke(string.IsNullOrEmpty(GetPit.error) ? "Failed - invalid PIT response" : GetPit.error, MsgType.Result, true);
                return false;
            }

            Log?.Invoke("Ok", MsgType.Result);

            var flashPlan = Odin.BuildFlashPlan(list, GetPit.Pit);
            if (list.Count > 0 && flashPlan.Count == 0)
            {
                Log?.Invoke("No matching partitions found for selected firmware", MsgType.Result, true);
                return false;
            }

            if (list.Count > 0)
            {
                Log?.Invoke($"Flash images matched : {flashPlan.Count}", MsgType.Message);
                phoneTotal = Odin.CalculatePhoneProgressTotalBytes(list, GetPit.Pit);
                Odin.SetPhoneSessionTotalBytes(phoneTotal);
                Log?.Invoke("LOKE session total : ", MsgType.Message);
                Log?.Invoke(Util.Util.FormatCalculatedSizeGbOdin(phoneTotal), MsgType.Result);
                if (phoneTotal > 0 && !await Odin.InitializeFlashTotal(phoneTotal))
                {
                    Log?.Invoke("Failed - LOKE total size rejected", MsgType.Result, true);
                    return false;
                }
            }

            var EfsClearInt = 0;
            var BootUpdateInt = 0;
            if (EfsClear.IsChecked == true)
                EfsClearInt++;
            if (BootUpdate.IsChecked == true)
                BootUpdateInt++;

            if (list.Count == 0)
                return false;

            if (await Odin.FlashFirmware(list, GetPit.Pit, EfsClearInt, BootUpdateInt, true))
            {
                if (AutoBoot.IsChecked == true)
                {
                    Log?.Invoke("Rebooting Device To Normal Mode : ", MsgType.Message);
                    if (await Odin.PDAToNormal())
                    {
                        Log?.Invoke("Ok", MsgType.Result);
                    }
                    else
                        Log?.Invoke("Failed", MsgType.Result, true);
                }
                else
                    Log?.Invoke("Auto Reboot Disabled Try Manual", MsgType.Message);

                return true;
            }

            Log?.Invoke("Flash Failed - Some partitions were not written", MsgType.Result, true);
            return false;
        }

        public async void BtnFlash_Click(object sender, RoutedEventArgs e)
        {
            var flashStopwatch = Stopwatch.StartNew();
            var flashSucceeded = false;
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
                    var protoList = new List<OdinFlash.Protocol.structs.FileFlash>();
                    foreach (var i in ListFlash)
                    {
                        protoList.Add(new OdinFlash.Protocol.structs.FileFlash
                        {
                            Enable = i.Enable,
                            FileName = i.FileName,
                            FilePath = i.FilePath,
                            RawSize = i.RawSize
                        });
                    }

                    var phoneTotal = Odin.CalculatePackagePhoneTotalBytes(protoList);
                    Log?.Invoke("Calculated Size : ", MsgType.Message);
                    if (phoneTotal > 0)
                    {
                        Log?.Invoke(Util.Util.FormatCalculatedSizeGbOdin(phoneTotal), MsgType.Result);
                        ProgressChanged?.Invoke(string.Empty, 0, 0, 0, phoneTotal, 0);
                        flashSucceeded = await DoFlash(ListFlash);
                    }
                    else
                        Log?.Invoke("Failed", MsgType.Result, true);
                }
                else if (!string.IsNullOrEmpty(TxtBxPit.Text) && RepartitionCheckBx.IsChecked == true)
                {
                    flashSucceeded = await DoFlash(ListFlash);
                }
                else
                    Log?.Invoke("Please Select Firmware Package and try again", MsgType.Message);

                if (flashSucceeded)
                    FlashCompleted?.Invoke(flashStopwatch.Elapsed);
            }
            catch (Exception ee)
            {
                Log?.Invoke($"System Error : ", MsgType.Message);
                Log?.Invoke(ee.Message, MsgType.Result, true);
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

        public async void BtnReadPit_Click(object sender, RoutedEventArgs e)
        {
            var readPitStopwatch = Stopwatch.StartNew();
            var readPitSucceeded = false;
            try
            {
                if (!await Odin.FindAndSetDownloadMode())
                {
                    Log?.Invoke("Download Mode Port : ", MsgType.Message);
                    Log?.Invoke("Not found", MsgType.Result, true);
                    return;
                }

                IsRunning?.Invoke(true, "ReadPit");
                await Odin.PrintInfo();
                LokePerformanceSettings.ApplyCapaRuntimeProfile(Odin.LastReportedCapa);
                Log?.Invoke("Checking Download Mode : ", MsgType.Message);
                if (!await Odin.IsOdin())
                {
                    Log?.Invoke("Failed - LOKE handshake not detected", MsgType.Result, true);
                    return;
                }

                Log?.Invoke("ODIN", MsgType.Result);
                Log?.Invoke("Initializing Device : ", MsgType.Message);
                if (!await Odin.LOKE_Initialize(0))
                {
                    Log?.Invoke("Failed - LOKE initialize rejected", MsgType.Result, true);
                    return;
                }

                Log?.Invoke("Initialized", MsgType.Result);
                Log?.Invoke("Reading Pit : ", MsgType.Message);
                var GetPit = await Odin.Read_Pit();
                if (!GetPit.Result)
                {
                    Log?.Invoke(string.IsNullOrEmpty(GetPit.error) ? "Failed - invalid PIT response" : GetPit.error, MsgType.Result, true);
                    return;
                }

                Log?.Invoke("Ok", MsgType.Result);
                Log?.Invoke($"SavedPath : ", MsgType.Message);
                var fpath = $"{Util.Util.MyPath}\\backup\\samsung\\pit\\{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.pit";
                Util.Util.CreatFolder(Path.GetDirectoryName(fpath));
                File.WriteAllBytes(fpath, GetPit.data);
                Log?.Invoke(fpath, MsgType.Result);
                if (AutoBoot.IsChecked == true)
                {
                    Log?.Invoke("Rebooting Device To Normal Mode : ", MsgType.Message);
                    if (await Odin.PDAToNormal())
                        Log?.Invoke("Ok", MsgType.Result);
                    else
                        Log?.Invoke("Failed", MsgType.Result, true);
                }
                else
                    Log?.Invoke("Auto Reboot Disabled Try Manual", MsgType.Message);

                readPitSucceeded = true;
            }
            catch (Exception ee)
            {
                Log?.Invoke($"System Error : ", MsgType.Message);
                Log?.Invoke(ee.Message, MsgType.Result, true);
            }
            finally
            {
                if (readPitSucceeded)
                    FlashCompleted?.Invoke(readPitStopwatch.Elapsed);
                IsRunning?.Invoke(false, "ReadPit");
            }
        }
    }
}
