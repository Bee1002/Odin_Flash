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
using ICSharpCode.SharpZipLib.Tar;

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
            if (OnLog != null)
            {
                OnLog.Invoke(message, level);
            }
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
        /// Procesa un archivo .tar en tiempo real, leyendo y enviando cada archivo directamente al puerto serie
        /// Evita crear archivos temporales en disco - procesamiento completamente en memoria/stream
        /// Usa TarInputStream para "enhebrar" el flujo de datos directamente al puerto serie
        /// </summary>
        /// <param name="tarFilePath">Ruta al archivo .tar de firmware</param>
        /// <returns>True si todos los archivos se procesaron exitosamente</returns>
        public async Task<bool> FlashTarFileAsync(string tarFilePath)
        {
            if (string.IsNullOrEmpty(tarFilePath) || !File.Exists(tarFilePath))
            {
                Log($"Archivo .tar no encontrado: {tarFilePath}", LogLevel.Error);
                return false;
            }

            try
            {
                Log($"Iniciando procesamiento de archivo .tar: {Path.GetFileName(tarFilePath)}", LogLevel.Info);

                // Abrir el archivo .tar y crear el TarInputStream
                using (FileStream fs = File.OpenRead(tarFilePath))
                using (TarInputStream tarStream = new TarInputStream(fs))
                {
                    TarEntry entry;
                    int fileCount = 0;
                    int successCount = 0;

                    // Procesar cada entry del archivo .tar
                    while ((entry = tarStream.GetNextEntry()) != null)
                    {
                        // Saltar directorios
                        if (entry.IsDirectory)
                            continue;

                        // Saltar archivos vacíos
                        if (entry.Size <= 0)
                            continue;

                        fileCount++;
                        string fileName = entry.Name;

                        try
                        {
                            Log($"Procesando archivo {fileCount}: {fileName} ({entry.Size / (1024.0 * 1024.0):F2} MB)", LogLevel.Info);

                            // Crear un stream wrapper que limita la lectura al tamaño del entry actual
                            // Esto asegura que solo leamos el archivo actual, no el siguiente entry
                            using (var entryStream = new TarEntryStreamWrapper(tarStream, entry.Size))
                            {
                                // Enviar el stream directamente al puerto serie sin guardar en disco
                                bool success = await FlashStreamAsync(entryStream, entry.Size, fileName);

                                if (success)
                                {
                                    successCount++;
                                    Log($"Archivo {fileName} flasheado exitosamente", LogLevel.Success);
                                }
                                else
                                {
                                    Log($"Error al flashear archivo: {fileName}", LogLevel.Error);
                                    // Continuar con el siguiente archivo en lugar de detener todo
                                }
                            }

                            // Pequeño delay entre archivos para estabilidad del dispositivo
                            await Task.Delay(500);
                        }
                        catch (Exception ex)
                        {
                            Log($"Excepción al procesar {fileName}: {ex.Message}", LogLevel.Error);
                            // Continuar con el siguiente archivo
                        }
                    }

                    Log($"Procesamiento completado: {successCount}/{fileCount} archivos flasheados",
                        successCount == fileCount ? LogLevel.Success : LogLevel.Warning);

                    return successCount == fileCount;
                }
            }
            catch (Exception ex)
            {
                Log($"Error crítico al procesar .tar: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Stream wrapper que limita la lectura al tamaño del entry del tar
        /// Asegura que solo se lea el archivo actual, no el siguiente entry
        /// </summary>
        private class TarEntryStreamWrapper : Stream
        {
            private readonly TarInputStream _tarStream;
            private readonly long _size;
            private long _position;

            public TarEntryStreamWrapper(TarInputStream tarStream, long size)
            {
                _tarStream = tarStream;
                _size = size;
                _position = 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _size;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _size)
                    return 0;

                long remaining = _size - _position;
                int bytesToRead = (int)Math.Min(count, remaining);
                int bytesRead = _tarStream.Read(buffer, offset, bytesToRead);
                _position += bytesRead;
                return bytesRead;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_position >= _size)
                    return 0;

                long remaining = _size - _position;
                int bytesToRead = (int)Math.Min(count, remaining);
                
                // TarInputStream no tiene ReadAsync nativo, usar Task.Run para no bloquear
                int bytesRead = await Task.Run(() => _tarStream.Read(buffer, offset, bytesToRead), cancellationToken);
                _position += bytesRead;
                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
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
        /// Conecta al dispositivo Samsung detectando automáticamente el puerto y realizando la secuencia de apertura
        /// Incluye limpieza de buffers y paquete de "despertar" antes del handshake ODIN
        /// Basado en el análisis de flujo de Odin: el dispositivo a veces necesita ser "limpiado" antes del comando
        /// </summary>
        /// <returns>True si la conexión fue exitosa</returns>
        public async Task<bool> ConnectDeviceAsync()
        {
            try
            {
                // 1. Detectar puerto automáticamente
                string port = GetSamsungDownloadPort();
                if (port == "Unknown")
                {
                    Log("No se pudo detectar puerto Samsung en modo Download", LogLevel.Error);
                    return false;
                }

                Log($"Puerto detectado: {port}. Iniciando conexión...", LogLevel.Info);

                // 2. Crear y abrir puerto serial
                if (_port != null && _port.IsOpen)
                {
                    _port.Close();
                    _port.Dispose();
                }

                _port = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 5000,
                    WriteTimeout = 5000
                };
                _port.Open();

                // Nota: _portName es readonly, no se puede actualizar aquí
                // El puerto se mantiene en la variable _port

                // 3. Limpiar basura del buffer
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                Log("Buffers limpiados", LogLevel.Debug);

                // 4. Enviar paquete de "Despertar" (500 bytes de ceros)
                // Esto ayuda a "despertar" el dispositivo y limpiar cualquier estado residual
                byte[] voodoo = new byte[500];
                _port.Write(voodoo, 0, 500);
                await Task.Delay(100);
                Log("Paquete de despertar enviado", LogLevel.Debug);

                // 5. Enviar Comando ODIN (Handshake real)
                byte[] odinCmd = CreateControlPacket("ODIN");
                _port.Write(odinCmd, 0, 500);
                Log("Comando ODIN enviado", LogLevel.Debug);

                // 6. Esperar respuesta LOKE o ACK
                bool ackReceived = await WaitForAckAsync();
                if (ackReceived)
                {
                    Log("Conexión establecida correctamente. Dispositivo respondió con ACK", LogLevel.Success);
                    return true;
                }
                else
                {
                    Log("Dispositivo no respondió con ACK después del comando ODIN", LogLevel.Warning);
                    // Intentar también verificar respuesta LOKE como fallback
                    return await LokeProtocol.PerformHandshakeAsync(_port);
                }
            }
            catch (Exception ex)
            {
                Log($"Error crítico en conexión: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Abre el puerto serial usando la configuración robusta extraída del análisis de Ghidra
        /// Basado en el análisis del decompiler: dwDesiredAccess, dwFlagsAndAttributes, DCB manipulation
        /// Configura DTR y RTS (crítico para que el teléfono responda) y aplica delay obligatorio de 500ms
        /// </summary>
        /// <param name="portName">Nombre del puerto COM a abrir (ej: "COM5")</param>
        /// <returns>True si el puerto se abrió correctamente</returns>
        public bool OpenPortNative(string portName)
        {
            try
            {
                // Cerrar puerto existente si está abierto
                if (_port != null && _port.IsOpen)
                {
                    _port.Close();
                    _port.Dispose();
                }

                _port = new SerialPort(portName);
                
                // Parámetros extraídos de los argumentos param_3, param_4, etc. del análisis de Ghidra
                _port.BaudRate = 115200; 
                _port.DataBits = 8;
                _port.StopBits = StopBits.One;
                _port.Parity = Parity.None;

                // LO MÁS IMPORTANTE (visto en Ghidra local_24._8_4_ | 3)
                // Sin DTR y RTS activos, el teléfono ignora cualquier comando
                _port.DtrEnable = true;
                _port.RtsEnable = true;

                // Configuración de Buffers (visto en SetupComm 0x1000 = 4096 bytes)
                _port.ReadBufferSize = 4096;
                _port.WriteBufferSize = 4096;

                // Timeouts
                _port.ReadTimeout = 5000;
                _port.WriteTimeout = 5000;

                _port.Open();

                // Limpieza inicial (visto en PurgeComm 0xf)
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                // El delay obligatorio de Odin (visto en Sleep(500))
                // Usar Thread.Sleep en lugar de Task.Delay porque este método es síncrono
                // y el delay es crítico para la estabilidad del puerto
                Thread.Sleep(500);

                Log($"Puerto {portName} abierto con configuración robusta (DTR/RTS activados)", LogLevel.Success);
                return _port.IsOpen;
            }
            catch (Exception ex)
            {
                Log($"Error abriendo puerto según especificación Loke: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Inicializa la conexión Odin imitando exactamente el comportamiento del Odin original
        /// Basado en el análisis de Ghidra: FUN_00437ef0 y FUN_00437fb0
        /// Parámetros extraídos directamente del decompiler:
        /// - 0x1C200 = 115200 baudios
        /// - 0x8 = 8 bits de datos
        /// - 0x0 = Parity.None
        /// - 0x0 = StopBits.One
        /// Incluye el Sleep(500) obligatorio después de abrir el puerto
        /// </summary>
        /// <param name="portName">Nombre del puerto COM a abrir (ej: "COM5")</param>
        /// <returns>True si la conexión se inicializó correctamente</returns>
        public async Task<bool> InitializeOdinConnection(string portName)
        {
            try
            {
                // Cerrar puerto existente si está abierto
                if (_port != null && _port.IsOpen)
                {
                    _port.Close();
                    _port.Dispose();
                }

                _port = new SerialPort(portName);

                // Parámetros extraídos de FUN_00437ef0 (análisis de Ghidra)
                _port.BaudRate = 115200;   // 0x1C200 en hexadecimal = 115200 en decimal
                _port.DataBits = 8;        // 0x8
                _port.Parity = Parity.None; // 0x0
                _port.StopBits = StopBits.One; // 0x0

                // Configuración de señales eléctricas (visto en el bitmask anterior)
                // Sin DTR y RTS activos, el teléfono ignora cualquier comando
                _port.DtrEnable = true;
                _port.RtsEnable = true;

                // Timeouts
                _port.ReadTimeout = 5000;
                _port.WriteTimeout = 5000;

                _port.Open();

                // Limpieza de buffers inicial (visto en PurgeComm)
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                // El Sleep(500) que vimos en FUN_00437fb0
                // Delay obligatorio de 500ms para estabilización del puerto
                await Task.Delay(500);

                Log($"Puerto {portName} abierto y configurado.", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error al imitar conexión Odin: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía el "latido" de handshake para confirmar que el dispositivo está presente y en modo Odin
        /// Envía el comando "ODIN" en un paquete de 500 bytes y espera la respuesta "LOKE" o ACK (0x06)
        /// Basado en el análisis de Ghidra: paquete de 0x1F4 (500) bytes con comando "ODIN" al inicio
        /// </summary>
        /// <returns>True si el handshake fue exitoso (recibió "LOKE" o ACK)</returns>
        public async Task<bool> SendOdinHandshake()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para handshake", LogLevel.Error);
                return false;
            }

            try
            {
                // 1. Preparamos el buffer de 500 bytes según Ghidra (0x1F4)
                byte[] handshakePacket = new byte[500];
                byte[] magic = System.Text.Encoding.ASCII.GetBytes("ODIN");
                
                // Copiamos "ODIN" al inicio del paquete
                Array.Copy(magic, 0, handshakePacket, 0, magic.Length);

                Log("Enviando comando de Handshake (ODIN)...", LogLevel.Info);

                // 2. Enviamos el paquete completo
                _port.Write(handshakePacket, 0, 500);

                // 3. Esperamos la respuesta del teléfono
                // El teléfono debe responder con "LOKE" (4 bytes) o un ACK (0x06)
                byte[] response = new byte[4];
                
                // Le damos un margen de 2 segundos para responder
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 2000;
                
                try
                {
                    int bytesRead = await Task.Run(() => _port.Read(response, 0, 4));

                    if (bytesRead > 0)
                    {
                        string respStr = System.Text.Encoding.ASCII.GetString(response);
                        if (respStr == "LOKE" || response[0] == 0x06)
                        {
                            Log("¡Handshake exitoso! Respuesta: LOKE detectado.", LogLevel.Success);
                            return true;
                        }
                    }
                }
                finally
                {
                    // Restaurar timeout original
                    _port.ReadTimeout = originalTimeout;
                }
                
                Log("El dispositivo no respondió al handshake correctamente.", LogLevel.Warning);
                return false;
            }
            catch (Exception ex)
            {
                Log($"Fallo en la comunicación: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Detecta y conecta al dispositivo Samsung en un solo flujo completo
        /// Integra: detección de puerto, inicialización de conexión y handshake
        /// Flujo completo basado en el análisis de Ghidra
        /// </summary>
        /// <returns>True si la detección y conexión fueron exitosas</returns>
        public async Task<bool> DetectAndConnect()
        {
            string portName = GetSamsungPort();
            if (string.IsNullOrEmpty(portName))
            {
                Log("Puerto Samsung no encontrado. ¿Están instalados los drivers?", LogLevel.Warning);
                return false;
            }

            Log($"Puerto detectado: {portName}. Inicializando...", LogLevel.Info);

            // 1. Abrir con los parámetros de Ghidra
            if (await InitializeOdinConnection(portName))
            {
                // 2. Esperar el tiempo que vimos en el binario
                await Task.Delay(500);

                // 3. Enviar el paquete "ODIN"
                return await SendOdinHandshake();
            }

            return false;
        }

        /// <summary>
        /// Motor de Handshake definitivo basado en la investigación de Ghidra
        /// Abre el puerto, configura DTR/RTS, realiza el handshake ODIN->LOKE y mantiene el puerto abierto
        /// Incluye los parámetros de DTR/RTS y el Sleep(500) que encontramos en FUN_00437fb0
        /// </summary>
        /// <param name="portName">Nombre del puerto COM a abrir y configurar</param>
        /// <returns>True si el handshake fue exitoso (recibió LOKE o ACK)</returns>
        public async Task<bool> InitializeAndHandshake(string portName)
        {
            try
            {
                // Cerrar puerto existente si está abierto
                if (_port != null && _port.IsOpen)
                {
                    _port.Close();
                    _port.Dispose();
                }

                _port = new SerialPort(portName, 115200);
                _port.DtrEnable = true; // Crucial según Ghidra
                _port.RtsEnable = true; // Crucial según Ghidra
                _port.ReadTimeout = 5000;
                _port.WriteTimeout = 5000;
                _port.Open();

                // Limpieza inicial
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                // El delay de estabilización de 500ms que vimos en Ghidra
                await Task.Delay(500);

                // Enviamos el paquete mágico "ODIN" (500 bytes)
                byte[] buffer = new byte[500];
                byte[] cmd = System.Text.Encoding.ASCII.GetBytes("ODIN");
                Array.Copy(cmd, 0, buffer, 0, cmd.Length);

                _port.Write(buffer, 0, 500);

                // Esperamos respuesta LOKE
                byte[] response = new byte[4];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 1000;
                
                try
                {
                    int read = await Task.Run(() => _port.Read(response, 0, 4));

                    if (read > 0)
                    {
                        string resp = System.Text.Encoding.ASCII.GetString(response);
                        // Si responde LOKE o ACK, mantenemos el puerto abierto
                        bool success = resp.Contains("LOKE") || response[0] == 0x06;
                        
                        if (success)
                        {
                            Log($"Handshake exitoso en {portName}. Puerto mantenido abierto.", LogLevel.Success);
                        }
                        else
                        {
                            Log($"Respuesta inesperada en {portName}: {resp}", LogLevel.Warning);
                        }
                        
                        return success;
                    }
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Log($"Error en InitializeAndHandshake: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Cierra el puerto serial y libera recursos
        /// </summary>
        public void ClosePort()
        {
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    _port.Close();
                }
                if (_port != null)
                {
                    _port.Dispose();
                    _port = null;
                }
            }
            catch (Exception ex)
            {
                Log($"Error al cerrar puerto: {ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>
        /// Escaneo agresivo de puertos COM disponibles
        /// Si el filtrado por nombre falla, prueba todos los puertos COM disponibles enviando el comando mágico ODIN
        /// Útil como método de fallback cuando la detección WMI no encuentra el dispositivo
        /// </summary>
        /// <returns>Nombre del puerto COM donde se detectó respuesta Odin, o null si no se encuentra</returns>
        public async Task<string> ScanForOdinDevice()
        {
            string[] ports = SerialPort.GetPortNames();
            foreach (string portName in ports)
            {
                Log($"Intentando Handshake en {portName}...", LogLevel.Info);
                try
                {
                    // Configuramos el puerto exactamente como vimos en Ghidra
                    using (SerialPort tempPort = new SerialPort(portName, 115200))
                    {
                        tempPort.DtrEnable = true;
                        tempPort.RtsEnable = true;
                        tempPort.ReadTimeout = 1000;
                        tempPort.Open();

                        // Pequeño delay para estabilización
                        await Task.Delay(100);

                        // Limpieza de buffers
                        tempPort.DiscardInBuffer();
                        tempPort.DiscardOutBuffer();

                        // Paquete de 500 bytes con "ODIN"
                        byte[] handshake = new byte[500];
                        byte[] magic = System.Text.Encoding.ASCII.GetBytes("ODIN");
                        Array.Copy(magic, 0, handshake, 0, magic.Length);

                        tempPort.Write(handshake, 0, 500);

                        // Esperamos respuesta LOKE o ACK (0x06)
                        byte[] response = new byte[4];
                        int read = await Task.Run(() => tempPort.Read(response, 0, 4));

                        if (read > 0 && (System.Text.Encoding.ASCII.GetString(response).Contains("LOKE") || response[0] == 0x06))
                        {
                            Log($"¡Dispositivo Odin encontrado en {portName}!", LogLevel.Success);
                            return portName; // ¡Lo encontramos!
                        }
                    }
                }
                catch
                {
                    // Puerto ocupado o sin respuesta, pasamos al siguiente
                    // No logueamos cada error para no saturar el log
                }
            }

            Log("Escaneo agresivo completado: No se encontró dispositivo Odin en ningún puerto", LogLevel.Warning);
            return null;
        }

        /// <summary>
        /// Escáner Nivel Odin - Detección de puerto con parámetros exactos de FUN_00437fb0
        /// Implementación paso a paso basada en el análisis detallado de Ghidra
        /// Incluye el retraso de 500ms y las señales de control críticas
        /// </summary>
        /// <returns>Nombre del puerto COM donde se detectó respuesta Odin, o null si no se encuentra</returns>
        public async Task<string> DetectOdinPortAsync()
        {
            string[] ports = SerialPort.GetPortNames();
            
            foreach (string portName in ports)
            {
                try
                {
                    using (SerialPort testPort = new SerialPort(portName, 115200))
                    {
                        // Configuración crítica de Ghidra (FUN_00437fb0)
                        testPort.DtrEnable = true;
                        testPort.RtsEnable = true;
                        testPort.ReadTimeout = 1500; // Un segundo y medio para que el móvil piense
                        testPort.WriteTimeout = 1000;

                        testPort.Open();
                        
                        // Paso 1: Limpiar basura
                        testPort.DiscardInBuffer();
                        testPort.DiscardOutBuffer();
                        
                        // Paso 2: El delay de "estabilización" de Odin (Ghidra Sleep(500))
                        await Task.Delay(500);

                        // Paso 3: Envío del paquete de 500 bytes (0x1F4)
                        byte[] buffer = new byte[500];
                        byte[] cmd = System.Text.Encoding.ASCII.GetBytes("ODIN");
                        Array.Copy(cmd, 0, buffer, 0, cmd.Length);

                        testPort.Write(buffer, 0, 500);

                        // Paso 4: Lectura de respuesta
                        byte[] response = new byte[4];
                        int read = await Task.Run(() => testPort.Read(response, 0, 4));
                        string responseStr = System.Text.Encoding.ASCII.GetString(response);

                        if (read > 0 && (responseStr.Contains("LOKE") || response[0] == 0x06))
                        {
                            Log($"¡Puerto Odin validado en {portName}!", LogLevel.Success);
                            return portName; // ¡Puerto validado!
                        }
                    }
                }
                catch (Exception ex)
                {
                    // El puerto está ocupado o no responde, seguimos al siguiente
                    // No logueamos cada error para no saturar el log
                    continue;
                }
            }
            
            Log("Escaneo nivel Odin completado: No se encontró dispositivo en ningún puerto", LogLevel.Warning);
            return null;
        }

        /// <summary>
        /// Handshake robusto con el dispositivo Odin
        /// Crea su propio puerto serial temporal para realizar el handshake sin afectar el puerto de la instancia
        /// Útil para verificar conectividad antes de inicializar la comunicación completa
        /// </summary>
        /// <param name="portName">Nombre del puerto COM a usar (ej: "COM5")</param>
        /// <returns>True si el handshake fue exitoso (recibió "LOKE" o ACK 0x06)</returns>
        public async Task<bool> TryOdinHandshake(string portName)
        {
            try
            {
                using (var port = new SerialPort(portName, 115200))
                {
                    port.ReadTimeout = 2000;
                    port.Open();

                    // 1. Limpieza de Buffer (Crucial para estabilidad)
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();

                    // 2. Enviar secuencia de "despertar" (500 bytes de ceros)
                    byte[] wakeUp = new byte[500];
                    port.Write(wakeUp, 0, 500);
                    await Task.Delay(100);

                    // 3. Enviar Comando "ODIN" real empaquetado
                    byte[] odinCmd = CreateControlPacket("ODIN");
                    port.Write(odinCmd, 0, 500);

                    // 4. Leer respuesta
                    byte[] response = new byte[4];
                    int read = port.Read(response, 0, 4);
                    string respStr = System.Text.Encoding.ASCII.GetString(response);

                    // El teléfono responde "LOKE" o envía un ACK (0x06)
                    return respStr == "LOKE" || response[0] == 0x06;
                }
            }
            catch
            {
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
        /// Generador de paquetes de control para el protocolo Odin
        /// Empaqueta cualquier comando en el formato de 500 bytes que entiende el bootloader
        /// </summary>
        /// <param name="command">Comando a empaquetar (máximo 4 bytes)</param>
        /// <param name="fileSize">Tamaño del archivo (para comandos DATA, opcional)</param>
        /// <returns>Array de 500 bytes con el paquete formateado</returns>
        private byte[] CreateControlPacket(string command, uint fileSize = 0)
        {
            byte[] packet = new byte[500]; // Tamaño fijo universal de Odin
            byte[] cmdBytes = System.Text.Encoding.ASCII.GetBytes(command);
            
            // El comando siempre va al inicio (Offset 0)
            Array.Copy(cmdBytes, 0, packet, 0, Math.Min(cmdBytes.Length, 4));

            // Si hay un tamaño de archivo (para comandos DATA), va en el Offset 4
            if (fileSize > 0)
            {
                byte[] sizeBytes = BitConverter.GetBytes(fileSize);
                if (BitConverter.IsLittleEndian) Array.Reverse(sizeBytes); // Samsung usa Big Endian
                Array.Copy(sizeBytes, 0, packet, 4, 4);
            }

            return packet;
        }

        /// <summary>
        /// Envía un flujo de datos al dispositivo usando el protocolo de fragmentos de 128KB.
        /// Método robusto que espera ACK después de cada fragmento para garantizar la integridad.
        /// Ideal para archivos grandes donde la robustez es más importante que la velocidad.
        /// </summary>
        /// <param name="dataStream">Stream con los datos a enviar</param>
        /// <param name="totalSize">Tamaño total del archivo en bytes</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public async Task<bool> SendDataInChunksAsync(Stream dataStream, long totalSize)
        {
            if (dataStream == null || !dataStream.CanRead)
            {
                Log("Stream no válido para envío por fragmentos", LogLevel.Error);
                return false;
            }

            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para envío por fragmentos", LogLevel.Error);
                return false;
            }

            const int CHUNK_SIZE = 131072; // 128KB - Estándar de Samsung Loke
            byte[] buffer = new byte[CHUNK_SIZE];
            long totalSent = 0;

            try
            {
                Log($"Iniciando envío robusto por fragmentos de {CHUNK_SIZE / 1024}KB (Total: {totalSize / (1024.0 * 1024.0):F2} MB)...", LogLevel.Info);

                int bytesRead;
                while ((bytesRead = await dataStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // 1. Enviar el fragmento por el puerto serie
                    _port.Write(buffer, 0, bytesRead);

                    // 2. ESPERAR ACK (0x06) - Esto es lo que hace que sea ROBUSTO
                    // El bootloader de Samsung responde 0x06 cuando el buffer está listo para más
                    if (!await WaitForAckAsync())
                    {
                        Log("Error: El dispositivo no respondió con ACK. Flasheo abortado.", LogLevel.Error);
                        return false;
                    }

                    totalSent += bytesRead;
                    OnProgress?.Invoke(totalSent, totalSize);

                    // Log cada 10MB para no saturar
                    if (totalSent % (10 * 1024 * 1024) == 0)
                    {
                        Log($"Progreso: {totalSent / (1024.0 * 1024.0):F2} MB / {totalSize / (1024.0 * 1024.0):F2} MB", LogLevel.Debug);
                    }
                }

                Log($"Transferencia por fragmentos completada: {totalSize / (1024.0 * 1024.0):F2} MB", LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Fallo crítico en la transferencia: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Espera y verifica el ACK del dispositivo (0x06)
        /// Wrapper privado que usa LokeProtocol con timeout de 5 segundos para mayor robustez
        /// </summary>
        /// <returns>True si se recibió ACK (0x06), False en caso contrario</returns>
        private async Task<bool> WaitForAckAsync()
        {
            if (_port == null || !_port.IsOpen)
                return false;

            // Usar LokeProtocol con timeout de 5 segundos para mayor robustez
            return await LokeProtocol.WaitAndVerifyAckAsync(_port, 5000);
        }

        /// <summary>
        /// Inicia una sesión de archivo enviando el comando DATA con el tamaño del archivo
        /// Según Ghidra, cada archivo empieza con un comando "DATA" (0x44415441)
        /// </summary>
        /// <param name="fileName">Nombre del archivo (para logging)</param>
        /// <param name="fileSize">Tamaño del archivo en bytes</param>
        /// <returns>True si el comando se envió correctamente y se recibió ACK</returns>
        public async Task<bool> StartFileSession(string fileName, long fileSize)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para iniciar sesión de archivo", LogLevel.Error);
                return false;
            }

            try
            {
                Log($"Iniciando sesión de archivo: {fileName} ({fileSize / (1024.0 * 1024.0):F2} MB)...", LogLevel.Info);

                // Usar CreateControlPacket para generar el paquete de control
                byte[] controlPacket = CreateControlPacket("DATA", (uint)fileSize);

                // Enviar el paquete de control completo de 500 bytes
                _port.Write(controlPacket, 0, 500);

                // Esperar y verificar ACK del dispositivo
                bool ackReceived = await WaitForAckAsync();
                
                if (ackReceived)
                {
                    Log($"Comando DATA enviado correctamente. Dispositivo listo para recibir {fileName}", LogLevel.Success);
                    return true;
                }
                else
                {
                    Log("Error: El dispositivo no respondió con ACK al comando DATA", LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Error al iniciar sesión de archivo: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía el comando de reinicio REBOOT_ODIN (0x67) siguiendo el flujo lógico completo de Ghidra
        /// Flujo basado en el análisis:
        /// 1. FUN_004379d0(param_2): Intenta abrir el puerto COM
        /// 2. Si cVar1 != '\0' (Éxito): Procede a enviar el comando 0x67
        /// 3. FUN_004324d0: Crea el paquete en memoria
        /// 4. FUN_00437a80(uVar2): Ejecuta el WriteFile del paquete creado
        /// </summary>
        /// <param name="portName">Nombre del puerto COM (opcional, si es null usa el puerto actual)</param>
        /// <returns>True si el comando se envió exitosamente</returns>
        public async Task<bool> SendRebootCommand(string portName = null)
        {
            try
            {
                // FASE 1: FUN_004379d0 - Intentar abrir/verificar el puerto COM
                bool portOpened = false;
                
                if (portName != null)
                {
                    // Si se proporciona un puerto específico, abrirlo con los parámetros de Ghidra
                    Log($"Abriendo puerto {portName} para comando de reinicio...", LogLevel.Info);
                    portOpened = await InitializeOdinConnection(portName);
                }
                else if (_port != null && _port.IsOpen)
                {
                    // Si el puerto ya está abierto, usarlo directamente
                    portOpened = true;
                    Log("Usando puerto existente para comando de reinicio", LogLevel.Info);
                }
                else
                {
                    // Intentar detectar y abrir el puerto automáticamente
                    string detectedPort = GetSamsungPort();
                    if (detectedPort != null)
                    {
                        Log($"Puerto detectado: {detectedPort}. Abriendo para comando de reinicio...", LogLevel.Info);
                        portOpened = await InitializeOdinConnection(detectedPort);
                    }
                    else
                    {
                        Log("No se pudo detectar puerto Samsung para comando de reinicio", LogLevel.Error);
                        return false;
                    }
                }

                // Verificar que el puerto se abrió correctamente (equivalente a cVar1 != '\0')
                if (!portOpened)
                {
                    Log("Error: No se pudo abrir el puerto para enviar comando de reinicio", LogLevel.Error);
                    return false;
                }

                // FASE 2: FUN_004324d0 - Crear el paquete en memoria
                // Odin usa paquetes de 500 bytes (0x1F4)
                byte[] packet = new byte[500];

                // Estructura detectada en FUN_004324d0:
                // El OpCode 0x67 suele ir en una posición específica del header
                packet[0] = 0x67; // OpCode: Reboot
                packet[1] = 0x02; // Parameter: Type 2 (visto en Ghidra)

                // El resto se rellena con 0x00 (padding) hasta los 500 bytes
                // (ya está inicializado a 0x00 por defecto en new byte[500])

                Log("Paquete REBOOT_ODIN (0x67) creado en memoria", LogLevel.Debug);

                // FASE 3: FUN_00437a80 - Ejecutar el WriteFile del paquete creado
                if (_port == null || !_port.IsOpen)
                {
                    Log("Error: Puerto no disponible para escribir paquete", LogLevel.Error);
                    return false;
                }

                _port.Write(packet, 0, 500);
                Log("Comando REBOOT_ODIN (0x67) enviado al puerto (WriteFile ejecutado).", LogLevel.Success);
                
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error al enviar comando de reinicio: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Versión simplificada del comando de reinicio que asume que el puerto ya está abierto
        /// Usa el flujo FUN_004324d0 (crear paquete) + FUN_00437a80 (WriteFile)
        /// </summary>
        public void SendRebootCommandDirect()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar comando de reinicio", LogLevel.Error);
                return;
            }

            try
            {
                // FUN_004324d0: Crear el paquete en memoria
                byte[] packet = new byte[500];
                packet[0] = 0x67; // OpCode: Reboot
                packet[1] = 0x02; // Parameter: Type 2

                // FUN_00437a80: Ejecutar WriteFile del paquete
                _port.Write(packet, 0, 500);
                Log("Comando REBOOT_ODIN (0x67) enviado al puerto.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"Error al enviar comando de reinicio: {ex.Message}", LogLevel.Error);
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
                
                // Fallback: usar método robusto de detección por VID/PID
                // Esto es más confiable que buscar todos los puertos COM
                // Crear instancia temporal ya que GetSamsungDownloadPort() es de instancia
                using (var tempEngine = new OdinEngine("COM1")) // Puerto temporal, solo para detección
                {
                    string detectedPort = tempEngine.GetSamsungDownloadPort();
                    if (detectedPort != "Unknown")
                    {
                        return detectedPort;
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
        /// Encuentra el puerto COM de Samsung buscando directamente por VID/PID en WMI
        /// Método optimizado que busca específicamente dispositivos Samsung en modo Download
        /// Versión estática que no requiere instancia de OdinEngine
        /// </summary>
        /// <returns>Nombre del puerto COM (ej: "COM5") o "Unknown" si no se encuentra</returns>
        public static string FindSamsungPort()
        {
            try
            {
                // Buscamos dispositivos que tengan el VID de Samsung (04E8) y PID de modo Download (685D o 6860)
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_04E8&PID_685D%' OR DeviceID LIKE '%VID_04E8&PID_6860%'"))
                {
                    foreach (var device in searcher.Get())
                    {
                        string caption = device["Caption"]?.ToString(); // Ejemplo: "SAMSUNG Mobile USB Serial Port (COM5)"
                        if (caption != null && caption.Contains("(COM"))
                        {
                            // Extraemos el COMxx
                            int start = caption.IndexOf("(COM") + 1;
                            int end = caption.IndexOf(")", start);
                            if (start > 0 && end > start)
                            {
                                return caption.Substring(start, end - start);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // No podemos usar Log() aquí porque es un método estático
                // El error se puede manejar en el código que llama a este método
                System.Diagnostics.Debug.WriteLine($"Error buscando puerto Samsung: {ex.Message}");
            }
            return "Unknown";
        }

        /// <summary>
        /// Encuentra el puerto COM de Samsung buscando directamente por VID/PID en WMI
        /// Versión de instancia que usa el sistema de logging de OdinEngine
        /// </summary>
        /// <returns>Nombre del puerto COM (ej: "COM5") o "Unknown" si no se encuentra</returns>
        public string FindSamsungPortWithLogging()
        {
            try
            {
                // Buscamos dispositivos que tengan el VID de Samsung (04E8) y PID de modo Download (685D o 6860)
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_04E8&PID_685D%' OR DeviceID LIKE '%VID_04E8&PID_6860%'"))
                {
                    foreach (var device in searcher.Get())
                    {
                        string caption = device["Caption"]?.ToString(); // Ejemplo: "SAMSUNG Mobile USB Serial Port (COM5)"
                        if (caption != null && caption.Contains("(COM"))
                        {
                            // Extraemos el COMxx
                            int start = caption.IndexOf("(COM") + 1;
                            int end = caption.IndexOf(")", start);
                            if (start > 0 && end > start)
                            {
                                string port = caption.Substring(start, end - start);
                                Log($"Puerto Samsung detectado: {port}", LogLevel.Success);
                                return port;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error buscando puerto: {ex.Message}", LogLevel.Error);
            }
            return "Unknown";
        }

        /// <summary>
        /// Detección robusta del puerto COM de Samsung usando VID/PID
        /// Este método ignora el "nombre" y busca directamente el ADN del hardware de Samsung
        /// Consulta todos los puertos seriales activos y filtra por VID_04E8 (Samsung) y PID_685D/6860 (modo Download)
        /// </summary>
        /// <returns>Nombre del puerto COM (ej: "COM5") o "Unknown" si no se encuentra</returns>
        public string GetSamsungDownloadPort()
        {
            try
            {
                // Consultamos todos los puertos seriales activos
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%)'"))
                {
                    var devices = searcher.Get();
                    foreach (var device in devices)
                    {
                        string deviceId = device["DeviceID"]?.ToString().ToUpper() ?? "";
                        string caption = device["Caption"]?.ToString() ?? "";

                        // VID_04E8 es el código único de Samsung
                        // PID_685D o 6860 son los códigos del modo Download/Odin
                        if (deviceId.Contains("VID_04E8") && (deviceId.Contains("PID_685D") || deviceId.Contains("PID_6860")))
                        {
                            // Extraemos el nombre del puerto COM del final del texto (ej: "COM5")
                            int start = caption.LastIndexOf("(COM") + 1;
                            int end = caption.LastIndexOf(")");
                            if (start > 0 && end > start)
                            {
                                string port = caption.Substring(start, end - start);
                                Log($"Puerto Samsung en modo Download detectado: {port}", LogLevel.Success);
                                return port;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error crítico en escaneo WMI: {ex.Message}", LogLevel.Error);
            }
            return "Unknown";
        }

        /// <summary>
        /// Detección por Hardware ID - Busca el VID_04E8 que es infalible para Samsung
        /// Versión menos estricta que solo busca por VID (Vendor ID) sin filtrar por PID específico
        /// Esto hace la detección más confiable ya que el VID_04E8 es único de Samsung
        /// </summary>
        /// <returns>Nombre del puerto COM (ej: "COM5") o null si no se encuentra</returns>
        public string GetSamsungPort()
        {
            try
            {
                // Buscamos específicamente el ID de Hardware que Odin busca en las APIs de Windows
                using (var searcher = new ManagementObjectSearcher("SELECT Caption, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_04E8%'"))
                {
                    foreach (var device in searcher.Get())
                    {
                        string caption = device["Caption"]?.ToString() ?? "";
                        if (caption.Contains("(COM"))
                        {
                            int start = caption.LastIndexOf("(COM") + 1;
                            int end = caption.LastIndexOf(")");
                            if (start > 0 && end > start)
                            {
                                return caption.Substring(start, end - start);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error WMI: {ex.Message}", LogLevel.Error);
            }
            return null;
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

                // Verificar que el puerto existe usando detección WMI
                // Usar GetSamsungDownloadPort() para verificar que el puerto detectado coincide
                // Crear instancia temporal ya que GetSamsungDownloadPort() es de instancia
                using (var tempEngine = new OdinEngine("COM1")) // Puerto temporal, solo para detección
                {
                    string detectedPort = tempEngine.GetSamsungDownloadPort();
                    if (detectedPort != "Unknown" && detectedPort == portName)
                    {
                        return (true, portName);
                    }
                }

                return (false, null);
            }
            catch (Exception)
            {
                return (false, null);
            }
        }

        /// <summary>
        /// Verifica si el dispositivo está conectado y el puerto está disponible
        /// Usa detección WMI basada en VID/PID en lugar de enumerar todos los puertos COM
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

                // Verificar que el puerto detectado por WMI coincide con el puerto actual
                string detectedPort = GetSamsungDownloadPort();
                return detectedPort != "Unknown" && detectedPort == _portName;
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
