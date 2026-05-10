using K4os.Compression.LZ4.Streams;
using OdinProtocolAtack.Pit;
using OdinProtocolAtack.Port;
using OdinProtocolAtack.structs;
using OdinProtocolAtack.util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static OdinProtocolAtack.util.utils;

// Aki ocurre la magia de la escritura de las particiones

namespace OdinProtocolAtack
{
    public class Odin
    {
        /// <summary>Máx. bytes por trozo en NAND Write (cmd 102 / 0x66). 128 KiB evita timeouts en preloader/particiones pequeñas.</summary>
        public static int FlashChunkBytes { get; set; } = 131072;

        /// <summary>Milisegundos de espera tras cada escritura de trozo y antes de leer el ACK de 8 bytes (0 = desactivado).</summary>
        public static int FlashAckDelayMs { get; set; } = 0;

        /// <summary>Si es true, vacía el buffer de entrada tras el buffer reserve (102,2) y antes del primer byte de imagen.</summary>
        public static bool DiscardInBufferBeforeNandPayload { get; set; } = false;

        public Cmd cmd = new Cmd();
        public device Device = new device();
        public Tar tar = new Tar();
        public PITData PitTool = new PITData();

        public event LogDelegate Log;
        public event ProgressChangedDelegate ProgressChanged;

        /// <summary>
        /// Finding samsung devices download mode port 
        /// </summary>
        /// <returns>serialport information
        public async Task<ItypePort> FindDownloadModePort() => await PortComm.FindDownloadModePort();
        public async Task<bool> LOKE_Initialize(long totalFileSize) => await cmd.LOKE_Initialize(this.Device, totalFileSize);


        /// <summary>
        /// Localiza COM en modo download: primero WMI (como Freya/SharpOdin); si falla, escaneo handshake ODIN/LOKE.
        /// Tras abrir, aplica secuencia DTR/RTS + purge para alinear con el mismo orden que el escaneo y reducir timeouts espurios.
        /// </summary>
        public async Task<bool> FindAndSetDownloadMode()
        {
            utils.Stop = false;
            var dev = await FindDownloadModePort();
            if (string.IsNullOrEmpty(dev.COM))
            {
                var scanned = await OdinHandshakeProbe.DetectOdinPortAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(scanned))
                {
                    dev = new ItypePort
                    {
                        COM = scanned,
                        Name = "Samsung Odin (handshake scan)",
                        Type = PortType.COM_LPT
                    };
                }
            }

            if (string.IsNullOrEmpty(dev.COM))
                return false;

            if (!Device.RegisterPort(dev))
                return false;

            await Device.ApplySamsungUsbWakeSequenceAsync().ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// set download mode device manually
        /// </summary>
        /// <param name="device">serialport of your device</param>
        /// <returns>true if can be opened</returns>
        public bool SetDownloadMode(ItypePort device)
        {
            return Device.RegisterPort(device);
        }

        /// <summary>
        /// Get Device info
        /// </summary>
        /// <returns></returns>
        public async Task<Dictionary<string, string>> DVIF()
        {
            var Info = new Dictionary<string, string>();
            try
            {
                var DV =new byte[] { 0x44,0x56,0x49,0x46 };

                await Device.WritePort(DV, DV.Length);
                await Task.Delay(400);
                var Read = await Device.ReadExistingAsync();
                if (!string.IsNullOrEmpty(Read))
                {
                    var array = Regex.Split(Read, ";");
                    foreach (var item in array)
                    {
                        var KeyValue = Regex.Split(item.Replace("#", null).Replace("@", null), "=");
                        if (string.IsNullOrEmpty(KeyValue[0]) || string.IsNullOrEmpty(KeyValue[1]))
                        {
                            continue;
                        }
                        Info.Add(KeyValue[0], KeyValue[1]);
                    }
                }
            }
            catch
            {
            }
            return Info;
        }

        /// <summary>
        /// GetDeviceInfo and print information
        /// </summary>
        /// <returns></returns>
        public async Task PrintInfo()
        {
            var info = await DVIF();
            foreach (var item in info)
            {
                switch (item.Key.ToLower())
                {
                    case "capa":
                        {
                            Log?.Invoke("Capa Number: ", MsgType.Message);
                            Log?.Invoke(item.Value , MsgType.Result);
                            break;
                        }
                    case "product":
                        {
                            Log?.Invoke("Product Id: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "model":
                        {
                            Log?.Invoke("Model Number: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "fwver":
                        {
                            Log?.Invoke("Firmware Version: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "vendor":
                        {
                            Log?.Invoke("vendor: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "sales":
                        {
                            Log?.Invoke("Sales Code: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "ver":
                        {
                            Log?.Invoke("Build Number: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "did":
                        {
                            Log?.Invoke("did Number: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "un":
                        {
                            Log?.Invoke("Unique Id: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "tmu_temp":
                        {
                            Log?.Invoke("Tmu Number: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                    case "prov":
                        {
                            Log?.Invoke("Provision: ", MsgType.Message);
                            Log?.Invoke(item.Value, MsgType.Result);
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// Check device is Odin mode
        /// </summary>
        /// <returns></returns>
        public async Task<bool> IsOdin()
        {
            byte[] array = new byte[] { 0x4F,0x44, 0x49, 0x4E };
            await Device.WritePort(array, 4);
            await Task.Delay(device.OdinMagicReplyDelayMs);
            string text = await Device.ReadExistingAsync();
            return !string.IsNullOrEmpty(text) && text.Contains("LOKE");
        }

        /// <summary>
        /// stop all operation
        /// </summary>
        public void StopOperations()
        {
            utils.Stop = true;
        }


        public async Task<bool> PDAToNormal()
        {
            try
            {
                SamsungLokeCommand cmd2 = new SamsungLokeCommand(103);
                await cmd.LOKE_SendCMD(this.Device,cmd2);
                cmd2.SeqCmd = 1;
                await cmd.LOKE_SendCMD(this.Device, cmd2);
            }
            catch { }
            var watch = new Stopwatch();
            try
            {
                watch.Start();
                do
                {
                    if (!Device.Port.IsOpen)
                    {
                        return true;
                    }
                } while (watch.ElapsedMilliseconds < 60000);
            }
            finally
            {
                watch.Stop();
            }
            return !Device.Port.IsOpen;
        }

        public async Task<Result> Write_Pit(byte[] pit)
        {
            var Result = new Result { error = "Failed RePartition" };
            try
            {
                SamsungLokeCommand command = new SamsungLokeCommand(101);
                if (await cmd.LOKE_SendCMD(Device, command))
                {
                    command = new SamsungLokeCommand(101, 2, pit.Length);
                    if (await cmd.LOKE_SendCMD(Device, command))
                    {
                        await Device.WritePort(pit, pit.Length);
                        cmd.BufferRead = await Device.ReadPort(8);
                        cmd.ValidateResponse(cmd.BufferRead, "Write PIT payload");
                        command = new SamsungLokeCommand(101, 3, pit.Length);
                        await cmd.LOKE_SendCMD(Device, command);
                        Result.status = true;

                    }
                }
            }
            catch (Exception ex)
            {
                Result.error = ex.Message;
            }

            return Result;
        }
       
        private string NormalizeFlashName(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                return string.Empty;

            filename = filename.Replace('\\', '/');
            var slash = filename.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < filename.Length)
                filename = filename.Substring(slash + 1);

            if (filename.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
                filename = filename.Substring(0, filename.Length - 4);

            return filename.Trim();
        }

        private bool IsFlashableFile(FileFlash file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.FileName))
                return false;

            var filename = file.FileName.Replace('\\', '/');
            if (filename.EndsWith("/", StringComparison.Ordinal))
                return false;
            if (filename.StartsWith("meta-data/", StringComparison.OrdinalIgnoreCase))
                return false;

            var extension = Path.GetExtension(filename);
            return extension.Equals(".lz4", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".img", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mbn", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".elf", StringComparison.OrdinalIgnoreCase);
        }

        private FileFlash FoundItem(List<FileFlash> files, TPIT_Entry partition)
        {
            foreach (var item in files)
            {
                if (!IsFlashableFile(item))
                    continue;

                var filename = NormalizeFlashName(item.FileName);
                var pitName = NormalizeFlashName(partition.MflashFilename);
                if (string.Equals(filename, pitName, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            return null;
        }
       
        private FileFlash FoundItem(FileFlash files, TPIT_Entry partition)
        {
            var filename = NormalizeFlashName(files.FileName);
            var pitName = NormalizeFlashName(partition.MflashFilename);
            if (string.Equals(filename, pitName, StringComparison.OrdinalIgnoreCase))
            {
                return files;
            }
            return null;
        }

        public long Calculate(int sessionLen)
        {
            return (sessionLen - 1L >> 17) + 1L << 17;
        }

        private async Task ReadExactlyFromStream(Stream inputStream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = await inputStream.ReadAsync(buffer, total, count - total);
                if (read <= 0)
                    throw new EndOfStreamException($"Cannot read input stream. Expected {count} bytes, received {total}.");
                total += read;
            }
        }
     
        private async Task<bool> SendAsync(TPIT_Entry entry, Stream inputStream,
            int sessionLength, bool isLast, Action<long> addProgressAction, int EfsClear = 0, int BootUpdate = 0)
        {
            SamsungLokeCommand command = new SamsungLokeCommand(102);
            await cmd.LOKE_SendCMD(Device,command);
            command = new SamsungLokeCommand(102, 2, Calculate(sessionLength));
            await cmd.LOKE_SendCMD(Device, command);
            if (DiscardInBufferBeforeNandPayload)
            {
                await Device.DiscardInBufferAsync();
            }
            int sent = 0;
            int chunkCap = FlashChunkBytes;
            if (chunkCap < 131072)
                chunkCap = 131072;
            if (chunkCap > 1048576)
                chunkCap = 1048576;
            byte[] flashBuffer = new byte[chunkCap];
            while (sent < sessionLength)
            {
                Array.Clear(flashBuffer, 0, flashBuffer.Length);
                int toRead = Math.Min(flashBuffer.Length, sessionLength - sent);
                await ReadExactlyFromStream(inputStream, flashBuffer, toRead);
                int writeLength = (int)Math.Min(flashBuffer.Length, Calculate(toRead));
                await Device.WritePort(flashBuffer, writeLength);
                if (FlashAckDelayMs > 0)
                    await Task.Delay(FlashAckDelayMs);
                cmd.ValidateResponse(await Device.ReadPort(8), $"NAND data ACK {entry.MflashFilename} offset {sent}");
                sent += toRead;
                addProgressAction(toRead);
            }
            command = new SamsungLokeCommand(102, 3, entry.MbinaryType, sessionLength);
            //await LOKE_SendCMD(command);
            if (entry.MbinaryType == 1L)
            {
                SamsungLokeCommand samsungLokeCommand = command.Clone();
                samsungLokeCommand.Identifier = (isLast ? 1 : 0);
                samsungLokeCommand.SessionEnd = (int)entry.MdeviceType;
                samsungLokeCommand.EfsClear = (int)entry.Midentifier;
                command = samsungLokeCommand;
            }
            else
            {
                SamsungLokeCommand samsungLokeCommand2 = command.Clone();
                samsungLokeCommand2.DeviceId = (int)entry.MdeviceType;
                samsungLokeCommand2.Identifier = (int)entry.Midentifier;
                samsungLokeCommand2.SessionEnd = (isLast ? 1 : 0);
                command = samsungLokeCommand2;
            }
            command.BootUpdate = BootUpdate;
            return await cmd.LOKE_SendCMD(Device, command);

        }

        public async Task<bool> WriteAsync(TPIT_Entry entry, long size, Stream inputStream, int EfsClear = 0, int BootUpdate = 0)
        {
            int maxSessionLen = ((entry.MdeviceType == 1L || entry.MdeviceType == 2L || entry.MdeviceType == 8L) ? 31457280 : 104857600);
            int sessionCount = (int)(size / maxSessionLen);
            if (size % maxSessionLen != 0L)
            {
                int num = sessionCount + 1;
                sessionCount = num;
            }
            ProgressChanged?.Invoke(entry.MflashFilename, size, 0L, 0);
            long currentProgress = 0L;
            long sent = 0L;
            int sessionIndex = 0;
            while (sessionIndex < sessionCount)
            {
                int sessionLen = (int)Math.Min(maxSessionLen, size - sent);
                bool isLast = sessionIndex == sessionCount - 1;
                Action<long> addProgressAction = delegate (long ff)
                {
                    currentProgress += ff;
                    ProgressChanged?.Invoke(entry.MflashFilename, size, currentProgress, currentProgress);
                };
                var write = await SendAsync(entry, inputStream, sessionLen, isLast, addProgressAction, EfsClear, BootUpdate);
                if (!write)
                {
                    return false;
                }
                sent += sessionLen;
                int num = sessionIndex + 1;
                sessionIndex = num;
            }
            return true;
        }

        private async Task<bool> FlashFromTar(string TarFile, long size,
           string inp_filename,
           TPIT_Entry pit,
           int EFSClear,
           int BootUpdate)
        {

            var TarFileData = new cListFileData();
            var temp1 = tar.TarInformation(TarFile);
            var foundTarItem = false;
            foreach (var item in temp1)
            {
                if (string.Equals(inp_filename, item.Filename, StringComparison.OrdinalIgnoreCase))
                {
                    TarFileData = item;
                    foundTarItem = true;
                    break;
                }
            }

            if (!foundTarItem)
                throw new FileNotFoundException($"Cannot find {inp_filename} inside {TarFile}.");

            string extension = Path.GetExtension(inp_filename);
            using (var reader = new BinaryReader(File.Open(TarFile, FileMode.Open)))
            {
                var j = TarFileData.FilePosStart;
                reader.BaseStream.Position = j;
                if (extension == ".lz4")
                {
                    using (var lz4 = LZ4Stream.Decode(reader.BaseStream))
                    {
                        return await WriteAsync(pit, size, lz4, EFSClear, BootUpdate);
                    }
                }
                else
                {
                    return await WriteAsync(pit, size, reader.BaseStream, EFSClear, BootUpdate);
                }
            }
        }
     
        public List<FileFlash> CurreptedPartitions(List<FileFlash> ready, List<FileFlash> Writed)
        {
            var ListCurrepted = new List<FileFlash>();
            foreach (var item in ready)
            {
                if (!item.Enable || !IsFlashableFile(item))
                {
                    continue;
                }
                var exst = false;
                foreach (var writen in Writed)
                {
                    if (item.FileName == writen.FileName)
                    {
                        exst = true;
                    }
                }
                if (!exst)
                {
                    ListCurrepted.Add(item);
                }
            }
            return ListCurrepted;
        }
        
        public async Task<bool> FlashFirmware(List<FileFlash> list, List<TPIT_Entry> pit, int EfsClear, int BootUpdate, bool Debug)
        {
            var WritenItem = new List<FileFlash>();
            foreach (var item in pit)
            {
                var FileItem = FoundItem(list, item);
                if (FileItem != null)
                {
                    if (!FileItem.Enable)
                    {
                        continue;
                    }
                    if (Debug)
                    {
                        Log?.Invoke($"Flashing {FileItem.FileName}: ",  MsgType.Message);
                    }
                    if (!await FlashFromTar(FileItem.FilePath, FileItem.RawSize, FileItem.FileName,
                        item, EfsClear, BootUpdate))
                    {
                        if (Debug)
                        {
                            Log?.Invoke($" : Failed", MsgType.Result , true);
                        }
                        return false;
                    }
                    else
                    {
                        WritenItem.Add(FileItem);
                        if (Debug)
                        {
                            Log?.Invoke($" : Ok", MsgType.Result);
                        }
                    }

                }
            }

            var GetCurrepted = CurreptedPartitions(list, WritenItem);
            if (GetCurrepted.Count > 0)
            {
                Log?.Invoke("File partition cannot find in your device partition : ", MsgType.Message);
                foreach (var currepted in GetCurrepted)
                {
                    Log?.Invoke(currepted.FileName + ", ", MsgType.Result , true);
                }
                return false;
            }

            return true;
        }


        private async Task<bool> FlashSingle(string Filepath, string inp_filename, TPIT_Entry pit,
          int EFSClear,
          int BootUpdate)
        {

            string extension = Path.GetExtension(inp_filename);
            ProgressChanged?.Invoke(inp_filename, 0, 0, 0);
            using (var reader = new BinaryReader(File.Open(Filepath, FileMode.Open)))
            {
                var j = 0L;
                if (extension == ".lz4")
                {
                    reader.BaseStream.Position = j;
                    using (var lz4 = LZ4Stream.Decode(reader.BaseStream))
                    {
                        return await WriteAsync(pit, lz4.Length, lz4, EFSClear, BootUpdate);
                    }
                }
                else
                {
                    return await WriteAsync(pit, reader.BaseStream.Length, reader.BaseStream, EFSClear, BootUpdate);
                }
            }
        }

        public async Task<bool> FlashSingleFile(FileFlash flash, List<TPIT_Entry> pit, int EfsClear, int BootUpdate, bool Debug)
        {
            var WritenItem = new FileFlash();

            foreach (var item in pit)
            {
                var FileItem = FoundItem(flash, item);
                if (FileItem != null)
                {
                    if (Debug)
                    {
                        Log?.Invoke($"Flashing {FileItem.FileName} : ",  MsgType.Message);
                    }
                    
                    if (!await FlashSingle(FileItem.FilePath, FileItem.FileName,
                        item, EfsClear, BootUpdate))
                    {
                        if (Debug)
                        {
                            Log?.Invoke("Failed", MsgType.Result , true);
                        }
                        return false;
                    }
                    else
                    {
                        WritenItem = FileItem;
                        if (Debug)
                        {
                            Log?.Invoke("Ok", MsgType.Result);
                        }
                        return true;
                    }
                }
            }
            if (string.IsNullOrEmpty(WritenItem.FileName))
            {
                Log?.Invoke("File Not Found In Device Partition : ", MsgType.Message);
                Log?.Invoke(flash.FileName, MsgType.Result , true);
                return false;
            }
            return true;
        }

        public async Task<Result> Write_Pit(string File)
        {
            var Result = new Result { error = "Failed RePartition" };
            try
            {
                byte[] pit = new byte[] { };
                var Extension = Path.GetExtension(File).ToLower();
                if (Extension == ".tar" || Extension == ".md5")
                {

                    var TarInfo = tar.TarInformation(File);
                    var pitname = TarInfo.ToList().Find(item => item.Filename.ToLower().EndsWith(".pit"));
                    if (pitname != null)
                    {
                        pit = await tar.ExtractFileFromTar(File, pitname.Filename);
                    }
                    else
                    {
                        Result.error = "Cannot Find Pit In Tar";
                        return Result;
                    }
                }
                else if (Extension == ".pit")
                {
                    pit = System.IO.File.ReadAllBytes(File);
                }
                else
                {
                    Result.error = "Pit Is Invalid";
                    return Result;
                }

                SamsungLokeCommand command = new SamsungLokeCommand(101);
                if (await cmd.LOKE_SendCMD(Device, command))
                {
                    command = new SamsungLokeCommand(101, 2, pit.Length);
                    if (await cmd.LOKE_SendCMD(Device, command))
                    {
                        await Device.WritePort(pit,  pit.Length);
                        cmd.BufferRead = await Device.ReadPort(8);
                        cmd.ValidateResponse(cmd.BufferRead, "Write PIT payload");
                        command = new SamsungLokeCommand(101, 3, pit.Length);
                        await cmd.LOKE_SendCMD(Device, command);
                        Result.status = true;

                    }
                }

            }
            catch (Exception ex)
            {
                Result.error = ex.Message;
            }

            return Result;
        }

        public async Task<ReadPitResult> Read_Pit()
        {
            var Result = new ReadPitResult();
            try
            {
                using (var PitStream = new MemoryStream())
                {
                    byte[] array = new byte[1025];
                    byte[] array2 = new byte[4097];
                    var cmd = new SamsungLokeCommand(0x65, 1);
                    if (await this.cmd.LOKE_SendCMD(Device,cmd))
                    {
                        long num = BitConverter.ToUInt32(this.cmd.BufferRead, 4);
                        if (num <= 0 || num > 8 * 1024 * 1024)
                        {
                            Result.error = $"Invalid PIT size from device: {num}";
                            return Result;
                        }

                        int num2 = (int)((num + 499L) / 500L);
                        int num3 = 0;
                        int num4 = num2 - 1;
                        for (int i = 0; i <= num4; i++)
                        {
                            int num5;
                            if (num - unchecked((long)num3) >= 500L)
                            {
                                num5 = 500;
                            }
                            else
                            {
                                num5 = (int)(num - unchecked((long)num3));
                            }
                            int num6 = 0;
                            do
                            {
                                array[num6] = 0;
                                num6++;
                            }
                            while (num6 <= 1023);
                            num6 = 0;
                            do
                            {
                                array2[num6] = 0;
                                num6++;
                            }
                            while (num6 <= 4096);
                            array[0] = 101;
                            array[1] = 0;
                            array[2] = 0;
                            array[3] = 0;
                            array[4] = 2;
                            array[5] = 0;
                            array[6] = 0;
                            array[7] = 0;
                            array[8] = (byte)(i % 256);
                            array[9] = (byte)(i / 256.0);
                            array[10] = (byte)(i / 65536.0);
                            array[11] = (byte)(i / 16777216.0);
                            num3 += num5;
                            await this.Device.WritePort(array,  1024);
                            int num7 = await this.Device.ReadExactAsync(array2, 0, num5);
                            if (num7 != num5)
                            {
                                Result.error = $"Incomplete PIT chunk {i}: {num7}/{num5} bytes.";
                                return Result;
                            }
                            PitStream.Write(array2, 0, num5);
                        }
                    }
                    byte[] sData = PitStream.ToArray();
                    Result.data = sData;
                    if (PitTool.UNPACK_PIT(sData))
                    {
                        Result.Pit = PitTool.xPIT_Entry.ToList();
                        Result.Result = true;
                    }
                    else
                    {
                        Result.error = "Device returned an invalid PIT.";
                    }
                }
            }
            catch (Exception e)
            {
                Result.error = e.Message;
            }
            return Result;
        }

        public long CalculateLz4SizeFromTar(string Archive, string inp_filename)
        {
            long lengh = 0L;
            try
            {
                var temp1 = tar.TarInformation(Archive);
                cListFileData TarFileData = temp1.ToList().Find(TarItem => TarItem.Filename == inp_filename);
                if (TarFileData != null)
                {
                    using (var reader = new FileStream(Archive, FileMode.Open, FileAccess.Read))
                    {
                        long j = TarFileData.FilePosStart;
                        reader.Position = j;
                        using (var lz4 = LZ4Stream.Decode(reader))
                        {
                            lengh = lz4.Length;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return lengh;
        }
    }
}
