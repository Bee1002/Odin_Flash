using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Management;
using System.Linq;
using System.Collections.Generic;

namespace Odin_Flash.Class
{

    /// <summary>
    /// Motor de protocolo Odin refactorizado y robusto
    /// Usa Streams para soportar archivos de varios GB sin OutOfMemory
    /// Basado en análisis de Ghidra: FUN_00435ad0, FUN_00434170, FUN_00434d50, FUN_00434fb0, FUN_00438342
    /// </summary>
    public class OdinEngine : IDisposable
    {
        // Eventos para la UI
        /// <summary>
        /// Evento de logging con nivel de detalle
        /// </summary>
        public event Action<string, LogLevel> OnLog;
        
        /// <summary>
        /// Evento de progreso de transferencia (current, total)
        /// </summary>
        public event Action<long, long> OnProgress;

        private SerialPort _port;
        private readonly string _portName;

        // Importación nativa para limpiar el puerto (Ref: Ghidra FUN_00438342)
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PurgeComm(IntPtr hFile, uint dwFlags);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ClearCommError(SafeFileHandle hFile, out uint lpErrors, IntPtr lpStat);

        private const uint PURGE_TXABORT = 0x0001;
        private const uint PURGE_RXABORT = 0x0002;
        private const uint PURGE_TXCLEAR = 0x0004;
        private const uint PURGE_RXCLEAR = 0x0008;

        /// <summary>
        /// Constructor para modo directo con puerto serial
        /// </summary>
        public OdinEngine(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                throw new ArgumentNullException(nameof(portName));
            
            _portName = portName;
        }

        /// <summary>
        /// Método público para reportar logs desde fuera de la clase
        /// Encapsula el acceso al evento OnLog
        /// </summary>
        public void ReportLog(string message, LogLevel level)
        {
            OnLog?.Invoke(message, level);
        }

        /// <summary>
        /// Registra un mensaje con el nivel especificado (uso interno)
        /// </summary>
        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            OnLog?.Invoke(message, level);
        }
        
        [Obsolete("Usar Log(string, LogLevel) en su lugar")]
        private void LogMessage(string msg, bool error = false)
        {
            LogLevel level = error ? LogLevel.Error : LogLevel.Info;
            Log(msg, level);
        }

        /// <summary>
        /// Flasheo robusto usando FileStream para evitar OutOfMemory
        /// Soporta archivos de varios GB sin cargar todo en memoria
        /// </summary>
        public async Task<bool> FlashFileAsync(string filePath, bool isLargeFile)
        {
            if (!File.Exists(filePath))
            {
                Log($"Archivo no encontrado: {filePath}", LogLevel.Error);
                return false;
            }

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true))
                {
                    long totalSize = fs.Length;
                    long bytesSent = 0;
                    int chunkSize = isLargeFile ? LokeProtocol.CHUNK_DATA : LokeProtocol.CHUNK_CONTROL;
                    byte[] buffer = new byte[chunkSize];

                    string fileName = Path.GetFileName(filePath);
                    Log($"Iniciando transferencia de {fileName} ({totalSize / (1024.0 * 1024.0):F2} MB)...", LogLevel.Info);

                    if (!await InitializeCommunicationAsync()) return false;

                    int read;
                    long lastProgressReport = 0;
                    const long PROGRESS_REPORT_INTERVAL = 1024 * 1024; // Reportar cada 1MB

                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (!SendBufferDirect(buffer, read)) return false;

                        bytesSent += read;
                        
                        // Reportar progreso cada 1MB para no saturar UI
                        if (bytesSent - lastProgressReport >= PROGRESS_REPORT_INTERVAL || bytesSent == totalSize)
                        {
                            OnProgress?.Invoke(bytesSent, totalSize);
                            lastProgressReport = bytesSent;
                        }
                    }

                    OnProgress?.Invoke(totalSize, totalSize);
                    Log($"Transferencia completada: {fileName}", LogLevel.Success);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Fallo crítico: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Escribe un bloque de datos y verifica el ACK del dispositivo (async)
        /// </summary>
        /// <param name="buffer">Buffer con los datos a enviar</param>
        /// <param name="length">Cantidad de bytes a enviar</param>
        /// <returns>True si se recibió ACK correctamente</returns>
        private async Task<bool> WriteAndVerifyAckAsync(byte[] buffer, int length)
        {
            if (_port == null || !_port.IsOpen)
                return false;

            try
            {
                // Asegurar que el bloque sea exactamente de 500 bytes (rellenar con ceros si es necesario)
                byte[] block = new byte[LokeProtocol.CHUNK_CONTROL];
                Array.Copy(buffer, 0, block, 0, Math.Min(length, LokeProtocol.CHUNK_CONTROL));
                // El resto ya está inicializado con 0x00

                // Enviar el bloque completo de 500 bytes
                _port.Write(block, 0, LokeProtocol.CHUNK_CONTROL);

                // Esperar y verificar ACK (usar await Task.Delay en lugar de Thread.Sleep)
                await Task.Delay(10); // Pequeño delay para que el dispositivo procese
                return await LokeProtocol.WaitAndVerifyAckAsync(_port, 1000);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Envía un archivo PIT usando FileStream para eficiencia en memoria
        /// Ciclo: Enviar comando PITM (500 bytes) -> Enviar PIT en bloques de 500 bytes -> Esperar estabilidad
        /// Basado en análisis de Ghidra: FUN_00434fb0 -> Comando "PITM" (0x5049544D)
        /// </summary>
        /// <param name="pitFilePath">Ruta al archivo PIT</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public async Task<bool> FlashPitFileAsync(string pitFilePath)
        {
            if (!File.Exists(pitFilePath))
            {
                Log($"Archivo PIT no encontrado: {pitFilePath}", LogLevel.Error);
                return false;
            }

            // Paso 1: Comando de Control - Informar que vamos a enviar un PIT
            // Según Ghidra: FUN_00434fb0 -> Comando "PITM" (0x5049544D)
            Log("Enviando comando PITM...", LogLevel.Debug);
            if (!await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.SetPitMode, 0, 0))
            {
                Log("Error: El dispositivo no aceptó el modo PIT", LogLevel.Error);
                return false;
            }

            try
            {
                using (FileStream fs = new FileStream(pitFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 500, useAsync: true))
                {
                    // El PIT siempre se envía en bloques de 500 bytes (Loke Control Chunk)
                    byte[] buffer = new byte[500];
                    int read;
                    long totalSent = 0;
                    long fileSize = fs.Length;

                    Log($"Transfiriendo archivo PIT ({fileSize} bytes)...", LogLevel.Info);

                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        // Enviar el bloque y esperar el ACK (0x06)
                        if (!await WriteAndVerifyAckAsync(buffer, read))
                        {
                            Log("Error enviando segmento del PIT", LogLevel.Error);
                            return false;
                        }

                        totalSent += read;
                    }

                    Log($"PIT transferido completamente ({totalSent} bytes)", LogLevel.Debug);
                }

                // Delay crítico para que el chip eMMC/UFS procese la tabla de particiones
                // DELAY_STABILITY es vital para que el eMMC/UFS se re-partitione
                Log("Esperando estabilización del dispositivo después de re-partition...", LogLevel.Info);
                await Task.Delay(1000); // 1 segundo de estabilidad

                Log("PIT flasheado con éxito.", LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Fallo en PIT: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad - redirige a FlashPitFileAsync
        /// </summary>
        [Obsolete("Usar FlashPitFileAsync en su lugar")]
        public async Task<bool> SendPitFile(string pitFilePath)
        {
            return await FlashPitFileAsync(pitFilePath);
        }

        /// <summary>
        /// Envía datos directamente desde un Stream usando el protocolo de paquetes de control
        /// Optimizado para procesar archivos .tar sin extraer a disco
        /// Ciclo: Enviar comando DATA + Tamaño total -> Enviar bloques de 128KB -> Validar ACK
        /// </summary>
        /// <param name="stream">Stream con los datos a enviar</param>
        /// <param name="fileSize">Tamaño del archivo en bytes</param>
        /// <param name="fileName">Nombre del archivo (para logging)</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public async Task<bool> FlashStreamAsync(Stream stream, long fileSize, string fileName = "stream")
        {
            if (stream == null || !stream.CanRead)
            {
                Log($"Stream no válido para {fileName}", LogLevel.Error);
                return false;
            }

            int currentChunkSize = (fileSize > 1024 * 1024) ? LokeProtocol.CHUNK_DATA : LokeProtocol.CHUNK_CONTROL;

            try
            {
                if (fileSize > 100 * 1024 * 1024)
                {
                    if (_port != null)
                    {
                        _port.WriteTimeout = -1;
                        _port.ReadTimeout = 10000;
                    }
                }

                if (!await InitializeCommunicationAsync()) return false;

                Log($"Enviando comando DATA para {fileName} ({fileSize / (1024.0 * 1024.0):F2} MB)...", LogLevel.Debug);

                // Paso 1: Enviar comando DATA con el tamaño total del archivo
                if (!await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.FlashData, (uint)fileSize, 0))
                {
                    Log("Error al enviar comando DATA", LogLevel.Error);
                    return false;
                }

                Log($"Comando DATA enviado. Iniciando transferencia de {fileName}...", LogLevel.Info);

                // Paso 2: Enviar el stream en bloques
                byte[] buffer = new byte[currentChunkSize];
                int bytesRead;
                long totalSent = 0;
                long lastProgressReport = 0;
                const long PROGRESS_REPORT_INTERVAL = 1024 * 1024;
                DateTime lastWriteTime = DateTime.Now;
                const int MAX_IDLE_MS = 400;
                uint sequenceId = 0;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    try
                    {
                        TimeSpan timeSinceLastWrite = DateTime.Now - lastWriteTime;
                        if (timeSinceLastWrite.TotalMilliseconds > MAX_IDLE_MS)
                        {
                            if (_port.BytesToRead == 0)
                            {
                                try
                                {
                                    byte[] keepAlive = { 0x64 };
                                    _port.Write(keepAlive, 0, 1);
                                }
                                catch { }
                            }
                        }

                        _port.Write(buffer, 0, bytesRead);
                        totalSent += bytesRead;
                        lastWriteTime = DateTime.Now;
                        sequenceId++;

                        // Validar ACK cada cierto intervalo (en modelos modernos)
                        if (sequenceId % 10 == 0 && _port.BytesToRead > 0)
                        {
                            if (!await LokeProtocol.WaitAndVerifyAckAsync(_port, 100))
                            {
                                Log("ACK no recibido, pero continuando...", LogLevel.Warning);
                            }
                        }

                        if (totalSent - lastProgressReport >= PROGRESS_REPORT_INTERVAL || totalSent == fileSize)
                        {
                            OnProgress?.Invoke(totalSent, fileSize);
                            lastProgressReport = totalSent;
                        }

                        if (currentChunkSize == LokeProtocol.CHUNK_DATA && totalSent % (LokeProtocol.CHUNK_DATA * 10) == 0)
                        {
                            await Task.Delay(1);
                        }
                    }
                    catch (IOException ex)
                    {
                        Log($"Atasco de E/S detectado. Aplicando corrección Odin...", LogLevel.Warning);
                        if (!await RecoverPortAfterErrorAsync()) return false;
                        try
                        {
                            _port.Write(buffer, 0, bytesRead);
                            totalSent += bytesRead;
                        }
                        catch (Exception retryEx)
                        {
                            Log($"Error al reintentar: {retryEx.Message}", LogLevel.Error);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error inesperado: {ex.Message}", LogLevel.Error);
                        return false;
                    }
                }

                if (fileSize > 100 * 1024 * 1024)
                {
                    Log("Archivo grande finalizado. Limpiando buffers...", LogLevel.Debug);
                    await ClearPortAfterLargeFileAsync();
                }

                OnProgress?.Invoke(totalSent, fileSize);
                Log($"Archivo {fileName} enviado exitosamente ({fileSize / (1024.0 * 1024.0):F2} MB)", LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error Crítico: {ex.Message}", LogLevel.Error);
                return false;
            }
            finally
            {
                if (fileSize > 100 * 1024 * 1024 && _port != null)
                {
                    _port.WriteTimeout = 5000;
                    _port.ReadTimeout = 5000;
                }
            }
        }

        /// <summary>
        /// Envía un archivo de firmware usando el protocolo de paquetes de control
        /// Ciclo: Enviar comando DATA + Tamaño total -> Enviar bloques de 128KB -> Validar ACK
        /// </summary>
        /// <param name="filePath">Ruta al archivo a enviar</param>
        /// <param name="fileSize">Tamaño del archivo en bytes</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public async Task<bool> SendFileWithLokeProtocol(string filePath, long fileSize)
        {
            if (!File.Exists(filePath))
            {
                Log($"Archivo no encontrado: {filePath}", LogLevel.Error);
                return false;
            }

            string fileName = Path.GetFileName(filePath);
            int currentChunkSize = (fileSize > 1024 * 1024) ? LokeProtocol.CHUNK_DATA : LokeProtocol.CHUNK_CONTROL;

            try
            {
                if (fileSize > 100 * 1024 * 1024)
                {
                    if (_port != null)
                    {
                        _port.WriteTimeout = -1;
                        _port.ReadTimeout = 10000;
                    }
                }

                if (!PrepareConnection()) return false;

                Log($"Enviando comando DATA para {fileName} ({fileSize / (1024.0 * 1024.0):F2} MB)...", LogLevel.Debug);

                // Paso 1: Enviar comando DATA con el tamaño total del archivo
                if (!await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.FlashData, (uint)fileSize, 0))
                {
                    Log("Error al enviar comando DATA", LogLevel.Error);
                    return false;
                }

                Log($"Comando DATA enviado. Iniciando transferencia de {fileName}...", LogLevel.Info);

                // Paso 2: Usar FlashStreamAsync para enviar el archivo
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, currentChunkSize, useAsync: true))
                {
                    bool sendResult = await FlashStreamAsync(fs, fileSize, fileName);
                    if (!sendResult) return false;
                }

                if (fileSize > 100 * 1024 * 1024)
                {
                    Log("Archivo grande finalizado. Limpiando buffers...", LogLevel.Debug);
                    await ClearPortAfterLargeFileAsync();
                }

                OnProgress?.Invoke(fileSize, fileSize);
                Log($"Archivo {fileName} enviado exitosamente ({fileSize / (1024.0 * 1024.0):F2} MB)", LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error Crítico: {ex.Message}", LogLevel.Error);
                return false;
            }
            finally
            {
                if (fileSize > 100 * 1024 * 1024 && _port != null)
                {
                    _port.WriteTimeout = 5000;
                    _port.ReadTimeout = 5000;
                }
            }
        }

        /// <summary>
        /// Finaliza la sesión Odin enviando el comando ENDS (async)
        /// El dispositivo se reiniciará automáticamente después de recibir este comando
        /// </summary>
        /// <returns>True si el comando se envió correctamente</returns>
        public async Task<bool> EndSessionAsync()
        {
            try
            {
                if (_port == null || !_port.IsOpen)
                {
                    Log("Puerto no disponible para finalizar sesión", LogLevel.Error);
                    return false;
                }

                Log("Finalizando sesión Odin...", LogLevel.Info);
                
                if (await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.EndSession, 0, 0))
                {
                    Log("Sesión finalizada. El dispositivo se reiniciará automáticamente.", LogLevel.Success);
                    return true;
                }
                else
                {
                    Log("Error al enviar comando ENDS", LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Error al finalizar sesión: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a EndSessionAsync
        /// </summary>
        [Obsolete("Usar EndSessionAsync en su lugar")]
        public bool EndSession()
        {
            try
            {
                return Task.Run(async () => await EndSessionAsync()).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Inicializa la comunicación con el dispositivo Odin
        /// Consolida toda la lógica de handshake: abre puerto, limpia buffers, envía comando ODIN, verifica respuesta LOKE
        /// </summary>
        /// <returns>True si la inicialización fue exitosa</returns>
        public async Task<bool> InitializeCommunicationAsync()
        {
            try
            {
                // Abrir puerto si no está abierto
                if (_port == null || !_port.IsOpen)
                {
                    if (_port == null)
                    {
                        _port = new SerialPort(_portName, 115200, Parity.None, 8, StopBits.One)
                        {
                            ReadTimeout = 5000,
                            WriteTimeout = 5000
                        };
                    }
                    _port.Open();
                }
                
                // Limpieza nativa de buffers (Clave para evitar ERROR_IO_PENDING)
                PurgePortBuffers();
                
                // Pequeño delay para estabilización después de abrir puerto
                await Task.Delay(LokeProtocol.DELAY_STABILITY);
                
                // Enviar comando ODIN y verificar respuesta LOKE
                return await LokeProtocol.PerformHandshakeAsync(_port);
            }
            catch (Exception ex)
            {
                Log($"Error en InitializeCommunication: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a InitializeCommunicationAsync
        /// </summary>
        [Obsolete("Usar InitializeCommunicationAsync en su lugar")]
        private bool PrepareConnection()
        {
            try
            {
                // Ejecutar de forma sincrónica usando Task.Run
                return Task.Run(async () => await InitializeCommunicationAsync()).Result;
            }
            catch (Exception ex)
            {
                Log($"Error en PrepareConnection: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Limpieza nativa de buffers usando PurgeComm (Ref: FUN_00438342)
        /// </summary>
        private void PurgePortBuffers()
        {
            try
            {
                if (_port == null || !_port.IsOpen) return;

                // Obtener el handle del puerto usando reflexión
                var baseStream = _port.BaseStream;
                var handleField = baseStream.GetType().GetField("_handle", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (handleField != null)
                {
                    var handle = (SafeFileHandle)handleField.GetValue(baseStream);
                    if (handle != null && !handle.IsInvalid)
                    {
                        PurgeComm(handle.DangerousGetHandle(), 
                            PURGE_RXCLEAR | PURGE_TXCLEAR | PURGE_RXABORT | PURGE_TXABORT);
                    }
                }
            }
            catch
            {
                // Fallback a métodos públicos si la limpieza nativa falla
                if (_port != null && _port.IsOpen)
                {
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                }
            }
        }

        private bool SendBufferDirect(byte[] data, int length)
        {
            try
            {
                if (_port == null || !_port.IsOpen) return false;
                _port.Write(data, 0, length);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error al enviar buffer: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Obtiene el puerto serial actual (para uso con Win32Comm)
        /// </summary>
        public System.IO.Ports.SerialPort GetCurrentPort()
        {
            return _port;
        }

        /// <summary>
        /// Recupera el puerto después de un error usando funciones nativas y re-sincroniza LOKE (async)
        /// </summary>
        public async Task<bool> RecoverPortAfterErrorAsync()
        {
            if (_port == null)
            {
                await Task.Delay(500);
                return true;
            }

            try
            {
                Log("Error detectado. Intentando recuperar puerto...", LogLevel.Warning);
                PurgePortBuffers();
                await Task.Delay(500);
                
                if (!_port.IsOpen) _port.Open();
                
                // Usar InitializeCommunicationAsync para re-sincronizar
                bool handshakeSuccess = await InitializeCommunicationAsync();
                if (handshakeSuccess)
                {
                    Log("Puerto recuperado y protocolo LOKE re-sincronizado", LogLevel.Success);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log($"Error durante recuperación: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a RecoverPortAfterErrorAsync
        /// </summary>
        [Obsolete("Usar RecoverPortAfterErrorAsync en su lugar")]
        public bool RecoverPortAfterError()
        {
            try
            {
                return Task.Run(async () => await RecoverPortAfterErrorAsync()).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Limpieza después de archivos grandes para evitar ERROR_IO_PENDING (async)
        /// </summary>
        public async Task<bool> ClearPortAfterLargeFileAsync()
        {
            if (_port == null)
            {
                await Task.Delay(500);
                return true;
            }

            try
            {
                Log("Waiting for device buffer to clear...", LogLevel.Debug);
                await Task.Delay(500);
                
                if (!_port.IsOpen) _port.Open();
                
                PurgePortBuffers();
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error durante limpieza: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a ClearPortAfterLargeFileAsync
        /// </summary>
        [Obsolete("Usar ClearPortAfterLargeFileAsync en su lugar")]
        public bool ClearPortAfterLargeFile()
        {
            try
            {
                return Task.Run(async () => await ClearPortAfterLargeFileAsync()).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Detecta si hay un dispositivo Samsung en modo Download usando VID/PID
        /// Samsung VID: 04E8, Modo Download PID: 685D o 6860
        /// </summary>
        /// <returns>True si se detecta un dispositivo Samsung en modo Download</returns>
        public static bool IsSamsungDownloadMode()
        {
            try
            {
                // Buscar dispositivos Samsung Mobile USB Composite Device usando WMI
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%Samsung Mobile USB Composite Device%'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        // Verificar que el dispositivo esté presente
                        string deviceId = device["DeviceID"]?.ToString() ?? "";
                        
                        // Verificar VID/PID en el DeviceID
                        // Formato típico: USB\VID_04E8&PID_685D\...
                        if (deviceId.Contains("VID_04E8") && 
                            (deviceId.Contains("PID_685D") || deviceId.Contains("PID_6860")))
                        {
                            return true;
                        }
                    }
                }
                
                // Fallback: buscar por nombre más genérico
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%Samsung%' AND Caption LIKE '%USB%'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string deviceId = device["DeviceID"]?.ToString() ?? "";
                        if (deviceId.Contains("VID_04E8") && 
                            (deviceId.Contains("PID_685D") || deviceId.Contains("PID_6860")))
                        {
                            return true;
                        }
                    }
                }
                
                return false;
            }
            catch
            {
                // Si WMI falla, retornar false
                return false;
            }
        }

        /// <summary>
        /// Detecta el puerto COM asociado a un dispositivo Samsung en modo Download
        /// </summary>
        /// <returns>Nombre del puerto COM (ej: "COM3") o null si no se encuentra</returns>
        public static string DetectSamsungComPort()
        {
            try
            {
                // Buscar dispositivos seriales Samsung
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%Samsung%' AND (Caption LIKE '%COM%' OR Caption LIKE '%Serial%')"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string deviceId = device["DeviceID"]?.ToString() ?? "";
                        
                        // Verificar que sea un dispositivo Samsung con VID 04E8
                        if (deviceId.Contains("VID_04E8"))
                        {
                            // Buscar el puerto COM asociado
                            string caption = device["Caption"]?.ToString() ?? "";
                            
                            // Extraer número de puerto COM del caption (ej: "Samsung Mobile USB Serial Port (COM3)")
                            var match = System.Text.RegularExpressions.Regex.Match(caption, @"COM(\d+)");
                            if (match.Success)
                            {
                                return $"COM{match.Groups[1].Value}";
                            }
                        }
                    }
                }
                
                // Fallback: buscar todos los puertos COM disponibles
                // y verificar si alguno corresponde a Samsung
                string[] ports = SerialPort.GetPortNames();
                foreach (string port in ports)
                {
                    try
                    {
                        using (var testPort = new SerialPort(port, 115200))
                        {
                            // Intentar abrir brevemente para verificar
                            testPort.Open();
                            testPort.Close();
                            
                            // Si se puede abrir, podría ser el puerto correcto
                            // En una implementación más robusta, se podría verificar
                            // enviando el comando ODIN y esperando respuesta LOKE
                            return port;
                        }
                    }
                    catch
                    {
                        // Continuar con el siguiente puerto
                        continue;
                    }
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lee el archivo PIT desde el dispositivo usando el comando PITR
        /// </summary>
        /// <returns>Array de bytes con el contenido del PIT, o null si falla</returns>
        public async Task<byte[]> ReadPitFromDeviceAsync()
        {
            try
            {
                if (_port == null || !_port.IsOpen)
                {
                    Log("Puerto no disponible para leer PIT", LogLevel.Error);
                    return null;
                }

                Log("Enviando comando PITR para leer PIT del dispositivo...", LogLevel.Debug);

                // Paso 1: Enviar comando PITR
                if (!await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.ReadPit, 0, 0))
                {
                    Log("Error al enviar comando PITR", LogLevel.Error);
                    return null;
                }

                Log("Comando PITR enviado. Esperando datos del PIT...", LogLevel.Info);

                // Paso 2: Leer datos del PIT
                // El dispositivo enviará el PIT en bloques de 500 bytes
                List<byte> pitData = new List<byte>();
                byte[] buffer = new byte[LokeProtocol.CHUNK_CONTROL];
                int timeout = 5000; // 5 segundos de timeout
                int elapsed = 0;
                const int pollInterval = 50;

                while (elapsed < timeout)
                {
                    if (_port.BytesToRead > 0)
                    {
                        int bytesRead = _port.Read(buffer, 0, Math.Min(buffer.Length, _port.BytesToRead));
                        if (bytesRead > 0)
                        {
                            pitData.AddRange(buffer.Take(bytesRead));
                            elapsed = 0; // Resetear timeout si hay datos
                        }
                    }
                    else
                    {
                        // Si no hay datos por un tiempo, asumir que terminó
                        if (pitData.Count > 0)
                        {
                            await Task.Delay(200); // Esperar un poco más por si hay más datos
                            if (_port.BytesToRead == 0)
                            {
                                break; // No hay más datos, terminar
                            }
                        }
                    }

                    await Task.Delay(pollInterval);
                    elapsed += pollInterval;
                }

                if (pitData.Count == 0)
                {
                    Log("No se recibieron datos del PIT", LogLevel.Error);
                    return null;
                }

                Log($"PIT leído exitosamente ({pitData.Count} bytes)", LogLevel.Success);
                return pitData.ToArray();
            }
            catch (Exception ex)
            {
                Log($"Error al leer PIT: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Reinicia el dispositivo a modo normal usando el comando REBT
        /// </summary>
        /// <returns>True si el comando se envió correctamente</returns>
        public async Task<bool> RebootToNormalModeAsync()
        {
            try
            {
                if (_port == null || !_port.IsOpen)
                {
                    Log("Puerto no disponible para reiniciar dispositivo", LogLevel.Error);
                    return false;
                }

                Log("Enviando comando REBT para reiniciar a modo normal...", LogLevel.Info);

                // Enviar comando REBT
                if (await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.Reboot, 0, 0))
                {
                    Log("Comando REBT enviado. El dispositivo se reiniciará a modo normal.", LogLevel.Success);
                    await Task.Delay(1000); // Dar tiempo para que el dispositivo procese el comando
                    return true;
                }
                else
                {
                    Log("Error al enviar comando REBT", LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Error al reiniciar dispositivo: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Busca y establece el modo Download del dispositivo Samsung
        /// Detecta el puerto COM y verifica que el dispositivo esté en modo Download
        /// </summary>
        /// <returns>True si se detectó y configuró correctamente el modo Download</returns>
        public static async Task<(bool success, string portName)> FindAndSetDownloadModeAsync()
        {
            try
            {
                // Verificar si hay un dispositivo Samsung en modo Download
                if (!IsSamsungDownloadMode())
                {
                    return (false, null);
                }

                // Detectar el puerto COM
                string portName = DetectSamsungComPort();
                if (string.IsNullOrEmpty(portName))
                {
                    return (false, null);
                }

                // Verificar que el puerto existe y está disponible
                string[] availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(portName))
                {
                    return (false, null);
                }

                return (true, portName);
            }
            catch (Exception)
            {
                return (false, null);
            }
        }

        /// <summary>
        /// Verifica si el dispositivo está conectado y el puerto está disponible
        /// Método simple para verificar conexión sin abrir el puerto
        /// </summary>
        /// <returns>True si el puerto existe y está disponible</returns>
        public bool CheckDeviceConnected()
        {
            try
            {
                if (string.IsNullOrEmpty(_portName))
                {
                    return false;
                }

                // Verificar que el puerto existe en la lista de puertos disponibles
                string[] availablePorts = SerialPort.GetPortNames();
                return availablePorts.Contains(_portName);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verifica si el dispositivo está en modo Odin (después de inicializar comunicación)
        /// </summary>
        /// <returns>True si el dispositivo responde correctamente al handshake</returns>
        public async Task<bool> IsOdinModeAsync()
        {
            try
            {
                if (_port == null || !_port.IsOpen)
                {
                    return false;
                }

                // Intentar realizar handshake
                return await LokeProtocol.PerformHandshakeAsync(_port);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_port != null)
            {
                if (_port.IsOpen) _port.Close();
                _port.Dispose();
                _port = null;
            }
        }
    }
}
