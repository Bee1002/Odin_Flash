using K4os.Compression.LZ4.Streams;
using OdinFlash.Protocol.Pit;
using OdinFlash.Protocol.Port;
using OdinFlash.Protocol.structs;
using OdinFlash.Protocol.util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static OdinFlash.Protocol.util.utils;

// Aki ocurre la magia de la escritura de las particiones

namespace OdinFlash.Protocol
{
    public class Odin
    {
        /// <summary>Entrada del plan de flash: archivo TAR + partición PIT + bytes LOKE.</summary>
        public struct FlashPlanEntry
        {
            public FileFlash File;
            public TPIT_Entry PitEntry;
            public long FlashBytes;
        }

        /// <summary>Máx. bytes por paquete en NAND (cmd 102). Ajustable al arranque desde App.config (host Odin_Flash).</summary>
        public static int FlashChunkBytes { get; set; } = 1048576;

        /// <summary>Milisegundos de espera tras cada escritura de trozo y antes de leer el ACK de 8 bytes (0 = desactivado).</summary>
        public static int FlashAckDelayMs { get; set; } = 0;

        /// <summary>Si es true, vacía el buffer de entrada tras el buffer reserve (102,2) y antes del primer byte de imagen.</summary>
        public static bool DiscardInBufferBeforeNandPayload { get; set; } = false;

        /// <summary>Si es true, vacía RX antes de cada sesión NAND (cmd 102 inicial).</summary>
        public static bool DiscardSerialRxBeforeNandSession { get; set; } = true;

        /// <summary>Pausa tras cada partición exitosa antes de la siguiente.</summary>
        public static int InterPartitionDelayMs { get; set; } = 300;

        /// <summary>Purgar RX entre particiones (tras <see cref="InterPartitionDelayMs"/>).</summary>
        public static bool DiscardSerialRxBetweenPartitions { get; set; } = true;

        // --- Tunables velocidad NAND (App.config → LokePerformanceSettings). NO confundir con totales LOKE. ---
        // Defaults conservadores; Capa >= NandSmallPacketCapaThreshold activa paquete/delay reducidos en SendAsync.
        // WritePort envía siempre `packet` bytes por ciclo ACK (último trozo rellena con ceros) — alineación LOKE.

        /// <summary>Capa ≥ este valor: limitar tamaño efectivo de paquete NAND.</summary>
        public static int NandSmallPacketCapaThreshold { get; set; } = 128;

        /// <summary>Tamaño máximo de paquete NAND cuando Capa ≥ <see cref="NandSmallPacketCapaThreshold"/>.</summary>
        public static int NandPacketBytesWhenHighCapa { get; set; } = 262144;

        /// <summary>Mínimo ms tras cada trozo antes del ACK 8 B en equipos de alta Capa.</summary>
        public static int FlashAckDelayMsHighCapaMin { get; set; } = 3;

        /// <summary>Paquete NAND y LOKE 0x64/5; fijado por <see cref="LokePerformanceSettings.ApplyCapaRuntimeProfile"/> tras DVIF.</summary>
        public static int LokeSessionPacketBytes { get; set; } = 1048576;

        int? _lastReportedCapa;
        long _batchTotalBytes;
        long _batchCompletedBefore;
        long _phoneSessionTotalBytes;

        // --- Barra de progreso del teléfono (Download Mode) ---
        // Tres piezas distintas: (1) total LOKE = suma paquetes BL+AP+CP+CSC → LOKE_Initialize;
        // (2) plan PIT = qué se escribe → FlashFirmware; (3) paquete 0x64/2 → ver Cmd.GetCmdBuff.

        /// <summary>Total LOKE del lote (Calculated Size) para barra del teléfono y progreso PC.</summary>
        public void SetPhoneSessionTotalBytes(long totalBytes) => _phoneSessionTotalBytes = totalBytes;

        /// <summary>Última «Capa» leída en <see cref="PrintInfo"/> (DVIF) en esta sesión.</summary>
        public int? LastReportedCapa => _lastReportedCapa;

        /// <summary>Modelo DVIF (ej. SM-A326B) tras <see cref="PrintInfo"/>.</summary>
        public string DeviceModel { get; private set; }

        /// <summary>Sales Code / CSC activo (ej. AMO) tras <see cref="PrintInfo"/>.</summary>
        public string DeviceSalesCode { get; private set; }

        /// <summary>Build / PDA (ej. A326BXXS4CWA3) tras <see cref="PrintInfo"/>.</summary>
        public string DeviceBuildNumber { get; private set; }

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

        /// <summary>Informa al teléfono el total exacto del lote (barra de progreso en Download Mode).</summary>
        public async Task<bool> InitializeFlashTotal(long totalFileSize) =>
            await cmd.LOKE_SetFlashTotal(this.Device, totalFileSize);

        /// <summary>
        /// Qué flashear: PIT ↔ TAR, deduplicado. No confundir con el total LOKE del teléfono
        /// (<see cref="CalculatePackagePhoneTotalBytes"/>).
        /// </summary>
        public List<FlashPlanEntry> BuildFlashPlan(List<FileFlash> list, List<TPIT_Entry> pit)
        {
            var plan = new List<FlashPlanEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pitEntry in pit)
            {
                if (string.IsNullOrWhiteSpace(pitEntry.MflashFilename))
                    continue;

                var fileItem = FindMatchingFile(list, pitEntry);
                if (fileItem == null || !fileItem.Enable || !IsFlashableFile(fileItem))
                    continue;
                if (!IsTarRootMember(fileItem.FileName))
                    continue;

                var key = $"{fileItem.FilePath}|{fileItem.FileName}|{pitEntry.Midentifier}";
                if (!seen.Add(key))
                    continue;

                long flashBytes;
                try
                {
                    flashBytes = GetTarEntryFlashBytes(fileItem.FilePath, fileItem.FileName);
                }
                catch
                {
                    continue;
                }

                if (flashBytes <= 0L)
                    continue;

                fileItem.RawSize = flashBytes;
                plan.Add(new FlashPlanEntry
                {
                    File = fileItem,
                    PitEntry = pitEntry,
                    FlashBytes = flashBytes
                });
            }

            return plan;
        }

        /// <summary>Bytes LOKE del plan PIT (solo imágenes que se flashean).</summary>
        public long CalculateFlashTotalBytes(List<FileFlash> list, List<TPIT_Entry> pit)
        {
            long total = 0L;
            foreach (var entry in BuildFlashPlan(list, pit))
                total += entry.FlashBytes;
            return total;
        }

        /// <summary>
        /// Calculated Size Odin: todos los slots habilitados. Este valor va a LOKE, no la suma del plan PIT.
        /// </summary>
        public long CalculatePackageTotalBytes(IEnumerable<FileFlash> list)
        {
            long total = 0L;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list)
            {
                if (item == null || !item.Enable || !IsFlashableFile(item))
                    continue;
                if (!IsTarRootMember(item.FileName))
                    continue;

                var key = $"{item.FilePath}|{item.FileName}";
                if (!seen.Add(key))
                    continue;

                try
                {
                    total += GetTarEntryFlashBytes(item.FilePath, item.FileName);
                }
                catch
                {
                    if (item.RawSize > 0L)
                        total += item.RawSize;
                }
            }

            return total;
        }

        /// <summary>Total LOKE final: max(Calculated Size, sesiones alineadas a 128 KiB).</summary>
        public long CalculatePackagePhoneTotalBytes(IEnumerable<FileFlash> list)
        {
            long packageTotal = CalculatePackageTotalBytes(list);
            long alignedTotal = 0L;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list)
            {
                if (item == null || !item.Enable || !IsFlashableFile(item))
                    continue;
                if (!IsTarRootMember(item.FileName))
                    continue;

                var key = $"{item.FilePath}|{item.FileName}";
                if (!seen.Add(key))
                    continue;

                long fileBytes;
                try
                {
                    fileBytes = GetTarEntryFlashBytes(item.FilePath, item.FileName);
                }
                catch
                {
                    fileBytes = item.RawSize;
                }

                if (fileBytes > 0L)
                    alignedTotal += CalculateLokeAlignedBytes(fileBytes, 0L);
            }

            if (packageTotal <= 0L)
                return alignedTotal;
            if (alignedTotal <= 0L)
                return packageTotal;
            return Math.Max(packageTotal, alignedTotal);
        }

        /// <summary>Tras leer PIT: por si la alineación por partición supera el total del paquete.</summary>
        public long CalculatePhoneProgressTotalBytes(List<FileFlash> list, List<TPIT_Entry> pit)
        {
            long packagePhoneTotal = CalculatePackagePhoneTotalBytes(list);
            long alignedPlanTotal = 0L;
            foreach (var entry in BuildFlashPlan(list, pit))
                alignedPlanTotal += CalculateLokeAlignedBytes(entry.FlashBytes, entry.PitEntry.MdeviceType);

            if (alignedPlanTotal <= 0L)
                return packagePhoneTotal;
            return Math.Max(packagePhoneTotal, alignedPlanTotal);
        }

        // Suma de Calculate() por sesión NAND; el teléfono puede contar esto, no solo bytes crudos del .tar.
        private long CalculateLokeAlignedBytes(long fileSize, long deviceType)
        {
            if (fileSize <= 0L)
                return 0L;

            int maxSessionLen = (deviceType == 1L || deviceType == 2L || deviceType == 8L)
                ? 31457280
                : 104857600;
            long alignedTotal = 0L;
            long sent = 0L;
            while (sent < fileSize)
            {
                int sessionLen = (int)Math.Min(maxSessionLen, fileSize - sent);
                alignedTotal += Calculate(sessionLen);
                sent += sessionLen;
            }

            return alignedTotal;
        }

        /// <summary>Bytes reales que LOKE contabiliza: descomprimido para .lz4, tamaño TAR en el resto.</summary>
        public long GetTarEntryFlashBytes(string tarPath, string fileName)
        {
            if (!TryGetTarEntryDataOffset(tarPath, fileName, out var dataOffset, out var tarSize))
                throw new FileNotFoundException($"Cannot find {fileName} inside {tarPath}.");

            if (fileName.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
                return ReadLz4DecompressedSize(tarPath, dataOffset, fileName);

            return tarSize;
        }


        /// <summary>
        /// Localiza COM en modo download: primero WMI; si falla, escaneo handshake ODIN/LOKE.
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
        /// GetDeviceInfo and print information (orden fijo estilo Odin).
        /// </summary>
        public async Task PrintInfo()
        {
            _lastReportedCapa = null;
            DeviceModel = null;
            DeviceSalesCode = null;
            DeviceBuildNumber = null;
            var info = await DVIF();
            LogDeviceInfoField(info, "model", "Model Number: ", v => DeviceModel = v?.Trim());
            LogDeviceInfoField(info, "un", "Unique Id: ");
            LogDeviceInfoField(info, "capa", "Capa Number: ", value =>
            {
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap))
                    _lastReportedCapa = cap;
            });
            LogDeviceInfoField(info, "vendor", "vendor: ");
            LogDeviceInfoField(info, "fwver", "Firmware Version: ");
            LogDeviceInfoField(info, "product", "Product Id: ");
            LogDeviceInfoField(info, "prov", "Provision: ");
            LogDeviceInfoField(info, "sales", "Sales Code: ", v => DeviceSalesCode = v?.Trim());
            LogDeviceInfoField(info, "ver", "Build Number: ", v => DeviceBuildNumber = v?.Trim());
            LogDeviceInfoField(info, "did", "did Number: ");
            LogDeviceInfoField(info, "tmu_temp", "Tmu Number: ");
        }

        private void LogDeviceInfoField(
            Dictionary<string, string> info,
            string key,
            string label,
            Action<string> onLogged = null)
        {
            foreach (var item in info)
            {
                if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                onLogged?.Invoke(item.Value);
                Log?.Invoke(label, MsgType.Message);
                Log?.Invoke(item.Value, MsgType.Result);
                return;
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
                // Breve pausa tras lectura PIT / flash antes de cerrar sesión LOKE (103).
                await Task.Delay(300);
                SamsungLokeCommand cmd2 = new SamsungLokeCommand(103);
                await cmd.LOKE_SendCMD(this.Device, cmd2);
                cmd2.SeqCmd = 1;
                await cmd.LOKE_SendCMD(this.Device, cmd2);
            }
            catch
            {
                return false;
            }

            var watch = new Stopwatch();
            try
            {
                watch.Start();
                do
                {
                    if (!Device.Port.IsOpen)
                        return true;
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
       
        private static string NormalizeTarPath(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return "";
            return filename.Replace('\\', '/').Trim().TrimStart('/');
        }

        private static string GetTarMemberStorageName(string name)
        {
            var n = name.Replace('\\', '/').Trim();
            while (n.StartsWith("./", StringComparison.Ordinal))
                n = n.Substring(2);
            return n;
        }

        private static string NormalizeTarMemberName(string name)
        {
            var n = GetTarMemberStorageName(name);
            if (n.Contains("/"))
                return n;
            while (n.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            return n;
        }

        private static bool IsTarRootMember(string entryName)
        {
            var n = NormalizeTarMemberName(entryName);
            return !string.IsNullOrEmpty(n) && !n.Contains("/");
        }

        private static bool TarMemberNamesMatch(string left, string right)
        {
            left = NormalizeTarMemberName(left);
            right = NormalizeTarMemberName(right);
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return true;

            var leftBase = Path.GetFileNameWithoutExtension(left);
            var rightBase = Path.GetFileNameWithoutExtension(right);
            if (string.IsNullOrEmpty(leftBase) || string.IsNullOrEmpty(rightBase))
                return false;
            if (string.Equals(leftBase, rightBase, StringComparison.OrdinalIgnoreCase))
                return true;

            if (leftBase.StartsWith("preloader", StringComparison.OrdinalIgnoreCase)
                && rightBase.StartsWith("preloader", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private FileFlash FindMatchingFile(List<FileFlash> files, TPIT_Entry pitEntry)
        {
            FileFlash loose = null;
            foreach (var item in files)
            {
                if (!IsFlashableFile(item))
                    continue;

                if (TarMemberNamesMatch(pitEntry.MflashFilename, item.FileName))
                    return item;

                var pitBase = Path.GetFileNameWithoutExtension(NormalizeTarMemberName(pitEntry.MflashFilename));
                var tarBase = Path.GetFileNameWithoutExtension(NormalizeTarMemberName(item.FileName));
                if (!string.IsNullOrEmpty(pitBase)
                    && string.Equals(pitBase, tarBase, StringComparison.OrdinalIgnoreCase))
                    loose = loose ?? item;
            }

            return loose;
        }

        private static long ReadLz4DecompressedSize(string path, long dataOffset, string contextName)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Position = dataOffset;
                using (var lz4 = LZ4Stream.Decode(fs))
                {
                    if (lz4.Length <= 0L)
                        throw new InvalidDataException($"Cannot get decompressed size for {contextName}.");
                    return lz4.Length;
                }
            }
        }

        private static bool TryGetTarEntryDataOffset(
            string tarPath,
            string fileName,
            out long dataOffset,
            out long dataSize)
        {
            dataOffset = 0L;
            dataSize = 0L;

            using (var fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var offset = 0L;
                var header = new byte[512];

                while (fs.Read(header, 0, 512) == 512)
                {
                    if (header[0] == 0)
                        break;

                    var name = ParseTarName(header);
                    if (name.EndsWith("/", StringComparison.Ordinal))
                    {
                        offset += 512;
                        fs.Position = offset;
                        continue;
                    }

                    var fileSize = ParseTarOctalField(header, 124, 12);
                    if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(GetTarMemberStorageName(name), GetTarMemberStorageName(fileName), StringComparison.OrdinalIgnoreCase))
                    {
                        dataOffset = offset + 512;
                        dataSize = fileSize;
                        return true;
                    }

                    offset += 512 + TarDataPaddedSize(fileSize);
                    fs.Position = offset;
                }
            }

            return false;
        }

        private static long TarDataPaddedSize(long size) =>
            ((size + 511) / 512) * 512;

        private static string ParseTarName(byte[] header)
        {
            var end = Array.IndexOf(header, (byte)0, 0, 100);
            if (end < 0)
                end = 100;
            return Encoding.ASCII.GetString(header, 0, end).TrimEnd('\0', ' ');
        }

        private static long ParseTarOctalField(byte[] header, int offset, int length)
        {
            long value = 0L;
            for (var i = 0; i < length; i++)
            {
                var b = header[offset + i];
                if (b == 0 || b == (byte)' ')
                    break;
                if (b < (byte)'0' || b > (byte)'7')
                    continue;
                value = value * 8 + (b - '0');
            }

            return value;
        }

        private string NormalizeFlashName(string filename)
        {
            return NormalizeTarMemberName(filename);
        }

        private FileFlash FoundItem(List<FileFlash> files, TPIT_Entry partition) =>
            FindMatchingFile(files, partition);

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
       
        private FileFlash FoundItem(FileFlash files, TPIT_Entry partition)
        {
            if (TarMemberNamesMatch(partition.MflashFilename, files.FileName))
                return files;
            return null;
        }

        /// <summary>Alinea el tamaño de sesión a múltiplo de 128 KiB (2^17), requerido por LOKE para el comando 102,2.</summary>
        public long Calculate(int sessionLen)
        {
            const long align = 1L << 17;
            if (sessionLen <= 0) return 0;
            return ((sessionLen - 1L) / align + 1L) * align;
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
            if (DiscardSerialRxBeforeNandSession)
            {
                try { await Device.DiscardInBufferAsync(); } catch { }
            }

            SamsungLokeCommand command = new SamsungLokeCommand(102);
            await cmd.LOKE_SendCMD(Device,command);
            command = new SamsungLokeCommand(102, 2, Calculate(sessionLength));
            await cmd.LOKE_SendCMD(Device, command);
            if (DiscardInBufferBeforeNandPayload)
            {
                await Device.DiscardInBufferAsync();
            }
            int sent = 0;
            int packet = LokeSessionPacketBytes;
            if (packet < 4096)
                packet = 4096;
            if (packet > 1048576)
                packet = 1048576;
            byte[] flashBuffer = new byte[packet];
            int ackDelay = FlashAckDelayMs;
            if (_lastReportedCapa is int capa && capa >= NandSmallPacketCapaThreshold && FlashAckDelayMsHighCapaMin > 0)
                ackDelay = Math.Max(ackDelay, FlashAckDelayMsHighCapaMin);
            while (sent < sessionLength)
            {
                Array.Clear(flashBuffer, 0, flashBuffer.Length);
                int toRead = Math.Min(flashBuffer.Length, sessionLength - sent);
                await ReadExactlyFromStream(inputStream, flashBuffer, toRead);
                // Siempre `packet` bytes por ACK LOKE (cola en cero si toRead < packet).
                await Device.WritePort(flashBuffer, flashBuffer.Length);
                if (ackDelay > 0)
                    await Task.Delay(ackDelay);
                cmd.ValidateResponse(await Device.ReadPort(8), $"NAND data ACK {entry.MflashFilename} offset {sent}");
                sent += toRead;
                addProgressAction(toRead);
            }
            command = new SamsungLokeCommand(102, 3, entry.MbinaryType, sessionLength);
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
            ReportProgress(entry.MflashFilename, size, 0L);
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
                    ReportProgress(entry.MflashFilename, size, currentProgress);
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
     
        public List<FileFlash> CorruptedPartitions(List<FileFlash> ready, List<FileFlash> Writed)
        {
            var ListCorrupted = new List<FileFlash>();
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
                    ListCorrupted.Add(item);
                }
            }
            return ListCorrupted;
        }

        private void ReportProgress(string filename, long partitionMax, long partitionValue)
        {
            long batchTotal = _batchTotalBytes > 0L ? _batchTotalBytes : partitionMax;
            long batchWritten = _batchTotalBytes > 0L
                ? _batchCompletedBefore + partitionValue
                : partitionValue;
            if (batchTotal > 0L && batchWritten > batchTotal)
                batchWritten = batchTotal;

            ProgressChanged?.Invoke(
                filename,
                partitionMax,
                partitionValue,
                partitionValue,
                batchTotal,
                batchWritten);
        }

        public async Task<bool> FlashFirmware(List<FileFlash> list, List<TPIT_Entry> pit, int EfsClear, int BootUpdate, bool Debug)
        {
            var plan = BuildFlashPlan(list, pit);
            if (_phoneSessionTotalBytes > 0L)
                _batchTotalBytes = _phoneSessionTotalBytes; // Mismo total que LOKE (barra PC coherente con teléfono)
            else
            {
                _batchTotalBytes = 0L;
                foreach (var planned in plan)
                    _batchTotalBytes += planned.FlashBytes;
            }
            _batchCompletedBefore = 0L;
            var WritenItem = new List<FileFlash>();
            foreach (var planned in plan)
            {
                var FileItem = planned.File;
                var pitEntry = planned.PitEntry;
                if (Debug)
                {
                    Log?.Invoke($"Flashing {FileItem.FileName}: ", MsgType.Message);
                }
                if (!await FlashFromTar(FileItem.FilePath, planned.FlashBytes, FileItem.FileName,
                    pitEntry, EfsClear, BootUpdate))
                {
                    if (Debug)
                    {
                        Log?.Invoke($" : Failed", MsgType.Result, true);
                    }
                    return false;
                }

                WritenItem.Add(FileItem);
                _batchCompletedBefore += planned.FlashBytes;
                if (Debug)
                {
                    Log?.Invoke($" : Ok", MsgType.Result);
                }

                if (InterPartitionDelayMs > 0)
                    await Task.Delay(InterPartitionDelayMs);
                if (DiscardSerialRxBetweenPartitions)
                {
                    try { await Device.DiscardSerialBuffersAsync(); } catch { }
                }
            }

            var GetCorrupted = CorruptedPartitions(list, WritenItem);
            if (GetCorrupted.Count > 0)
            {
                Log?.Invoke("File partition cannot find in your device partition : ", MsgType.Message);
                foreach (var corrupted in GetCorrupted)
                {
                    Log?.Invoke(corrupted.FileName + ", ", MsgType.Result , true);
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
            ReportProgress(inp_filename, 0L, 0L);
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
                var Extension = Path.GetExtension(File).ToLowerInvariant();
                var isTarPack = Extension == ".tar" || Extension == ".md5"
                    || File.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);

                if (isTarPack)
                {
                    var TarInfo = tar.TarInformation(File);
                    if (TarInfo == null || TarInfo.Count == 0)
                    {
                        Result.error = "Cannot open or parse Tar archive.";
                        return Result;
                    }
                    var pitname = TarInfo
                        .Where(item => NormalizeTarPath(item.Filename).EndsWith(".pit", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => NormalizeTarPath(item.Filename), StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (pitname == null)
                        pitname = TarInfo.ToList().Find(item => item.Filename != null && item.Filename.ToLowerInvariant().EndsWith(".pit"));
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

                if (pit == null || pit.Length < 64)
                {
                    Result.error = "PIT vacío o demasiado pequeño.";
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
                var want = NormalizeTarPath(inp_filename);
                cListFileData TarFileData = temp1.ToList().Find(TarItem => string.Equals(NormalizeTarPath(TarItem.Filename), want, StringComparison.OrdinalIgnoreCase));
                if (TarFileData == null)
                    TarFileData = temp1.ToList().Find(TarItem => string.Equals(TarItem.Filename, inp_filename, StringComparison.OrdinalIgnoreCase));
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

        /// <summary>Tamaño descomprimido de un .lz4 suelto (fuera de TAR). Necesario para <see cref="LOKE_Initialize"/>.</summary>
        public long CalculateLz4DecompressedFileSize(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var lz4 = LZ4Stream.Decode(fs))
                {
                    return lz4.Length;
                }
            }
            catch
            {
                return 0;
            }
        }
    }
}
