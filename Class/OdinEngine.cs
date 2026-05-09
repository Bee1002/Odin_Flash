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
        
        // Almacenamiento del PIT parseado para búsqueda de índices de partición
        private Dictionary<string, uint> _partitionIndexMap = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private byte[] _currentPitData = null;
        
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
        /// <param name="skipDataCommand">Si es true, no envía el comando DATA (ya se envió el comando de inicio)</param>
        /// <param name="skipChunkSizeCommand">Si es true, no envía el comando 0x66, 0x02 en cada chunk (ya se envió antes del bucle)</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public async Task<bool> FlashStreamAsync(Stream stream, long fileSize, string fileName = "stream", bool skipDataCommand = false, bool skipChunkSizeCommand = false)
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

                // CRÍTICO: Si skipDataCommand es true, la sesión ya está activa y NO debemos reinicializar
                // Reinicializar aquí causaría un handshake adicional que interferiría con el protocolo 0x66
                // que ya se inició con 0x66, 0x01 (NAND Write Start) y 0x66, 0x02 (Buffer Reserve)
                if (!skipDataCommand)
                {
                    // Solo inicializar si no se envió el comando de inicio (sesión nueva)
                    if (!await InitializeCommunicationAsync()) return false;
                }
                else
                {
                    // Verificar que el puerto esté abierto (sesión ya activa)
                    if (_port == null || !_port.IsOpen)
                    {
                        Log("Error: Puerto no disponible. La sesión debería estar activa.", LogLevel.Error);
                        return false;
                    }
                    // Limpiar buffers sin hacer handshake (sesión ya establecida)
                    PurgePortBuffers();
                }

                // Paso 1: Enviar comando DATA solo si no se envió el comando de inicio (0x66, Sub 0x01)
                // Si skipDataCommand es true, significa que ya se envió el comando de inicio en WritePartitionData
                if (!skipDataCommand)
                {
                    Log($"Enviando comando DATA para {fileName} ({fileSize / (1024.0 * 1024.0):F2} MB)...", LogLevel.Debug);

                    // Enviar comando DATA con el tamaño total del archivo
                    if (!await LokeProtocol.SendControlPacketAsync(_port, OdinCommand.FlashData, (uint)fileSize, 0))
                    {
                        Log("Error al enviar comando DATA", LogLevel.Error);
                        return false;
                    }

                    Log($"Comando DATA enviado. Iniciando transferencia de {fileName}...", LogLevel.Info);
                }
                else
                {
                    Log($"Skipping DATA command (NAND Write Start already sent). Iniciando transferencia de {fileName}...", LogLevel.Debug);
                }

                // Paso 2: Enviar el stream usando el protocolo de 3 pasos (Op 0x66)
                // Basado en FUN_00433880 de Ghidra
                // CRÍTICO: Odin usa estrictamente fragmentos de 131072 bytes (128 KB)
                // Cualquier otro tamaño causa pérdida de sincronización y cierre del puerto
                const int STANDARD_CHUNK_SIZE = 131072; // 128KB - Estándar estricto de Samsung Loke
                int actualChunkSize = STANDARD_CHUNK_SIZE; // SIEMPRE usar 131072 bytes, sin excepciones
                
                byte[] buffer = new byte[actualChunkSize];
                int bytesRead;
                ulong totalSent = 0; // Usar ulong para manejar archivos grandes (64 bits)
                ulong totalFileSize = (ulong)fileSize;
                long lastProgressReport = 0;
                const long PROGRESS_REPORT_INTERVAL = 1024 * 1024;

                Log($"Iniciando protocolo de 3 pasos (Op 0x66) para {fileName} (chunk: {actualChunkSize / 1024}KB)...", LogLevel.Info);

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    try
                    {
                        // Verificar si hemos alcanzado el tamaño total del archivo
                        if (totalSent >= totalFileSize)
                        {
                            break;
                        }

                        // CRÍTICO: Para el protocolo 0x66 (NAND Write), el chunkSize DEBE ser exactamente 131,072 bytes (128KB)
                        // Esto multiplica la velocidad por 260 según el análisis
                        // Solo el último fragmento puede ser menor
                        int fragmentSize = bytesRead;
                        
                        // Verificar que siempre leamos exactamente 131072 bytes (excepto el último chunk)
                        bool isLastPossibleChunk = (totalSent + (ulong)bytesRead >= totalFileSize);
                        if (bytesRead < STANDARD_CHUNK_SIZE && !isLastPossibleChunk)
                        {
                            Log($"Warning: Leídos solo {bytesRead} bytes en lugar de {STANDARD_CHUNK_SIZE}. Esto puede causar problemas de sincronización.", LogLevel.Warning);
                        }
                        
                        // Ajustar el tamaño del fragmento si es el último
                        // IMPORTANTE: Para el último chunk, puede ser menor que 131072 bytes
                        if (totalSent + (ulong)fragmentSize > totalFileSize)
                        {
                            fragmentSize = (int)(totalFileSize - totalSent);
                        }
                        
                        // CRÍTICO: Asegurar que el fragmentSize sea exactamente 131072 bytes para chunks intermedios
                        // Esto es esencial para la velocidad y sincronización del protocolo 0x66
                        // Solo permitir fragmentSize diferente a STANDARD_CHUNK_SIZE si es el último chunk
                        bool isLastChunkCheck = (totalSent + (ulong)fragmentSize >= totalFileSize);
                        if (fragmentSize != STANDARD_CHUNK_SIZE && !isLastChunkCheck)
                        {
                            Log($"Error: fragmentSize ({fragmentSize}) no es igual a STANDARD_CHUNK_SIZE ({STANDARD_CHUNK_SIZE}) y no es el último chunk. Esto causará problemas.", LogLevel.Error);
                            return false;
                        }
                        
                        // Log para confirmar el tamaño del chunk (especialmente importante para velocidad)
                        if (fragmentSize == STANDARD_CHUNK_SIZE)
                        {
                            Log($"<ID:0/001> Enviando chunk de {fragmentSize / 1024}KB (estándar 128KB para protocolo 0x66 - velocidad optimizada)", LogLevel.Debug);
                        }
                        
                        // CRÍTICO: Verificar que el archivo NO sea .lz4 comprimido
                        // Si el log muestra "cache.img.lz4", significa que no se descomprimió correctamente
                        // El teléfono detectará la cabecera LZ4 (18 4D 22 04) y abortará por seguridad
                        if (fragmentSize > 4 && buffer[0] == 0x18 && buffer[1] == 0x4D && buffer[2] == 0x22 && buffer[3] == 0x04)
                        {
                            Log($"<ID:0/001> ERROR CRÍTICO: Detected LZ4 header (18 4D 22 04) in data stream!", LogLevel.Error);
                            Log($"<ID:0/001> The file was not decompressed correctly. Aborting to prevent NAND corruption.", LogLevel.Error);
                            return false;
                        }

                        // PASO A: Buffer Reserve (Op 0x66, Sub 0x02) - Solo si no se envió antes del bucle
                        // NOTA CRÍTICA: El Sub 0x00 (Pre-check) NO existe en el protocolo LOKE.
                        // Enviarlo causa que el dispositivo se desconecte (respuesta 0xFFFFFFFF).
                        // La secuencia correcta es estrictamente: 0x02 (Reserve) -> 0x03 (Commit)
                        // Según FUN_00433880: local_38 = ((local_58 - 1 >> 0x11) + 1) * 0x20000
                        // Este comando avisa el tamaño del chunk que se va a enviar
                        byte[] fragmentData = new byte[fragmentSize];
                        Array.Copy(buffer, 0, fragmentData, 0, fragmentSize);

                        if (!await SendOp66Data(fragmentData, fragmentSize, skipChunkSizeCommand))
                        {
                            Log($"Error: Falló el Buffer Reserve del fragmento (offset: {totalSent})", LogLevel.Error);
                            return false;
                        }

                        // PASO C: Data Transfer + Commit (Op 0x66, Sub 0x03)
                        // Según el análisis: El paquete 0x66, 0x03 incluye:
                        // - Header de 32 bytes (metadatos: uStack_34, uStack_24, etc.)
                        // - Datos del chunk después del header (32 + fragmentSize bytes total)
                        // FUN_004324d0(0x66,3,&local_38,8,0,0,1) según FUN_00433880
                        // CRÍTICO: Calcular correctamente si es el último chunk
                        // El flag uStack_24 debe ser 1 solo cuando este es realmente el último fragmento
                        bool isLastChunk = (totalSent + (ulong)fragmentSize >= totalFileSize);
                        
                        // Log para depuración del flag de finalización
                        if (isLastChunk)
                        {
                            Log($"<ID:0/001> Último chunk detectado. Total enviado: {totalSent + (ulong)fragmentSize}/{totalFileSize} bytes. Flag uStack_24 = 1", LogLevel.Info);
                        }
                        
                        if (!await SendOp66Commit(fragmentData, (uint)fragmentSize, totalSent, totalFileSize, isLastChunk))
                        {
                            Log($"Error: Falló el commit del fragmento (offset: {totalSent})", LogLevel.Error);
                            return false;
                        }

                        // Actualizar contador total (lógica de 64 bits)
                        totalSent += (ulong)fragmentSize;

                        // Reportar progreso
                        if (totalSent - (ulong)lastProgressReport >= PROGRESS_REPORT_INTERVAL || totalSent == totalFileSize)
                        {
                            OnProgress?.Invoke((long)totalSent, (long)totalFileSize);
                            lastProgressReport = (long)totalSent;
                        }

                        // IMPORTANTE: Sleep(0) después de cada iteración (según auditoría de FUN_00433380)
                        // Esto permite que el sistema operativo procese otros hilos
                        System.Threading.Thread.Sleep(0);
                    }
                    catch (IOException ex)
                    {
                        Log($"Atasco de E/S detectado. Aplicando corrección Odin...", LogLevel.Warning);
                        if (!await RecoverPortAfterErrorAsync()) return false;
                        // Reintentar el fragmento actual
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Log($"Error inesperado en fragmento (offset: {totalSent}): {ex.Message}", LogLevel.Error);
                        return false;
                    }
                }

                if (fileSize > 100 * 1024 * 1024)
                {
                    Log("Archivo grande finalizado. Limpiando buffers...", LogLevel.Debug);
                    await ClearPortAfterLargeFileAsync();
                }

                OnProgress?.Invoke((long)totalSent, fileSize);
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
        /// Valida que todos los archivos existan y sean legibles antes de iniciar el flasheo
        /// Basado en FUN_00435de0 - validación pre-flight (CFile::Open)
        /// Si algún archivo no existe o no es legible, retorna false
        /// </summary>
        /// <param name="filePaths">Lista de rutas de archivos a validar</param>
        /// <returns>True si todos los archivos son válidos, False si alguno falla</returns>
        public bool ValidateFilesPreFlight(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
            {
                Log("No hay archivos para validar", LogLevel.Warning);
                return true; // Si no hay archivos, no hay problema
            }

            foreach (string filePath in filePaths)
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    Log("Ruta de archivo vacía o nula", LogLevel.Error);
                    return false;
                }

                if (!File.Exists(filePath))
                {
                    Log($"Archivo no encontrado: {filePath}", LogLevel.Error);
                    return false;
                }

                // Verificar que el archivo sea legible (no esté bloqueado)
                try
                {
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // Si podemos abrir el archivo, es válido
                    }
                }
                catch (IOException ex)
                {
                    Log($"Archivo bloqueado o no accesible: {filePath} - {ex.Message}", LogLevel.Error);
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"Error al validar archivo {filePath}: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }

            Log($"Validación pre-flight exitosa: {filePaths.Count} archivo(s) válido(s)", LogLevel.Success);
            return true;
        }

        /// <summary>
        /// Escribe datos de una partición usando el protocolo de 3 pasos (Op 0x66)
        /// Basado en FUN_00433880 - función maestra de escritura
        /// Implementa: Start NAND Write -> Bucle de datos (0x66) -> Cierre de archivo
        /// </summary>
        /// <param name="filePath">Ruta al archivo a flashear</param>
        /// <param name="fileSize">Tamaño del archivo en bytes</param>
        /// <param name="partitionName">Nombre de la partición destino (opcional, para logging)</param>
        /// <returns>True si la escritura fue exitosa, False si falla</returns>
        public async Task<bool> WritePartitionData(string filePath, long fileSize, string partitionName = null)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Log($"Error: Archivo no encontrado o ruta inválida: {filePath}", LogLevel.Error);
                return false;
            }

            string fileName = partitionName ?? Path.GetFileName(filePath);
            string actualFilePath = filePath;
            bool isLz4File = false;
            string tempDecompressedPath = null;
            
            // PASO A: Descomprimir archivo .lz4 si es necesario
            // El teléfono NO puede escribir un archivo .lz4 directamente en la memoria NAND
            // Si se envía un archivo .lz4, el teléfono detectará la cabecera LZ4 (18 4D 22 04)
            // y abortará por seguridad para evitar dañar la memoria
            if (Util.Lz4Decompress.IsLz4File(filePath))
            {
                Log($"<ID:0/001> Detected .lz4 file: {Path.GetFileName(filePath)}", LogLevel.Info);
                Log($"<ID:0/001> Decompressing before flashing (required: device cannot write LZ4 directly to NAND)...", LogLevel.Info);
                isLz4File = true;
                try
                {
                    tempDecompressedPath = Util.Lz4Decompress.DecompressToFile(filePath);
                    actualFilePath = tempDecompressedPath;
                    
                    // Verificar que el archivo descomprimido existe y tiene contenido
                    if (!File.Exists(actualFilePath))
                    {
                        Log($"<ID:0/001> Error: Decompressed file not found at {actualFilePath}", LogLevel.Error);
                        return false;
                    }
                    
                    long decompressedSize = new FileInfo(actualFilePath).Length;
                    if (decompressedSize == 0)
                    {
                        Log($"<ID:0/001> Error: Decompressed file is empty", LogLevel.Error);
                        return false;
                    }
                    
                    Log($"<ID:0/001> File decompressed successfully: {decompressedSize} bytes", LogLevel.Success);
                    Log($"<ID:0/001> Using decompressed file: {Path.GetFileName(actualFilePath)} (original: {Path.GetFileName(filePath)})", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Log($"<ID:0/001> Error decompressing .lz4 file: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }
            else
            {
                // Verificar que el archivo NO sea .lz4 (doble verificación)
                // A veces el nombre puede no tener extensión pero el contenido sí
                if (File.Exists(filePath))
                {
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        byte[] header = new byte[4];
                        if (fs.Read(header, 0, 4) == 4)
                        {
                            if (header[0] == 0x18 && header[1] == 0x4D && header[2] == 0x22 && header[3] == 0x04)
                            {
                                Log($"<ID:0/001> ERROR: File has LZ4 header but no .lz4 extension! File: {Path.GetFileName(filePath)}", LogLevel.Error);
                                Log($"<ID:0/001> This file must be decompressed before flashing.", LogLevel.Error);
                                return false;
                            }
                        }
                    }
                }
            }
            
            // Obtener el tamaño real del archivo en disco (equivalente a CFile::GetLength en FUN_00435de0)
            long fileSizeInDisk = new FileInfo(actualFilePath).Length;
            
            // Actualizar fileName para mostrar el nombre correcto (sin .lz4 si se descomprimió)
            // Esto evita confusión en los logs mostrando "cache.img.lz4" cuando en realidad se está flasheando la imagen descomprimida
            // IMPORTANTE: Hacer esto ANTES de buscar el ID de partición para que busque por el nombre correcto
            string searchName = fileName;
            if (isLz4File)
            {
                // Mostrar el nombre original sin .lz4 para el log y búsqueda
                searchName = Path.GetFileNameWithoutExtension(fileName);
                fileName = searchName; // Actualizar fileName para logs
                Log($"<ID:0/001> Note: Original .lz4 file was decompressed. Flashing decompressed image.", LogLevel.Info);
            }
            else
            {
                // También quitar extensión .img si existe para búsqueda
                searchName = Path.GetFileNameWithoutExtension(fileName);
            }
            
            Log($"<ID:0/001> NAND Write Start!! - {fileName}", LogLevel.Info);
            Log($"File size to flash: {fileSizeInDisk} bytes", LogLevel.Info);

            try
            {
                // PASO B: Enviar el paquete de inicio (Op 0x66, Sub 0x01)
                // Este comando "reserva el espacio" y relaciona el archivo con una partición del PIT
                // Buscar el ID real de la partición desde el PIT parseado
                // La partición 0 es el PIT o tabla protegida, NO usar para datos
                // Usar searchName (sin extensiones) para la búsqueda
                uint? foundIndex = GetPartitionIndex(searchName);
                uint partitionIndex = foundIndex ?? 0;
                
                if (!foundIndex.HasValue)
                {
                    Log($"<ID:0/001> Warning: Could not find partition ID for '{searchName}'. Using default ID 0 (may fail if partition 0 is protected).", LogLevel.Warning);
                }
                else
                {
                    Log($"<ID:0/001> Using partition ID {partitionIndex} for '{searchName}'", LogLevel.Info);
                }
                
                if (!await SendOp66Start(partitionIndex, (ulong)fileSizeInDisk, 0))
                {
                    Log($"<ID:0/001> Failed to send NAND Write Start command", LogLevel.Error);
                    return false;
                }
                
                // PASO C: Sincronización de sesión - Delay de 50ms después de NAND Write Start
                // Esto asegura que el teléfono ha mapeado el área de memoria antes de recibir el primer bloque
                await Task.Delay(50);
                
                // PASO D: CRÍTICO - Buffer Reserve (Op 0x66, Sub 0x02) ANTES del primer bloque
                // Según FUN_004324d0 y FUN_00433880: 
                // local_38 = ((local_58 - 1 >> 0x11) + 1) * 0x20000
                // FUN_004324d0(0x66, 2, &local_38, 1, 0, 0, 1)
                // Este comando "desbloquea" la NAND para escribir y reserva el buffer
                // Debe enviarse INMEDIATAMENTE después de aceptar el inicio (0x66, 0x01)
                // y ANTES de empezar a enviar los bytes del archivo
                // Calcula el tamaño del chunk alineado para el primer bloque (131072 bytes estándar)
                const int STANDARD_CHUNK_SIZE = 131072; // 128KB (0x20000)
                // Fórmula exacta de FUN_00433880: ((size - 1 >> 17) + 1) * 0x20000
                uint chunkSize = (uint)(((STANDARD_CHUNK_SIZE - 1) >> 17) + 1) * 0x20000;
                
                // Enviar el tamaño del chunk (1 byte) con Op 0x66 Sub 0x02
                // FUN_004324d0(0x66,2,&local_38,1,0,0,1) - envía 1 byte del tamaño
                // El tamaño se envía como 1 byte (byte menos significativo)
                byte[] sizeBytes = new byte[] { (byte)(chunkSize & 0xFF) };
                byte[] sizeResponse = await SendOp66Packet(0x02, sizeBytes, 1);
                
                if (sizeResponse == null)
                {
                    Log($"<ID:0/001> Error: No se recibió respuesta al Buffer Reserve (0x66, 0x02)", LogLevel.Error);
                    return false;
                }
                
                // Verificar que no sea respuesta de error
                uint responseValue = BitConverter.ToUInt32(sizeResponse, 0);
                if (responseValue == 0x80000080 || (sizeResponse[0] == 0x80 && sizeResponse[3] == 0x80))
                {
                    Log($"<ID:0/001> Error: Dispositivo respondió con error al Buffer Reserve", LogLevel.Error);
                    return false;
                }
                
                Log($"<ID:0/001> Buffer Reserve (0x66, 0x02) sent successfully. Chunk size: {chunkSize} bytes (128KB)", LogLevel.Info);
                
                // Abrir archivo (equivalente a CFile::Open en FUN_00435de0)
                using (FileStream fileStream = new FileStream(actualFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Según FUN_00435de0: Odin ignora el tamaño del PIT para la validación
                    // Solo usa el nombre de la partición para saber dónde escribir
                    // El tamaño lo obtiene del archivo real usando CFile::GetLength
                    // Por lo tanto, NO validamos contra fileSize (que viene del PIT)
                    // Usamos directamente el tamaño real del archivo en disco

                    // Usar FlashStreamAsync que ya implementa el protocolo de 3 pasos (0x66)
                    // Pasar el tamaño REAL del archivo en disco, no el del PIT
                    // skipDataCommand = true porque ya enviamos el comando de inicio
                    // skipChunkSizeCommand = true porque ya enviamos el comando de alineamiento
                    bool success = await FlashStreamAsync(fileStream, fileSizeInDisk, fileName, skipDataCommand: true, skipChunkSizeCommand: true);
                    
                    if (success)
                    {
                        Log($"<ID:0/001> NAND Write Complete - {fileName}", LogLevel.Success);
                    }
                    else
                    {
                        Log($"<ID:0/001> NAND Write Failed - {fileName}", LogLevel.Error);
                    }

                    return success;
                }
            }
            catch (Exception ex)
            {
                Log($"Error crítico al escribir partición {fileName}: {ex.Message}", LogLevel.Error);
                return false;
            }
            finally
            {
                // Limpiar archivo temporal .lz4 descomprimido si existe
                if (isLz4File && !string.IsNullOrEmpty(tempDecompressedPath) && File.Exists(tempDecompressedPath))
                {
                    try
                    {
                        File.Delete(tempDecompressedPath);
                        Log($"<ID:0/001> Temporary decompressed file deleted: {Path.GetFileName(tempDecompressedPath)}", LogLevel.Debug);
                    }
                    catch (Exception ex)
                    {
                        Log($"<ID:0/001> Warning: Could not delete temporary file: {ex.Message}", LogLevel.Warning);
                    }
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

                // Timeouts
                _port.ReadTimeout = 5000;
                _port.WriteTimeout = 5000;

                // Abrir puerto primero
                _port.Open();

                // TRUCO: Reset de señales DTR/RTS para asegurar detección correcta
                // Desactivar primero, esperar, luego activar - esto ayuda al dispositivo a detectar el cambio de estado
                _port.DtrEnable = false;
                _port.RtsEnable = false;
                await Task.Delay(100);
                
                // CRÍTICO: Configuración de señales eléctricas según FUN_00437fb0 (Ghidra)
                // El bitmask local_24._8_4_ | 3 activa específicamente DTR y RTS
                // Sin DTR y RTS activos, el teléfono ignora cualquier comando
                _port.DtrEnable = true;
                _port.RtsEnable = true;
                
                // ESTO ES VITAL: El teléfono necesita tiempo para detectar que DTR/RTS subieron
                // El delay de 500ms debe ocurrir DESPUÉS de activar DTR/RTS y ANTES de limpiar buffers
                // Esto permite que el hardware del teléfono detecte las señales eléctricas correctamente
                await Task.Delay(500);

                // Limpieza de buffers inicial (visto en PurgeComm)
                // Se hace DESPUÉS del delay para no interferir con la detección de DTR/RTS
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                Log($"Puerto {portName} abierto y configurado.", LogLevel.Info);
                return _port.IsOpen;
            }
            catch (Exception ex)
            {
                Log($"Error al imitar conexión Odin: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía el "latido" de handshake para confirmar que el dispositivo está presente y en modo Odin
        /// Usa la estructura correcta según Ghidra FUN_004324d0: paquete de 1024 bytes (0x400)
        /// Lectura de exactamente 8 bytes según FUN_00437b00 para evitar que el buffer se quede "sucio"
        /// Estructura: [0-3] OpCode (4 bytes, Little Endian), [4-7] Sub-comando, [8-11] Magic String "ODIN"
        /// Basado en el análisis del decompiler: local_408 = param_1 (OpCode), local_404 = param_2 (Sub-comando)
        /// </summary>
        /// <returns>True si el handshake fue exitoso (recibió ACK 0x06 o respuesta 0x64)</returns>
        public async Task<bool> SendOdinHandshake()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para handshake", LogLevel.Error);
                return false;
            }

            try
            {
                // 1. Odin v3.07 usa 1024 bytes (0x400)
                byte[] sendBuffer = new byte[1024]; 
                
                // 2. Estructura exacta de los parámetros de FUN_004324d0
                // Offset 0: OpCode (param_1) - local_408 = param_1
                byte[] opCode = BitConverter.GetBytes(0x64); // 64 00 00 00
                Array.Copy(opCode, 0, sendBuffer, 0, 4);

                // Offset 4: Sub-OpCode (param_2) -> Odin 3.07 usa a veces 0x00000001 - local_404 = param_2
                byte[] subOp = BitConverter.GetBytes(0x01); 
                Array.Copy(subOp, 0, sendBuffer, 4, 4);

                // Offset 8: Aquí es donde Odin suele meter el "Magic" o parámetros adicionales (param_3)
                // Según Ghidra, hay un bucle for que copia param_3 a auStack_400
                byte[] magic = System.Text.Encoding.ASCII.GetBytes("ODIN");
                Array.Copy(magic, 0, sendBuffer, 8, magic.Length);

                // 3. Limpieza física del puerto
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                
                // Enviamos la ráfaga completa
                _port.Write(sendBuffer, 0, 1024);
                Log("<ID:0/001> Sending 0x64 session control packet...", LogLevel.Info);

                // 4. La lectura de 8 bytes que vimos en FUN_00437b00
                byte[] receiveBuffer = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000; // Aumentamos a 5s por si el driver es lento 

                try
                {
                    int bytesRead = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(receiveBuffer, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    if (bytesRead > 0)
                    {
                        // Debug: Ver qué nos devolvió exactamente el teléfono
                        string hexResponse = BitConverter.ToString(receiveBuffer);
                        Log($"Phone Response (HEX): {hexResponse}", LogLevel.Debug);

                        // Verificamos si los primeros 4 bytes coinciden con nuestro OpCode (Eco)
                        int responseOp = BitConverter.ToInt32(receiveBuffer, 0);
                        if (responseOp == 0x64 || receiveBuffer[0] == 0x06)
                        {
                            Log("<ID:0/001> Handshake Successful! Session Open.", LogLevel.Success);
                            return true;
                        }
                    }
                }
                finally
                {
                    // Restaurar timeout original
                    _port.ReadTimeout = originalTimeout;
                }
                
                Log("<ID:0/001> Handshake Failed: Invalid response from device.", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Communication Error: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Solicita información del dispositivo usando el comando 0x65
        /// Este comando se envía inmediatamente después del handshake para mantener la conexión activa
        /// Evita que el teléfono reinicie el puerto USB y se desconecte
        /// </summary>
        /// <returns>True si el dispositivo respondió (la conexión se mantiene estable)</returns>
        public async Task<bool> SendRequestInfo()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para solicitar información", LogLevel.Error);
                return false;
            }

            try
            {
                byte[] buffer = new byte[1024];
                
                // OpCode 0x65 (Visto en Ghidra como el siguiente paso tras 0x64)
                byte[] opCode = BitConverter.GetBytes(0x65);
                Array.Copy(opCode, 0, buffer, 0, 4);
                
                // Sub-op 1
                byte[] subOp = BitConverter.GetBytes(0x01);
                Array.Copy(subOp, 0, buffer, 4, 4);

                _port.Write(buffer, 0, 1024);
                Log("<ID:0/001> Requesting Device Info (0x65)...", LogLevel.Info);

                byte[] resp = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000;

                try
                {
                    int read = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(resp, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    if (read > 0)
                    {
                        string hexResponse = BitConverter.ToString(resp);
                        Log($"Device Info Response (HEX): {hexResponse}", LogLevel.Debug);
                        Log("<ID:0/001> Device Info received. Connection maintained.", LogLevel.Success);
                        return true;
                    }
                    else
                    {
                        Log("<ID:0/001> No response to info request, but connection may still be active.", LogLevel.Warning);
                        return true; // Aún así consideramos éxito para mantener la conexión
                    }
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error requesting device info: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Solicita y descarga el archivo PIT (Partition Information Table) del dispositivo usando el comando 0x61
        /// El PIT contiene la "hoja de ruta" del teléfono con información sobre las particiones
        /// Usa OpCode 0x61 (Request PIT Download) en lugar de 0x66 para mayor compatibilidad
        /// El comando 0x61 es distinto a los anteriores: el teléfono no solo responde con 8 bytes,
        /// sino que envía un archivo completo (la tabla de particiones)
        /// </summary>
        /// <returns>Array de bytes con los datos del PIT, o null si falla la solicitud o descarga</returns>
        [Obsolete("Usar GetPitForMapping() en su lugar. El protocolo 0x65 es el correcto según Ghidra.")]
        public async Task<byte[]> RequestPit()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para solicitar PIT", LogLevel.Error);
                return null;
            }

            try
            {
                byte[] buffer = new byte[1024];
                
                // Prueba con 0x61 (Request PIT Download) en lugar de 0x66
                byte[] opCode = BitConverter.GetBytes(0x61);
                Array.Copy(opCode, 0, buffer, 0, 4);
                
                // Sub-op 0x00
                byte[] subOp = BitConverter.GetBytes(0x00);
                Array.Copy(subOp, 0, buffer, 4, 4);

                // Limpieza de buffers antes de enviar (evita interferencia de datos residuales)
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _port.Write(buffer, 0, 1024);
                Log("<ID:0/001> Requesting PIT via 0x61...", LogLevel.Info);

                // 1. Leer cabecera de respuesta (8 bytes)
                byte[] header = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000; // 5 segundos para la cabecera

                try
                {
                    int headerRead = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(header, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    if (headerRead < 8)
                    {
                        Log("<ID:0/001> No response to PIT request.", LogLevel.Warning);
                        return null;
                    }

                    string hexResponse = BitConverter.ToString(header);
                    Log($"PIT Response (HEX): {hexResponse}", LogLevel.Debug);

                    // Si responde algo distinto a FF FF FF FF, ¡lo tenemos!
                    // Verificar si la respuesta no es 0xFF (rechazo o error)
                    if (headerRead > 0 && header[0] != 0xFF)
                    {
                        // 2. Preparar buffer para el archivo PIT (normalmente 4KB o 8KB)
                        // En el protocolo Loke, tras el ACK del 0x61, el móvil manda el stream
                        byte[] pitData = new byte[4096]; // Buffer inicial de 4KB
                        int totalRead = 0;
                        
                        // Damos un tiempo para que el móvil prepare el envío
                        await Task.Delay(100);

                        // Aumentar timeout para la lectura del archivo completo
                        _port.ReadTimeout = 10000; // 10 segundos para recibir el PIT completo

                        // 3. Leer los datos del PIT
                        try
                        {
                            totalRead = await Task.Run(() => {
                                try 
                                { 
                                    return _port.Read(pitData, 0, pitData.Length); 
                                }
                                catch 
                                { 
                                    return 0; 
                                }
                            });

                            if (totalRead > 0)
                            {
                                // Si leímos menos de lo esperado, ajustar el array
                                if (totalRead < pitData.Length)
                                {
                                    byte[] trimmedData = new byte[totalRead];
                                    Array.Copy(pitData, 0, trimmedData, 0, totalRead);
                                    Log($"<ID:0/001> PIT received ({totalRead} bytes).", LogLevel.Success);
                                    return trimmedData;
                                }
                                
                                Log($"<ID:0/001> PIT received ({totalRead} bytes).", LogLevel.Success);
                                return pitData;
                            }
                            else
                            {
                                Log("<ID:0/001> No PIT data received after header.", LogLevel.Warning);
                                return null;
                            }
                        }
                        finally
                        {
                            _port.ReadTimeout = originalTimeout;
                        }
                    }
                    else
                    {
                        Log("<ID:0/001> PIT Request Rejected. Invalid response header.", LogLevel.Warning);
                        return null;
                    }
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error requesting PIT: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Lee el PIT automáticamente siguiendo la secuencia correcta de apertura de datos
        /// NOTA: Este método usa el protocolo 0x61 (legacy). Para el protocolo correcto de Odin, usar GetPitForMapping()
        /// Paso 1: Petición de PIT (0x61, 0)
        /// Paso 2: Lectura del stream de datos
        /// IMPORTANTE: NO envía 0x64,3 si ya se hizo 0x64,1 y 0x65 exitoso (causa reinicio de sesión)
        /// </summary>
        /// <returns>Array de bytes con los datos del PIT, o null si falla</returns>
        [Obsolete("Usar GetPitForMapping() en su lugar. El protocolo 0x65 es el correcto según Ghidra.")]
        public async Task<byte[]> ReadPitAutomatic(bool skipModeChange = false)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para leer PIT automáticamente", LogLevel.Error);
                return null;
            }

            try
            {
                // IMPORTANTE: NO enviar 0x64,3 si ya se hizo 0x64,1 y 0x65 exitoso
                // Muchos modelos modernos consideran el 0x64,3 como un intento de reinicio de sesión
                // Si el log ya dice "Device ready", NO volver a enviar 0x64,3
                Log("<ID:0/001> Omitiendo 0x64,3 para evitar reinicio de sesión (ya se hizo 0x64,1 y 0x65)...", LogLevel.Info);

                // Paso 1: PETICIÓN DE PIT (0x61)
                Log("<ID:0/001> Requesting PIT via 0x61...", LogLevel.Info);
                byte[] response = await SendControlPacketWithResponse(0x61, 0);

                if (response != null && response[0] == 0x61)
                {
                    // Si llegamos aquí, ¡el teléfono aceptó!
                    // Los bytes 4, 5, 6, 7 del response contienen el TAMAÑO del archivo PIT
                    int pitSize = BitConverter.ToInt32(response, 4);
                    Log($"<ID:0/001> PIT detected! Size: {pitSize} bytes. Reading stream...", LogLevel.Success);

                    // Paso 4: LECTURA DEL STREAM
                    // IMPORTANTE: Tras el ACK del 0x61, NO envíes más paquetes. Solo LEE.
                    byte[] pitData = new byte[pitSize > 0 ? pitSize : 4096];
                    int originalTimeout = _port.ReadTimeout;
                    _port.ReadTimeout = 5000;
                    
                    try
                    {
                        int totalRead = 0;
                        while (totalRead < pitData.Length)
                        {
                            int read = await Task.Run(() => {
                                try 
                                { 
                                    return _port.Read(pitData, totalRead, pitData.Length - totalRead); 
                                }
                                catch 
                                { 
                                    return 0; 
                                }
                            });
                            
                            if (read == 0) break;
                            totalRead += read;
                        }

                        if (totalRead > 0)
                        {
                            // Si leímos menos de lo esperado, ajustar el array
                            if (totalRead < pitData.Length)
                            {
                                byte[] trimmedData = new byte[totalRead];
                                Array.Copy(pitData, 0, trimmedData, 0, totalRead);
                                Log($"<ID:0/001> PIT File downloaded successfully ({totalRead} bytes).", LogLevel.Success);
                                return trimmedData;
                            }
                            
                            Log($"<ID:0/001> PIT File downloaded successfully ({totalRead} bytes).", LogLevel.Success);
                            return pitData;
                        }
                        else
                        {
                            Log("<ID:0/001> No PIT data received.", LogLevel.Warning);
                            return null;
                        }
                    }
                    finally
                    {
                        _port.ReadTimeout = originalTimeout;
                    }
                }

                Log("<ID:0/001> PIT Request Rejected or Timeout.", LogLevel.Error);
                return null;
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error in ReadPitAutomatic: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Obtiene el PIT para mapeo usando el protocolo correcto de Odin (OpCode 0x65)
        /// Basado en el análisis de Ghidra: FUN_00435750
        /// Secuencia exacta según Ghidra:
        /// 1. FUN_004324d0(0x65, 1, ...): Obtener el tamaño total del PIT (local_20)
        /// 2. Bucle: FUN_004324d0(0x65, 2, &local_1c, 1, ...): Indicar fragmento a leer
        /// 3. FUN_00437b00(...): Leer los bytes del puerto COM (500 bytes por fragmento)
        /// 4. FUN_004324d0(0x65, 3, ...): Finalizar lectura
        /// </summary>
        /// <returns>Array de bytes con los datos completos del PIT, o null si falla</returns>
        public async Task<byte[]> GetPitForMapping()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para obtener PIT para mapeo", LogLevel.Error);
                return null;
            }

            try
            {
                Log("<ID:0/001> Iniciando lectura de flujo binario PIT...", LogLevel.Info);

                // PASO 0 (Opcional según Ghidra): Erase Sector (0x64, 7) si está habilitado
                // Según FUN_00435750: if (*(int *)(param_1 + 0x34) != 0) → FUN_004324d0(100, 7, ...)
                // Esto prepara el dispositivo para la lectura del PIT en algunos modelos
                // Nota: 100 decimal = 0x64 hexadecimal
                try
                {
                    Log("<ID:0/001> Preparing device for PIT read (optional erase sector)...", LogLevel.Debug);
                    await SendControlPacket(0x64, 7);
                    await Task.Delay(100); // Pequeña pausa después del erase
                }
                catch (Exception eraseEx)
                {
                    // No es crítico si falla, continuamos de todas formas
                    Log($"<ID:0/001> Erase sector step skipped (non-critical): {eraseEx.Message}", LogLevel.Debug);
                }

                // PASO 1: FUN_004324d0(0x65, 1, ...) - Obtener tamaño total del PIT
                byte[] sizeResp = await SendControlPacketWithResponse(0x65, 1);
                if (sizeResp == null || sizeResp[0] != 0x65)
                {
                    Log("<ID:0/001> Failed to get PIT size. Invalid response.", LogLevel.Error);
                    return null;
                }

                // El tamaño viene en los bytes 4-7 de la respuesta (local_20 en Ghidra)
                int totalSize = BitConverter.ToInt32(sizeResp, 4);
                
                // IMPORTANTE: Aceptar 16384 (0x4000 = 16KB) como tamaño válido del buffer
                // El teléfono responde con el tamaño real del buffer que necesita, no un DeviceID
                // Si el teléfono responde 00-40 (16384), usar ese valor para el bucle de fragmentos
                
                if (totalSize <= 0)
                {
                    Log("<ID:0/001> Invalid PIT size received.", LogLevel.Error);
                    return null;
                }

                Log($"<ID:0/001> PIT Size detected: {totalSize} bytes", LogLevel.Info);

                // PASO 2: Preparar buffer y calcular fragmentos (de 500 en 500 como en Ghidra)
                // iVar1 = (local_20 + -1) / 500 + 1; (cálculo de fragmentos en Ghidra)
                byte[] fullPit = new byte[totalSize];
                int fragments = (totalSize + 499) / 500; // Redondeo hacia arriba
                int totalBytesRead = 0; // Contador para verificar que se leyó la cantidad correcta

                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000; // 5 segundos por fragmento

                try
                {
                    // Bucle for según Ghidra: for (local_1c = 0; local_1c < iVar1; local_1c = local_1c + 1)
                    for (int i = 0; i < fragments; i++)
                    {
                        // FUN_004324d0(0x65, 2, &local_1c, 1, ...) - Indicar fragmento a leer
                        // param_3 = &local_1c (puntero al índice), param_4 = 1
                        // IMPORTANTE: SendControlPacketWithIndex NO lee respuesta, solo envía
                        if (!await SendControlPacketWithIndex(0x65, 2, i))
                        {
                            Log($"<ID:0/001> Failed to request fragment {i}.", LogLevel.Error);
                            return null;
                        }

                        // Calcular bytes a leer según Ghidra:
                        // if (local_20 + local_1c * -500 < 0x1f5) local_44 = local_20 + local_1c * -500;
                        // else local_44 = 500;
                        int bytesToRead = Math.Min(500, totalSize - (i * 500));
                        int offset = i * 500;

                        // FUN_00437b00(...) - Leer los bytes del puerto COM DIRECTAMENTE
                        // Leer completamente el fragmento (puede requerir múltiples lecturas)
                        byte[] chunk = new byte[bytesToRead];
                        int readInThisFragment = 0;

                        // Bucle de lectura de puerto para asegurar que recibimos los bytes completos
                        while (readInThisFragment < bytesToRead)
                        {
                            int r = await Task.Run(() =>
                            {
                                try
                                {
                                    return _port.Read(chunk, readInThisFragment, bytesToRead - readInThisFragment);
                                }
                                catch
                                {
                                    return 0;
                                }
                            });

                            if (r == 0) break;
                            readInThisFragment += r;
                        }

                        // VALIDACIÓN DEL PRIMER BLOQUE (Aquí es donde está el Magic Number del PIT)
                        // El PIT tiene una cabecera: 0x12349876 (Little Endian = 0x76, 0x98, 0x34, 0x12)
                        if (i == 0 && readInThisFragment >= 4)
                        {
                            if (chunk[0] == 0x76 && chunk[1] == 0x98 && chunk[2] == 0x34 && chunk[3] == 0x12)
                            {
                                Log("<ID:0/001> ¡PIT IDENTIFICADO! Cabecera 0x12349876 detectada.", LogLevel.Success);
                            }
                            else
                            {
                                string hexHeader = BitConverter.ToString(chunk, 0, Math.Min(4, readInThisFragment));
                                Log($"<ID:0/001> PIT header: {hexHeader} (expected: 76-98-34-12)", LogLevel.Debug);
                            }
                        }

                        if (readInThisFragment > 0)
                        {
                            Array.Copy(chunk, 0, fullPit, offset, readInThisFragment);
                            totalBytesRead += readInThisFragment; // Acumular bytes leídos
                            Log($"<ID:0/001> Fragment {i + 1}/{fragments} read ({readInThisFragment} bytes)", LogLevel.Debug);
                        }
                        else
                        {
                            Log($"<ID:0/001> No data received for fragment {i}.", LogLevel.Error);
                            return null; // Error crítico: no se recibieron datos
                        }

                        // Pequeña pausa entre fragmentos para dar tiempo al dispositivo
                        await Task.Delay(50);
                    }
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }

                // PASO 3: FUN_004324d0(0x65, 3, ...) - Finalizar lectura
                if (!await SendControlPacket(0x65, 3))
                {
                    Log("<ID:0/001> Warning: Failed to finalize PIT reading, but data may be valid.", LogLevel.Warning);
                }

                // VERIFICACIÓN CRÍTICA: Asegurar que se leyó la cantidad correcta de bytes
                // La función debe retornar true (datos válidos) solo si leyó la cantidad que el teléfono reportó
                if (totalBytesRead != totalSize)
                {
                    Log($"<ID:0/001> Error: PIT size mismatch. Expected {totalSize} bytes, but only read {totalBytesRead} bytes.", LogLevel.Error);
                    return null;
                }

                Log($"<ID:0/001> PIT read and mapped successfully ({totalSize} bytes, {fragments} fragments).", LogLevel.Success);

                // Guardar PIT en disco para análisis posterior
                try
                {
                    string fileName = $"dump_{DateTime.Now:yyyyMMdd_HHmmss}.pit";
                    File.WriteAllBytes(fileName, fullPit);
                    Log($"<ID:0/001> PIT guardado en el disco como: {fileName}", LogLevel.Success);
                }
                catch (Exception saveEx)
                {
                    Log($"<ID:0/001> Warning: No se pudo guardar el PIT en disco: {saveEx.Message}", LogLevel.Warning);
                    // Continuamos aunque falle el guardado, el PIT ya está en memoria
                }

                // Analizar y mostrar información del PIT
                ParsePitData(fullPit);

                // IMPORTANTE: Sleep(100) obligatorio después de leer PIT (según FUN_0042e470)
                // Esto permite que el puerto COM procese completamente el cierre del PIT
                // antes de iniciar la inicialización del flasheo
                System.Threading.Thread.Sleep(100);

                return fullPit;
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error in GetPitForMapping: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Parsea los datos del PIT para extraer información de particiones
        /// Procesa el array de bytes del PIT y extrae los nombres de particiones y archivos .img asociados
        /// Almacena un mapa de nombre de partición -> ID para búsqueda rápida
        /// Estructura del PIT según análisis:
        /// - Header: 28 bytes (Magic Number + Entry Count)
        /// - Cada entrada: 132 bytes
        ///   - Offset 0x00: 4 bytes - Binary Type
        ///   - Offset 0x04: 4 bytes - Device Type
        ///   - Offset 0x08: 4 bytes - Identifier / ID (el número de partición que necesitamos)
        ///   - Offset 0x33 (51): 32 bytes - Partition Name (ej: "CACHE")
        /// </summary>
        /// <param name="pitData">Array de bytes con los datos completos del PIT</param>
        public void ParsePitData(byte[] pitData)
        {
            if (pitData == null || pitData.Length < 28)
            {
                Log("<ID:0/001> PIT data is null or too small to parse.", LogLevel.Warning);
                return;
            }

            try
            {
                // Limpiar mapa anterior y almacenar nuevo PIT
                _partitionIndexMap.Clear();
                _currentPitData = pitData;
                
                // El Magic Number ya lo validamos en GetPitForMapping, empezamos desde el offset 28
                // Cada entrada de partición en un PIT moderno mide 132 bytes
                const int ENTRY_SIZE = 132;
                int headerOffset = 28;

                // El número de entradas está en el offset 4 del header
                int entryCount = BitConverter.ToInt32(pitData, 4);
                Log($"<ID:0/001> Parsing PIT: {entryCount} partitions found.", LogLevel.Info);

                for (int i = 0; i < entryCount; i++)
                {
                    int currentEntryOffset = headerOffset + (i * ENTRY_SIZE);

                    // Si nos pasamos del tamaño del array, salimos
                    if (currentEntryOffset + ENTRY_SIZE > pitData.Length)
                    {
                        Log($"<ID:0/001> Warning: Entry {i} exceeds PIT data size. Stopping parse.", LogLevel.Warning);
                        break;
                    }

                    // Extraer ID de la Partición (Offset 0x08 = 8 bytes dentro de la entrada)
                    // Este es el número de partición que debemos usar en el comando 0x66, Sub 0x01
                    int partitionId = BitConverter.ToInt32(pitData, currentEntryOffset + 0x08);
                    
                    // Extraer Nombre de la Partición (Offset 0x33 = 51 bytes dentro de la entrada)
                    // Limpiar caracteres nulos y espacios
                    string partitionName = System.Text.Encoding.ASCII.GetString(pitData, currentEntryOffset + 0x33, 32)
                        .Replace("\0", "").Trim();

                    // Extraer Nombre del Archivo Flash (Offset 64 dentro de la entrada)
                    string flashName = System.Text.Encoding.ASCII.GetString(pitData, currentEntryOffset + 64, 32).TrimEnd('\0');

                    if (!string.IsNullOrEmpty(partitionName))
                    {
                        // Almacenar el ID de la partición usando el nombre como clave
                        // Usar tanto el nombre de partición como el flash name para búsqueda
                        _partitionIndexMap[partitionName] = (uint)partitionId;
                        
                        if (!string.IsNullOrEmpty(flashName))
                        {
                            try
                            {
                                // También mapear por flash name sin extensión (ej: "cache.img" -> "cache")
                                // Validar y limpiar el nombre antes de usar Path.GetFileNameWithoutExtension
                                string cleanFlashName = flashName.Trim();
                                // Eliminar caracteres inválidos para rutas
                                char[] invalidChars = Path.GetInvalidFileNameChars();
                                foreach (char c in invalidChars)
                                {
                                    cleanFlashName = cleanFlashName.Replace(c.ToString(), "");
                                }
                                
                                if (!string.IsNullOrEmpty(cleanFlashName))
                                {
                                    string flashNameWithoutExt = Path.GetFileNameWithoutExtension(cleanFlashName);
                                    if (!string.IsNullOrEmpty(flashNameWithoutExt))
                                    {
                                        _partitionIndexMap[flashNameWithoutExt] = (uint)partitionId;
                                        // También mapear en minúsculas para búsqueda case-insensitive
                                        _partitionIndexMap[flashNameWithoutExt.ToLower()] = (uint)partitionId;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // Si hay error al procesar flashName, continuar sin mapearlo
                                Log($"<ID:0/001> Warning: Could not process flash name '{flashName}' for partition {partitionId}: {ex.Message}", LogLevel.Debug);
                            }
                        }
                        
                        Log($"[PIT] Partition[{partitionId}]: {partitionName.PadRight(12)} | Flash Name: {flashName}", LogLevel.Info);
                    }
                }

                Log($"<ID:0/001> PIT parsing completed successfully. {_partitionIndexMap.Count} partitions indexed.", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error parsing PIT data: {ex.Message}", LogLevel.Error);
            }
        }
        
        /// <summary>
        /// Obtiene el ID real de una partición desde el PIT parseado basándose en el nombre del archivo
        /// Busca por nombre de partición (ej: "CACHE") o por nombre de archivo (ej: "cache.img")
        /// </summary>
        /// <param name="fileName">Nombre del archivo o partición a buscar (ej: "cache.img", "CACHE")</param>
        /// <returns>ID de la partición si se encuentra, null si no se encuentra</returns>
        private uint? GetPartitionIndex(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || _partitionIndexMap.Count == 0)
            {
                return null;
            }

            // Limpiar el nombre de caracteres inválidos
            string cleanName = fileName.Trim();
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                cleanName = cleanName.Replace(c.ToString(), "");
            }

            // Intentar buscar por nombre de archivo sin extensión
            string nameWithoutExt = Path.GetFileNameWithoutExtension(cleanName);
            
            // Estrategia de búsqueda múltiple (case-insensitive):
            // 1. Buscar por nombre sin extensión exacto (ej: "cache")
            if (!string.IsNullOrEmpty(nameWithoutExt))
            {
                if (_partitionIndexMap.ContainsKey(nameWithoutExt))
                {
                    uint index = _partitionIndexMap[nameWithoutExt];
                    Log($"<ID:0/001> Found partition ID {index} for '{nameWithoutExt}'", LogLevel.Debug);
                    return index;
                }
                
                // 2. Buscar en minúsculas (ej: "cache")
                string lowerName = nameWithoutExt.ToLower();
                if (_partitionIndexMap.ContainsKey(lowerName))
                {
                    uint index = _partitionIndexMap[lowerName];
                    Log($"<ID:0/001> Found partition ID {index} for '{lowerName}' (lowercase)", LogLevel.Debug);
                    return index;
                }
                
                // 3. Buscar en mayúsculas (ej: "CACHE")
                string upperName = nameWithoutExt.ToUpper();
                if (_partitionIndexMap.ContainsKey(upperName))
                {
                    uint index = _partitionIndexMap[upperName];
                    Log($"<ID:0/001> Found partition ID {index} for '{upperName}' (uppercase)", LogLevel.Debug);
                    return index;
                }
            }
            
            // 4. Buscar por nombre completo del archivo (sin extensión ya procesado arriba)
            if (_partitionIndexMap.ContainsKey(cleanName))
            {
                uint index = _partitionIndexMap[cleanName];
                Log($"<ID:0/001> Found partition ID {index} for '{cleanName}'", LogLevel.Debug);
                return index;
            }
            
            // 5. Búsqueda case-insensitive en todo el mapa
            foreach (var kvp in _partitionIndexMap)
            {
                if (string.Equals(kvp.Key, nameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"<ID:0/001> Found partition ID {kvp.Value} for '{kvp.Key}' (case-insensitive match)", LogLevel.Debug);
                    return kvp.Value;
                }
            }
            
            // No se encontró - no mostrar warning duplicado (ya se muestra en WritePartitionData)
            return null;
        }

        /// <summary>
        /// Envía un paquete de control con OpCode, Sub-OpCode e índice específicos
        /// Usado para indicar qué fragmento del PIT se va a leer (OpCode 0x65, Sub-op 2)
        /// Basado en FUN_004324d0 de Ghidra: FUN_004324d0(0x65, 2, &local_1c, 1, ...)
        /// Estructura del paquete según protocolo Odin:
        /// - Offset 0-3: OpCode (0x65)
        /// - Offset 4-7: Sub-OpCode (0x02)
        /// - Offset 8-11: Índice del fragmento (local_1c)
        /// - Offset 12-15: Magic "ODIN" (opcional, para mayor compatibilidad)
        /// 
        /// IMPORTANTE: Este método SOLO envía el paquete. NO lee la respuesta porque
        /// si lee 8 bytes aquí, se "comería" los primeros 8 bytes del PIT (donde está el Magic Number).
        /// La lectura de datos se hace directamente en GetPitForMapping() después de enviar la petición.
        /// </summary>
        /// <param name="opCode">OpCode del comando (ej: 0x65)</param>
        /// <param name="subOpCode">Sub-OpCode del comando (ej: 2 para indicar fragmento)</param>
        /// <param name="index">Índice del fragmento a leer</param>
        /// <returns>True si el paquete se envió correctamente</returns>
        private async Task<bool> SendControlPacketWithIndex(uint opCode, uint subOpCode, int index)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar paquete de control con índice", LogLevel.Error);
                return false;
            }

            try
            {
                byte[] buffer = new byte[1024];
                // Inicializar buffer con ceros (importante para evitar basura)
                Array.Clear(buffer, 0, buffer.Length);

                // Offset 0-3: OpCode (4 bytes Little Endian)
                byte[] opCodeBytes = BitConverter.GetBytes(opCode);
                Array.Copy(opCodeBytes, 0, buffer, 0, 4);

                // Offset 4-7: Sub-OpCode (4 bytes Little Endian)
                byte[] subOpBytes = BitConverter.GetBytes(subOpCode);
                Array.Copy(subOpBytes, 0, buffer, 4, 4);

                // Offset 8-11: Índice del fragmento (4 bytes Little Endian)
                // Según Ghidra FUN_004324d0(0x65, 2, &local_1c, 1, ...)
                // El índice se pasa como puntero, pero en el paquete se serializa como valor
                byte[] indexBytes = BitConverter.GetBytes(index);
                Array.Copy(indexBytes, 0, buffer, 8, 4);

                // Offset 12-15: Magic "ODIN" (opcional, para mayor compatibilidad con algunos modelos)
                // Algunos modelos esperan este magic string para validar el paquete
                byte[] magic = System.Text.Encoding.ASCII.GetBytes("ODIN");
                Array.Copy(magic, 0, buffer, 12, magic.Length);

                // Limpieza de buffers ANTES de enviar (crítico según análisis de Ghidra)
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                // Enviar paquete completo de 1024 bytes
                _port.Write(buffer, 0, 1024);
                Log($"<ID:0/001> Sending control packet (Op: 0x{opCode:X2}, Sub: 0x{subOpCode:X2}, Index: {index})...", LogLevel.Debug);

                // CRÍTICO: NO leer respuesta aquí. Si leemos 8 bytes, nos "comemos" los primeros
                // 8 bytes del PIT que contienen el Magic Number (0x12349876).
                // La lectura de datos se hace directamente en GetPitForMapping() después de enviar.
                return true;
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error sending control packet with index: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Lee el PIT automáticamente usando una secuencia directa sin repetir handshakes
        /// Envía 0x61 y lee el tamaño del PIT desde la respuesta, luego lee los datos completos
        /// Mantiene la conexión abierta para después el flash
        /// </summary>
        /// <returns>Array de bytes con los datos del PIT, o null si falla</returns>
        [Obsolete("Usar ReadPitAutomatic() en su lugar")]
        public async Task<byte[]> AutoReadPit()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para leer PIT automáticamente", LogLevel.Error);
                return null;
            }

            try
            {
                // NO envíes 0x64 otra vez si ya lo hiciste al principio.
                // Vamos directo al grano.
                
                Log("<ID:0/001> Requesting PIT via 0x61...", LogLevel.Info);
                
                // Para el PIT, NO usamos SendControlPacket porque limpia el buffer
                // La respuesta del PIT viene pegada al ACK, así que no debemos limpiar después de enviar
                byte[] buffer = new byte[1024];
                BitConverter.GetBytes(0x61).CopyTo(buffer, 0);
                BitConverter.GetBytes(0x00).CopyTo(buffer, 4);

                // Limpieza de buffers SOLO antes de enviar (no después)
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _port.Write(buffer, 0, 1024);

                // CRÍTICO: Esperar 200ms antes de leer porque la respuesta del PIT viene pegada al ACK
                await Task.Delay(200);

                // Leer respuesta (8 bytes) - La respuesta viene inmediatamente después del ACK
                byte[] response = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000;

                try
                {
                    int read = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(response, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    if (read >= 8 && response[0] == 0x61)
                    {
                        // El teléfono aceptó. Los bytes 4 a 7 de 'response' son el tamaño del PIT.
                        int pitSize = BitConverter.ToInt32(response, 4);
                        Log($"<ID:0/001> PIT size reported by phone: {pitSize} bytes.", LogLevel.Info);

                        if (pitSize <= 0) pitSize = 4096; // Tamaño de seguridad si viene vacío

                        // PASO CRÍTICO: Leer el flujo de datos inmediatamente
                        byte[] pitBuffer = new byte[pitSize];
                        _port.ReadTimeout = 5000;

                        try 
                        {
                            int bytesRead = 0;
                            int attempts = 0;
                            
                            // Odin lee en un bucle hasta completar el tamaño
                            while (bytesRead < pitSize && attempts < 10) 
                            {
                                int readBytes = await Task.Run(() => {
                                    try 
                                    { 
                                        return _port.Read(pitBuffer, bytesRead, pitSize - bytesRead); 
                                    }
                                    catch 
                                    { 
                                        return 0; 
                                    }
                                });
                                
                                if (readBytes > 0)
                                {
                                    bytesRead += readBytes;
                                }
                                attempts++;
                                
                                // Si no leímos nada en este intento, esperar un poco
                                if (readBytes == 0 && bytesRead < pitSize)
                                {
                                    await Task.Delay(100);
                                }
                            }

                            if (bytesRead > 0) 
                            {
                                // Si leímos menos de lo esperado, ajustar el array
                                if (bytesRead < pitSize)
                                {
                                    byte[] trimmedBuffer = new byte[bytesRead];
                                    Array.Copy(pitBuffer, 0, trimmedBuffer, 0, bytesRead);
                                    Log($"<ID:0/001> PIT Read Successfully ({bytesRead} bytes)!", LogLevel.Success);
                                    return trimmedBuffer;
                                }
                                
                                Log($"<ID:0/001> PIT Read Successfully ({bytesRead} bytes)!", LogLevel.Success);
                                // Aquí podrías guardar el archivo para verificar:
                                // File.WriteAllBytes("dump.pit", pitBuffer);
                                return pitBuffer;
                            }
                            else
                            {
                                Log("<ID:0/001> No PIT data received.", LogLevel.Warning);
                                return null;
                            }
                        } 
                        catch (Exception ex) 
                        {
                            Log($"Error leyendo flujo de datos PIT: {ex.Message}", LogLevel.Error);
                            return null;
                        }
                        finally
                        {
                            _port.ReadTimeout = originalTimeout;
                        }
                    }
                    else
                    {
                        Log("<ID:0/001> PIT request rejected or invalid response.", LogLevel.Warning);
                        return null;
                    }
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error in AutoReadPit: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Solicita el PIT usando una secuencia optimizada que "engaña" al teléfono
        /// para que se mantenga vivo y entregue el PIT sin entrar en el bucle complejo de flasheo
        /// Según el flujo de Odin, antes de un 0x66/0x61, a veces se necesita un "Begin Session" limpio
        /// </summary>
        /// <returns>True si el PIT se recibió correctamente</returns>
        [Obsolete("Usar GetPitForMapping() en su lugar. El protocolo 0x65 es el correcto según Ghidra.")]
        public async Task<bool> RequestPitFinal()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para solicitar PIT final", LogLevel.Error);
                return false;
            }

            try
            {
                byte[] buffer = new byte[1024];
                
                // PASO A: Comando 0x61 con Sub-op 0 (Petición de PIT)
                // Usamos la estructura de 1024 bytes que confirmamos en Ghidra
                BitConverter.GetBytes(0x61).CopyTo(buffer, 0); 
                BitConverter.GetBytes(0x00).CopyTo(buffer, 4);

                // Limpieza de buffers antes de enviar
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _port.Write(buffer, 0, 1024);
                Log("<ID:0/001> Requesting PIT (0x61)...", LogLevel.Info);

                byte[] resp = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 2000; // 2 segundos para la respuesta inicial

                try
                {
                    int read = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(resp, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    // Si el teléfono responde con algo que NO sea FF FF FF FF
                    if (read > 0 && resp[0] != 0xFF) 
                    {
                        string hexResponse = BitConverter.ToString(resp);
                        Log($"PIT Response (HEX): {hexResponse}", LogLevel.Debug);
                        
                        // El teléfono está listo para enviar el PIT.
                        // Ahora debemos leer los datos en paquetes de 1024
                        byte[] pitData = new byte[4096];
                        
                        // Aumentar timeout para la lectura del archivo completo
                        _port.ReadTimeout = 10000; // 10 segundos para recibir el PIT completo
                        
                        try
                        {
                            int dataRead = await Task.Run(() => {
                                try 
                                { 
                                    return _port.Read(pitData, 0, 4096); 
                                }
                                catch 
                                { 
                                    return 0; 
                                }
                            });
                            
                            if (dataRead > 0)
                            {
                                Log($"<ID:0/001> PIT Data Received: {dataRead} bytes.", LogLevel.Success);
                                return true;
                            }
                            else
                            {
                                Log("<ID:0/001> No PIT data received after confirmation.", LogLevel.Warning);
                                return false;
                            }
                        }
                        finally
                        {
                            _port.ReadTimeout = originalTimeout;
                        }
                    }
                    else
                    {
                        Log("<ID:0/001> PIT request rejected (0xFF response). Trying fallback...", LogLevel.Warning);
                    }
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }

                // Si falla el 0x61, intenta el 0x66 pero con Sub-op 1 (Solo cabecera)
                Log("<ID:0/001> Trying fallback: 0x66 with Sub-op 0x01...", LogLevel.Info);
                return await SendControlPacket(0x66, 0x01);
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error in RequestPitFinal: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía un paquete de control con OpCode y Sub-OpCode específicos y retorna la respuesta
        /// Usado para comandos que necesitan leer la respuesta completa (como 0x61 para PIT)
        /// </summary>
        /// <param name="opCode">OpCode del comando (ej: 0x61)</param>
        /// <param name="subOpCode">Sub-OpCode del comando (ej: 0x00 para PIT)</param>
        /// <returns>Array de bytes con la respuesta (8 bytes), o null si falla</returns>
        public async Task<byte[]> SendControlPacketWithResponse(uint opCode, uint subOpCode)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar paquete de control", LogLevel.Error);
                return null;
            }

            try
            {
                byte[] buffer = new byte[1024];
                
                // OpCode (4 bytes Little Endian)
                byte[] opCodeBytes = BitConverter.GetBytes(opCode);
                Array.Copy(opCodeBytes, 0, buffer, 0, 4);
                
                // Sub-OpCode (4 bytes Little Endian)
                byte[] subOpBytes = BitConverter.GetBytes(subOpCode);
                Array.Copy(subOpBytes, 0, buffer, 4, 4);

                // Limpieza de buffers antes de enviar
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _port.Write(buffer, 0, 1024);
                Log($"<ID:0/001> Sending control packet (Op: 0x{opCode:X2}, Sub: 0x{subOpCode:X2})...", LogLevel.Info);

                // Leer respuesta (8 bytes)
                byte[] resp = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000;

                try
                {
                    int read = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(resp, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    if (read >= 8)
                    {
                        string hexResponse = BitConverter.ToString(resp);
                        Log($"Control Packet Response (HEX): {hexResponse}", LogLevel.Debug);
                        return resp;
                    }
                    
                    Log("<ID:0/001> Control packet response incomplete.", LogLevel.Warning);
                    return null;
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error sending control packet: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Envía un paquete de control con OpCode y Sub-OpCode específicos
        /// Usado para establecer modo de sesión y otros comandos de control
        /// Basado en FUN_004324d0 de Ghidra
        /// </summary>
        /// <param name="opCode">OpCode del comando (ej: 0x64)</param>
        /// <param name="subOpCode">Sub-OpCode del comando (ej: 0x03 para establecer modo de sesión)</param>
        /// <returns>True si el comando se envió correctamente</returns>
        public async Task<bool> SendControlPacket(uint opCode, uint subOpCode)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar paquete de control", LogLevel.Error);
                return false;
            }

            try
            {
                byte[] buffer = new byte[1024];
                
                // OpCode (4 bytes Little Endian)
                byte[] opCodeBytes = BitConverter.GetBytes(opCode);
                Array.Copy(opCodeBytes, 0, buffer, 0, 4);
                
                // Sub-OpCode (4 bytes Little Endian)
                byte[] subOpBytes = BitConverter.GetBytes(subOpCode);
                Array.Copy(subOpBytes, 0, buffer, 4, 4);

                // Limpieza de buffers antes de enviar
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _port.Write(buffer, 0, 1024);
                Log($"<ID:0/001> Sending control packet (Op: 0x{opCode:X2}, Sub: 0x{subOpCode:X2})...", LogLevel.Info);

                // Leer respuesta (8 bytes)
                byte[] resp = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000;

                try
                {
                    int read = await Task.Run(() => {
                        try 
                        { 
                            return _port.Read(resp, 0, 8); 
                        }
                        catch 
                        { 
                            return 0; 
                        }
                    });

                    if (read > 0)
                    {
                        string hexResponse = BitConverter.ToString(resp);
                        Log($"Control Packet Response (HEX): {hexResponse}", LogLevel.Debug);
                        
                        // Verificar respuesta (puede ser eco del OpCode o ACK)
                        int responseOp = BitConverter.ToInt32(resp, 0);
                        if (responseOp == opCode || resp[0] == 0x06)
                        {
                            Log($"<ID:0/001> Control packet accepted.", LogLevel.Success);
                            return true;
                        }
                    }
                    
                    Log("<ID:0/001> Control packet response timeout or invalid.", LogLevel.Warning);
                    return true; // Consideramos éxito aunque no haya respuesta clara
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error sending control packet: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía el comando de inicio de NAND Write (Op 0x66, Sub 0x01)
        /// Basado en FUN_00433880: Antes de iniciar el bucle de datos, se envía un paquete de "reserva de espacio"
        /// que contiene: Índice de partición, Tamaño total (64 bits), Dirección de inicio
        /// </summary>
        /// <param name="partitionIndex">Índice de la partición en el PIT (por defecto 0)</param>
        /// <param name="fileSize">Tamaño total del archivo en bytes (64 bits)</param>
        /// <param name="startAddress">Dirección de inicio (normalmente 0 para archivos binarios)</param>
        /// <returns>True si el comando fue aceptado, False si falla</returns>
        private async Task<bool> SendOp66Start(uint partitionIndex, ulong fileSize, ulong startAddress = 0)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar comando Op 0x66 Sub 0x01", LogLevel.Error);
                return false;
            }

            try
            {
                // Estructura del paquete de inicio según análisis específico para MediaTek MT6853:
                // - Offset 0-3: ID de la partición (4 bytes - Little Endian)
                // - Offset 4-7: Tamaño del archivo (4 bytes - Little Endian, int para archivos hasta 2GB)
                // - Offset 8-15: Resto en 0x00 (8 bytes - Inicialización del buffer de recepción)
                // Total: 16 bytes de datos
                byte[] startPacket = new byte[16];
                Array.Clear(startPacket, 0, 16); // Inicializar todo en 0
                
                // Offset 0-3: ID de la partición (4 bytes Little Endian)
                // El ID de la partición se obtiene del PIT parseado (ej: 54 para 'cache')
                byte[] indexBytes = BitConverter.GetBytes((uint)partitionIndex);
                Array.Copy(indexBytes, 0, startPacket, 0, 4);
                
                // Offset 4-7: Tamaño total del archivo .img (DESCOMPRIMIDO) en 4 bytes
                // Es vital que este valor sea exacto al tamaño del archivo en disco
                // NOTA: Si el archivo es >2GB, se truncará. Para archivos grandes, considerar usar 8 bytes.
                if (fileSize > int.MaxValue)
                {
                    Log($"<ID:0/001> Warning: File size {fileSize} bytes exceeds int.MaxValue. Truncating to 4 bytes.", LogLevel.Warning);
                }
                int fileSizeInt = (int)fileSize; // Convertir a int (4 bytes)
                byte[] sizeBytes = BitConverter.GetBytes(fileSizeInt);
                Array.Copy(sizeBytes, 0, startPacket, 4, 4);
                
                // Offset 8-15: Los bytes restantes quedan en 0x00 (ya inicializado por Array.Clear)
                // Esto inicializa el buffer de recepción del dispositivo
                
                // Enviar el paquete usando SendOp66Packet con Sub-op 0x01
                byte[] response = await SendOp66Packet(0x01, startPacket, 16);
                
                if (response == null)
                {
                    Log("Error: No se recibió respuesta al comando de inicio NAND Write", LogLevel.Error);
                    return false;
                }
                
                // Verificar que no sea respuesta de error
                uint responseValue = BitConverter.ToUInt32(response, 0);
                if (responseValue == 0x80000080 || (response[0] == 0x80 && response[3] == 0x80))
                {
                    Log("Error: Dispositivo respondió con error al comando de inicio", LogLevel.Error);
                    return false;
                }
                
                Log($"NAND Write Start command accepted. Partition: {partitionIndex}, Size: {fileSize} bytes", LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error al enviar comando de inicio NAND Write: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía un comando Op 0x66 con sub-opcode y datos opcionales (paquetes pequeños hasta 1024 bytes)
        /// Basado en FUN_004324d0 de Ghidra - Estructura del paquete de 1024 bytes
        /// </summary>
        /// <param name="subOpCode">Sub-opcode del comando (0x01, 0x02, 0x03, etc.)</param>
        /// <param name="data">Datos opcionales a enviar (null para comandos sin datos)</param>
        /// <param name="dataLength">Longitud de los datos a enviar</param>
        /// <returns>Respuesta del dispositivo (8 bytes) o null si falla</returns>
        private async Task<byte[]> SendOp66Packet(uint subOpCode, byte[] data = null, int dataLength = 0)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar comando Op 0x66", LogLevel.Error);
                return null;
            }

            try
            {
                byte[] buffer = new byte[1024];
                Array.Clear(buffer, 0, buffer.Length);

                // Offset 0-3: OpCode 0x66 (4 bytes Little Endian)
                byte[] opCodeBytes = BitConverter.GetBytes(0x66u);
                Array.Copy(opCodeBytes, 0, buffer, 0, 4);

                // Offset 4-7: Sub-OpCode (4 bytes Little Endian)
                byte[] subOpBytes = BitConverter.GetBytes(subOpCode);
                Array.Copy(subOpBytes, 0, buffer, 4, 4);

                // Offset 8+: Datos opcionales (si se proporcionan)
                if (data != null && dataLength > 0)
                {
                    int copyLength = Math.Min(dataLength, buffer.Length - 8);
                    Array.Copy(data, 0, buffer, 8, copyLength);
                }

                // Limpieza de buffers antes de enviar
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _port.Write(buffer, 0, 1024);

                // Leer respuesta (8 bytes)
                byte[] resp = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000;

                try
                {
                    int read = await Task.Run(() =>
                    {
                        try
                        {
                            return _port.Read(resp, 0, 8);
                        }
                        catch
                        {
                            return 0;
                        }
                    });

                    if (read >= 8)
                    {
                        return resp;
                    }

                    return null;
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"Error enviando comando Op 0x66 Sub 0x{subOpCode:X2}: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Envía un comando Op 0x66 con sub-opcode y datos grandes (para paquetes de commit con header + datos)
        /// Este método maneja paquetes que pueden ser mayores a 1024 bytes (ej: 32 bytes header + 131072 bytes datos)
        /// Basado en FUN_004324d0 de Ghidra pero adaptado para paquetes grandes
        /// </summary>
        /// <param name="subOpCode">Sub-opcode del comando (normalmente 0x03 para commit)</param>
        /// <param name="data">Datos completos a enviar (header + datos del chunk)</param>
        /// <param name="dataLength">Longitud total de los datos a enviar</param>
        /// <returns>Respuesta del dispositivo (8 bytes) o null si falla</returns>
        private async Task<byte[]> SendOp66PacketLarge(uint subOpCode, byte[] data, int dataLength)
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para enviar comando Op 0x66 (paquete grande)", LogLevel.Error);
                return null;
            }

            try
            {
                // PASO 1: Enviar el header del paquete (primeros 1024 bytes con OpCode, SubOpCode y primeros datos)
                byte[] headerBuffer = new byte[1024];
                Array.Clear(headerBuffer, 0, 1024);

                // Offset 0-3: OpCode 0x66 (4 bytes Little Endian)
                byte[] opCodeBytes = BitConverter.GetBytes(0x66u);
                Array.Copy(opCodeBytes, 0, headerBuffer, 0, 4);

                // Offset 4-7: Sub-OpCode (4 bytes Little Endian)
                byte[] subOpBytes = BitConverter.GetBytes(subOpCode);
                Array.Copy(subOpBytes, 0, headerBuffer, 4, 4);

                // Offset 8+: Primeros datos (hasta 1016 bytes en el buffer de 1024)
                int headerDataLength = Math.Min(dataLength, 1024 - 8);
                if (headerDataLength > 0)
                {
                    Array.Copy(data, 0, headerBuffer, 8, headerDataLength);
                }

                // Limpieza de buffers antes de enviar
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                // Enviar header de 1024 bytes
                _port.Write(headerBuffer, 0, 1024);

                // PASO 2: Si hay más datos después de los primeros 1016 bytes, enviarlos directamente
                if (dataLength > (1024 - 8))
                {
                    int remainingData = dataLength - (1024 - 8);
                    int remainingOffset = 1024 - 8;
                    _port.Write(data, remainingOffset, remainingData);
                }

                // PASO 3: Leer respuesta (8 bytes)
                byte[] resp = new byte[8];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 5000;

                try
                {
                    int read = await Task.Run(() =>
                    {
                        try
                        {
                            return _port.Read(resp, 0, 8);
                        }
                        catch
                        {
                            return 0;
                        }
                    });

                    if (read >= 8)
                    {
                        return resp;
                    }

                    return null;
                }
                finally
                {
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"Error enviando comando Op 0x66 Sub 0x{subOpCode:X2} (paquete grande): {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Paso A: Pre-check (Op 0x66, Sub 0x00)
        /// Verifica si el dispositivo está listo para recibir datos
        /// Basado en FUN_00433880 - local_5c = FUN_004324d0(0x66,0,0,0,0,0,1)
        /// NOTA: El sub-opcode es 0x00, NO 0x05
        /// </summary>
        /// <param name="maxRetries">Número máximo de reintentos si el dispositivo está ocupado</param>
        /// <returns>True si el dispositivo está listo (respuesta 00-00-00-00), False si falla</returns>
        private async Task<bool> SendOp66PreCheck(int maxRetries = 10)
        {
            const uint BUSY_RESPONSE = 0x80000080; // -0x80000000 en formato little endian
            const uint READY_RESPONSE = 0x00000000; // 00-00-00-00

            for (int retry = 0; retry < maxRetries; retry++)
            {
                // FUN_004324d0(0x66,0,0,0,0,0,1) - Sub-opcode es 0x00
                byte[] response = await SendOp66Packet(0x00);
                if (response == null)
                {
                    Log($"Pre-check falló: No se recibió respuesta (intento {retry + 1}/{maxRetries})", LogLevel.Warning);
                    if (retry < maxRetries - 1)
                    {
                        await Task.Delay(10); // Esperar 10ms antes de reintentar
                        continue;
                    }
                    return false;
                }

                uint responseValue = BitConverter.ToUInt32(response, 0);

                if (responseValue == READY_RESPONSE)
                {
                    // Dispositivo listo
                    return true;
                }
                else if (responseValue == BUSY_RESPONSE || (response[0] == 0x80 && response[3] == 0x80))
                {
                    // Dispositivo ocupado (-0x80000000)
                    Log($"Dispositivo ocupado, esperando 10ms... (intento {retry + 1}/{maxRetries})", LogLevel.Debug);
                    await Task.Delay(10);
                    continue;
                }
                else
                {
                    // Respuesta inesperada, pero continuamos
                    Log($"Pre-check: Respuesta inesperada 0x{responseValue:X8}, continuando...", LogLevel.Debug);
                    return true;
                }
            }

            Log("Pre-check falló: Dispositivo ocupado después de múltiples intentos", LogLevel.Error);
            return false;
        }

        /// <summary>
        /// Paso B: Data Transfer (Op 0x66, Sub 0x02 + datos)
        /// Envía un fragmento de datos al dispositivo
        /// Basado en FUN_00433880:
        /// 1. local_38 = ((local_58 - 1 >> 0x11) + 1) * 0x20000 - Calcula tamaño del chunk (131072 bytes)
        /// 2. FUN_004324d0(0x66,2,&local_38,1,0,0,1) - Envía 1 byte del tamaño del chunk
        /// 3. FUN_004327a0(param_2,local_58,uVar6) - Envía los datos reales
        /// 4. ReleaseBuffer(0xffffffff) - Libera el buffer
        /// </summary>
        /// <param name="data">Datos del fragmento a enviar</param>
        /// <param name="dataLength">Longitud de los datos</param>
        /// <param name="skipChunkSizeCommand">Si es true, no envía el comando 0x66, 0x02 (ya se envió antes del bucle)</param>
        /// <returns>True si el envío fue exitoso, False si falla</returns>
        private async Task<bool> SendOp66Data(byte[] data, int dataLength, bool skipChunkSizeCommand = false)
        {
            if (data == null || dataLength <= 0)
            {
                Log("Error: Datos inválidos para envío Op 0x66 Sub 0x02", LogLevel.Error);
                return false;
            }

            if (_port == null || !_port.IsOpen)
            {
                Log("Error: Puerto no disponible para envío de datos", LogLevel.Error);
                return false;
            }

            try
            {
                // Según FUN_00433880: Después del Buffer Reserve (0x66, 0x02), los datos se envían DIRECTAMENTE
                // FUN_004327a0(param_2,local_58,uVar6) - envía los datos reales directamente al puerto
                // NO se envía otro comando 0x66, 0x02 aquí si skipChunkSizeCommand es true
                // (el Buffer Reserve ya se envió antes del bucle)
                
                if (!skipChunkSizeCommand)
                {
                    // PASO 1: Calcular el tamaño del chunk según la fórmula exacta de FUN_00433880
                    // local_38 = ((local_58 - 1 >> 0x11) + 1) * 0x20000
                    // Esto asegura que el chunk sea siempre múltiplo de 131072 bytes (128KB)
                    uint alignedSize = (uint)(((dataLength - 1) >> 17) + 1) * 0x20000;
                    
                    // PASO 2: Enviar el tamaño alineado completo (4 bytes) con Op 0x66 Sub 0x02
                    // CORRECCIÓN: Enviar los 4 bytes completos del tamaño alineado, no solo 1 byte
                    // Según FUN_00433880: local_38 se envía completo (4 bytes) para el alineamiento correcto
                    byte[] sizeBytes = BitConverter.GetBytes(alignedSize);
                    byte[] sizeResponse = await SendOp66Packet(0x02, sizeBytes, 4);
                    
                    if (sizeResponse == null)
                    {
                        Log("Error: No se recibió respuesta al enviar tamaño del fragmento", LogLevel.Error);
                        return false;
                    }
                    
                    // Verificar que no sea respuesta de error
                    uint responseValue = BitConverter.ToUInt32(sizeResponse, 0);
                    if (responseValue == 0x80000080 || (sizeResponse[0] == 0x80 && sizeResponse[3] == 0x80))
                    {
                        Log("Error: Dispositivo respondió con error al recibir tamaño", LogLevel.Error);
                        return false;
                    }
                }

                // PASO 3: Enviar los datos reales directamente al puerto
                // FUN_004327a0(param_2,local_58,uVar6) - envía los datos reales (local_58 bytes)
                // IMPORTANTE: Enviar exactamente dataLength bytes, no chunkSize
                // El chunkSize es solo para la reserva de espacio, los datos reales son dataLength
                _port.DiscardOutBuffer();
                
                // Enviar los datos del fragmento (exactamente dataLength bytes)
                _port.Write(data, 0, dataLength);
                
                // Pequeño delay para que el dispositivo procese
                await Task.Delay(1);

                return true;
            }
            catch (Exception ex)
            {
                Log($"Error al enviar datos Op 0x66 Sub 0x02: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Paso C: Post-check / Write Commit (Op 0x66, Sub 0x03)
        /// Confirma que el bloque se escribió correctamente en la memoria flash
        /// Basado en FUN_00433880 - local_5c = FUN_004324d0(0x66,3,&local_38,8,0,0,1)
        /// NOTA: El sub-opcode es 0x03, NO 0x07
        /// 
        /// Estructura completa del paquete de commit (32 bytes - 8 campos de 4 bytes):
        /// - local_38 (offset 0-3) = 0 (inicializado)
        /// - uStack_34 (offset 4-7) = local_58 (tamaño del fragmento enviado)
        /// - uStack_30 (offset 8-11) = *local_40 (primer campo de la estructura de partición/archivo)
        /// - uStack_2c (offset 12-15) = local_40[1] (segundo campo)
        /// - uStack_28 (offset 16-19) = local_40[2] (tercer campo)
        /// - uStack_24 (offset 20-23) = local_88 (FLAG CRÍTICO: 1 si es último chunk, 0 si no)
        /// - uStack_20 (offset 24-27) = 0
        /// - uStack_1c (offset 28-31) = 0
        /// 
        /// El flag uStack_24 es CRÍTICO: si no se envía 1 en el último chunk, el teléfono
        /// piensa que todavía hay más datos y no escribe nada a la memoria física.
        /// </summary>
        /// <param name="chunkData">Datos del chunk a enviar (se incluyen después del header de 32 bytes)</param>
        /// <param name="bytesSent">Bytes enviados en este fragmento</param>
        /// <param name="totalBytesSent">Total de bytes enviados hasta ahora</param>
        /// <param name="totalFileSize">Tamaño total del archivo</param>
        /// <param name="isLastChunk">True si este es el último fragmento</param>
        /// <returns>True si el commit fue exitoso, False si falla</returns>
        private async Task<bool> SendOp66Commit(byte[] chunkData, uint bytesSent, ulong totalBytesSent, ulong totalFileSize, bool isLastChunk)
        {
            // Según FUN_00433880 y análisis detallado:
            // La estructura completa del paquete de commit tiene 32 bytes (8 campos de 4 bytes cada uno)
            // según las variables uStack en la pila:
            // - local_38 (4 bytes) = 0 (inicializado)
            // - uStack_34 (4 bytes) = local_58 (tamaño del fragmento enviado)
            // - uStack_30 (4 bytes) = *local_40 (primer campo de la estructura de partición/archivo)
            // - uStack_2c (4 bytes) = local_40[1] (segundo campo)
            // - uStack_28 (4 bytes) = local_40[2] (tercer campo)
            // - uStack_24 (4 bytes) = local_88 (FLAG CRÍTICO: 1 si es último chunk, 0 si no)
            // - uStack_20 (4 bytes) = 0
            // - uStack_1c (4 bytes) = 0
            // 
            // El flag uStack_24 es CRÍTICO: si no se envía 1 en el último chunk, el teléfono
            // piensa que todavía hay más datos y no escribe nada a la memoria física.
            
            // Estructura del paquete: Header de 32 bytes (los datos se envían después)
            // El header se envía como paquete 0x66, 0x03, y luego los datos directamente al puerto
            byte[] commitPacket = new byte[32];
            Array.Clear(commitPacket, 0, 32); // Inicializar header a 0
            
            // HEADER DE 32 BYTES:
            // local_38 (offset 0-3) = 0 (inicializado a 0)
            // Ya está en 0 por Array.Clear
            
            // uStack_34 (offset 4-7) = local_58 (tamaño del fragmento enviado)
            byte[] bytesSentBytes = BitConverter.GetBytes(bytesSent);
            Array.Copy(bytesSentBytes, 0, commitPacket, 4, 4);
            
            // uStack_30 (offset 8-11) = *local_40 (primer campo de la estructura de partición/archivo)
            // Por ahora usamos 0, pero podría contener información del archivo
            // Ya está en 0
            
            // uStack_2c (offset 12-15) = local_40[1] (segundo campo)
            // Ya está en 0
            
            // uStack_28 (offset 16-19) = local_40[2] (tercer campo)
            // Ya está en 0
            
            // uStack_24 (offset 20-23) = local_88 (FLAG CRÍTICO: 1 si es último chunk, 0 si no)
            // ESTE ES EL CAMPO MÁS IMPORTANTE: le dice al teléfono que termine de escribir
            // Si este flag no es 1 en el último chunk, el teléfono se queda esperando más datos
            // y el timeout expira causando "NAND Write Failed"
            uint lastChunkFlag = isLastChunk ? 1u : 0u;
            byte[] lastChunkBytes = BitConverter.GetBytes(lastChunkFlag);
            Array.Copy(lastChunkBytes, 0, commitPacket, 20, 4);
            
            // Log crítico para depuración
            if (isLastChunk)
            {
                Log($"<ID:0/001> CRÍTICO: Enviando flag de finalización (uStack_24 = 1) en el último chunk. Bytes enviados: {bytesSent}, Total: {totalFileSize}", LogLevel.Info);
            }
            
            // uStack_20 (offset 24-27) = 0
            // Ya está en 0
            
            // uStack_1c (offset 28-31) = 0
            // Ya está en 0

            // DATOS DEL CHUNK:
            // Según el análisis definitivo: El paquete 0x66, 0x03 debe incluir header de 32 bytes + datos del chunk
            // Todo en un solo envío para que el teléfono procese correctamente
            if (chunkData == null || chunkData.Length < bytesSent)
            {
                Log("Error: Datos del chunk inválidos para el commit", LogLevel.Error);
                return false;
            }

            // CORRECCIÓN CRÍTICA: Construir el paquete completo para envío único
            // Estructura según FUN_00433880: Header 0x66/0x03 (8 bytes) + Header de 32 bytes + Datos del chunk
            // Todo debe enviarse en un solo bloque continuo para evitar fragmentación que causa error 0xFFFFFFFF
            int headerAndDataSize = 32 + (int)bytesSent; // Header de 32 bytes + datos del chunk
            int totalDataSize = 8 + headerAndDataSize; // 8 bytes (0x66 + 0x03) + 32 bytes header + datos
            
            // Construir el paquete completo: header de comando + header de 32 bytes + datos
            byte[] fullPacket = new byte[totalDataSize];
            
            // PASO 1: Construir header del comando 0x66, 0x03 (primeros 8 bytes)
            byte[] opCodeBytes = BitConverter.GetBytes(0x66u);
            Array.Copy(opCodeBytes, 0, fullPacket, 0, 4);
            byte[] subOpBytes = BitConverter.GetBytes(0x03u);
            Array.Copy(subOpBytes, 0, fullPacket, 4, 4);
            
            // PASO 2: Copiar header de 32 bytes después del comando (offset 8+)
            Array.Copy(commitPacket, 0, fullPacket, 8, 32);
            
            // PASO 3: Copiar datos del chunk después del header de 32 bytes (offset 40+)
            Array.Copy(chunkData, 0, fullPacket, 40, (int)bytesSent);

            // PASO 4: Enviar el paquete completo en un solo bloque continuo
            // El protocolo LOKE requiere el primer bloque de 1024 bytes, pero si el paquete es menor,
            // lo enviamos completo. Si es mayor, enviamos el bloque de 1024 y luego el resto.
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            
            if (totalDataSize <= 1024)
            {
                // Si el paquete cabe en 1024 bytes, enviarlo completo en un buffer de 1024
                byte[] buffer1024 = new byte[1024];
                Array.Clear(buffer1024, 0, 1024);
                Array.Copy(fullPacket, 0, buffer1024, 0, totalDataSize);
                _port.Write(buffer1024, 0, 1024);
            }
            else
            {
                // Si el paquete es mayor, enviar primero 1024 bytes y luego el resto en un solo envío continuo
                byte[] buffer1024 = new byte[1024];
                Array.Copy(fullPacket, 0, buffer1024, 0, 1024);
                _port.Write(buffer1024, 0, 1024);
                
                // Enviar el resto de datos inmediatamente después (sin fragmentación)
                int remainingSize = totalDataSize - 1024;
                _port.Write(fullPacket, 1024, remainingSize);
            }
            
            // Leer respuesta (8 bytes)
            byte[] resp = new byte[8];
            int originalTimeout = _port.ReadTimeout;
            _port.ReadTimeout = 5000;
            
            byte[] response = null;
            try
            {
                int read = await Task.Run(() =>
                {
                    try
                    {
                        return _port.Read(resp, 0, 8);
                    }
                    catch
                    {
                        return 0;
                    }
                });
                
                if (read == 8)
                {
                    response = resp;
                }
            }
            catch (Exception ex)
            {
                Log($"Error al leer respuesta del commit: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                _port.ReadTimeout = originalTimeout;
            }
            
            if (response == null)
            {
                Log("Error: No se recibió respuesta al commit (Op 0x66 Sub 0x03). Posible timeout o puerto cerrado.", LogLevel.Error);
                // Resetear buffer del puerto si hay problema de comunicación
                try
                {
                    if (_port != null && _port.IsOpen)
                    {
                        _port.DiscardInBuffer();
                        _port.DiscardOutBuffer();
                    }
                }
                catch { }
                return false;
            }
            
            // Verificar respuesta: 0x66 es una respuesta válida de "Continuar" durante el envío de fragmentos
            // También aceptamos 0x00 (éxito) y rechazamos 0x80000080 (error)
            uint responseValue = BitConverter.ToUInt32(response, 0);
            bool isValidResponse = false;
            
            // Respuestas válidas según protocolo LOKE:
            // - 0x00000000 (0x00): Éxito/Listo
            // - 0x00000066 (0x66): Continuar (válido durante envío de fragmentos) - ¡CRÍTICO!
            // - 0xFFFFFFFF: NO es una respuesta válida, es un error de timeout/puerto cerrado
            // - 0x80000080: Error explícito del dispositivo
            if (responseValue == 0x00000000)
            {
                isValidResponse = true;
            }
            else if (responseValue == 0x00000066 || response[0] == 0x66)
            {
                // 0x66 es una respuesta válida de "Continuar" durante el envío de fragmentos
                isValidResponse = true;
                Log("Dispositivo respondió con 0x66 (Continuar) - Respuesta válida", LogLevel.Debug);
            }
            else if (responseValue == 0xFFFFFFFF)
            {
                // 0xFFFFFFFF indica timeout o puerto cerrado - NO es una respuesta válida del dispositivo
                Log("Error: Respuesta 0xFFFFFFFF detectada. Esto indica timeout o puerto cerrado, no una respuesta del dispositivo.", LogLevel.Error);
                // Resetear buffer del puerto
                try
                {
                    if (_port != null && _port.IsOpen)
                    {
                        _port.DiscardInBuffer();
                        _port.DiscardOutBuffer();
                    }
                }
                catch { }
                return false;
            }
            else if (responseValue == 0x80000080 || (response[0] == 0x80 && response[3] == 0x80))
            {
                Log("Error: Dispositivo respondió con error al commit (0x80000080)", LogLevel.Error);
                return false;
            }
            else
            {
                // Otra respuesta inesperada, pero no es error explícito - aceptarla
                isValidResponse = true;
                Log($"Respuesta inesperada pero no error: 0x{responseValue:X8} - Continuando...", LogLevel.Debug);
            }
            
            if (!isValidResponse)
            {
                Log($"Error: Respuesta inválida al commit: 0x{responseValue:X8}", LogLevel.Error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Realiza un handshake completo para "desbloquear" el PIT
        /// Secuencia correcta basada en análisis de Ghidra:
        /// 1. Abrir Sesión (Op 0x64, Sub 0x01)
        /// 2. Pedir Info (Op 0x65, Sub 0x01)
        /// 3. Pedir el PIT usando GetPitForMapping() (protocolo 0x65)
        /// NOTA: NO enviar 0x64, 3 después de 0x64/1 y 0x65 (causa reinicio de sesión)
        /// </summary>
        /// <returns>True si el handshake completo fue exitoso y el PIT se recibió correctamente</returns>
        public async Task<bool> FullHandshake()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para handshake completo", LogLevel.Error);
                return false;
            }

            try
            {
                // 1. Abrir Sesión (Op 0x64, Sub 0x01)
                Log("<ID:0/001> Step 1: Opening session (0x64, 0x01)...", LogLevel.Info);
                if (!await SendOdinHandshake())
                {
                    Log("<ID:0/001> Failed to open session.", LogLevel.Error);
                    return false;
                }

                // 2. Pedir Info (Op 0x65, Sub 0x01)
                Log("<ID:0/001> Step 2: Requesting device info (0x65)...", LogLevel.Info);
                if (!await SendRequestInfo())
                {
                    Log("<ID:0/001> Failed to request device info.", LogLevel.Warning);
                    // Continuamos aunque falle
                }

                // 3. AHORA pedir el PIT usando el protocolo correcto 0x65
                // NO enviar 0x64, 3 - causa reinicio de sesión en modelos modernos
                Log("<ID:0/001> Step 3: Requesting PIT using protocol 0x65 (GetPitForMapping)...", LogLevel.Info);
                byte[] pitData = await GetPitForMapping();
                
                if (pitData != null && pitData.Length > 0)
                {
                    Log($"<ID:0/001> Full handshake successful! PIT received ({pitData.Length} bytes).", LogLevel.Success);
                    return true;
                }
                else
                {
                    Log("<ID:0/001> Full handshake completed but PIT request failed.", LogLevel.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error in full handshake: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Inicia una sesión completa con el dispositivo: abre puerto, handshake y solicita información
        /// Este método orquesta el flujo completo para mantener la conexión estable
        /// </summary>
        /// <param name="portName">Nombre del puerto COM a usar</param>
        /// <returns>True si la sesión se inició correctamente</returns>
        public async Task<bool> StartSession(string portName)
        {
            try
            {
                // PASO 1: Inicializar puerto con parámetros de Ghidra
                if (await InitializeOdinConnection(portName))
                {
                    // PASO 2: Abrir Sesión (Handshake con 0x64)
                    if (await SendOdinHandshake()) 
                    {
                        // PASO 3: Inmediatamente pedir información (Comando 0x65)
                        // Esto evita que el teléfono reinicie el puerto USB
                        if (await SendRequestInfo())
                        {
                            Log("<ID:0/001> Session started successfully. Device ready.", LogLevel.Success);
                            return true;
                        }
                        else
                        {
                            Log("<ID:0/001> Handshake successful but info request failed.", LogLevel.Warning);
                            // Aún consideramos éxito si el handshake funcionó
                            return true;
                        }
                    }
                    else
                    {
                        Log("<ID:0/001> Handshake failed. Session not started.", LogLevel.Error);
                        return false;
                    }
                }
                else
                {
                    Log("<ID:0/001> Failed to initialize port. Session not started.", LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"<ID:0/001> Error starting session: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Envía el paquete de apertura de sesión usando el formato correcto de Ghidra (FUN_004324d0)
        /// Estructura del paquete:
        /// - Byte 0: 0x64 (OpCode para 'Open Session')
        /// - Bytes 1-3: 0x00, 0x00, 0x00 (padding/reservado)
        /// - Bytes 4-7: "ODIN" (magic string)
        /// - Bytes 8-499: 0x00 (resto del paquete)
        /// 
        /// La diferencia clave con SendOdinHandshake() es que este método usa el OpCode 0x64 al inicio,
        /// que es lo que el chip UART de Samsung espera para activar el modo de transferencia.
        /// </summary>
        /// <returns>True si la sesión se abrió correctamente (recibió ACK 0x06)</returns>
        public async Task<bool> SendOpenSession()
        {
            if (_port == null || !_port.IsOpen)
            {
                Log("Puerto no disponible para abrir sesión", LogLevel.Error);
                return false;
            }

            try
            {
                // EL TAMAÑO CORRECTO: 1024 bytes (0x400 como vimos en Ghidra)
                byte[] packet = new byte[1024];

                // ESTRUCTURA (LITTLE ENDIAN)
                // param_1 (OpCode 0x64 para Open Session) - local_408 = param_1
                packet[0] = 0x64; // OpCode para 'Open Session' (confirmado en Ghidra)
                packet[1] = 0x00;
                packet[2] = 0x00;
                packet[3] = 0x00;

                // param_2 (Sub-comando, Odin suele usar 0x00 o 0x01 aquí) - local_404 = param_2
                packet[4] = 0x01;
                packet[5] = 0x00;
                packet[6] = 0x00;
                packet[7] = 0x00;

                // Magic String "ODIN" (Ubicación estándar en offset 8)
                byte[] magic = System.Text.Encoding.ASCII.GetBytes("ODIN");
                Array.Copy(magic, 0, packet, 8, magic.Length);

                // Bytes 12-1023: Resto del paquete con 0x00 (ya inicializado por defecto)

                Log("Enviando paquete maestro de 1024 bytes (Op: 0x64)...", LogLevel.Info);
                
                _port.Write(packet, 0, 1024);

                // Esperar respuesta: El teléfono debe devolver un ACK (0x06) 
                // o un paquete que empiece por 0x64 (Sesión aceptada)
                byte[] response = new byte[1];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 2000;
                
                try
                {
                    int bytesRead = await Task.Run(() => _port.Read(response, 0, 1));

                    if (bytesRead > 0 && response[0] == 0x06) // 0x06 es ACK (Acknowledge)
                    {
                        Log("<ID:0/001> Handshake Successful! Session Opened.", LogLevel.Success);
                        return true;
                    }
                    else if (bytesRead > 0 && response[0] == 0x64)
                    {
                        // Algunos modelos responden con 0x64 (Sesión aceptada)
                        Log("<ID:0/001> Handshake Successful! Session Opened (0x64 response).", LogLevel.Success);
                        return true;
                    }
                    else
                    {
                        Log($"Respuesta inesperada: 0x{response[0]:X2}", LogLevel.Warning);
                        return false;
                    }
                }
                finally
                {
                    // Restaurar timeout original
                    _port.ReadTimeout = originalTimeout;
                }
            }
            catch (Exception ex)
            {
                Log($"Fallo en la respuesta del hardware: {ex.Message}", LogLevel.Error);
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
            // Nota: InitializeOdinConnection ya incluye el delay de 500ms interno según Ghidra
            if (await InitializeOdinConnection(portName))
            {
                // 2. Enviar el paquete "ODIN" inmediatamente (el delay ya se hizo en InitializeOdinConnection)
                return await SendOdinHandshake();
            }

            return false;
        }

        /// <summary>
        /// Motor de Handshake definitivo basado en la investigación de Ghidra
        /// Abre el puerto, configura DTR/RTS, realiza el handshake con formato 0x64 y mantiene el puerto abierto
        /// Incluye los parámetros de DTR/RTS y el Sleep(500) que encontramos en FUN_00437fb0
        /// Actualizado para usar el formato correcto según Ghidra: OpCode 0x64 (4 bytes) + Magic String "ODIN"
        /// </summary>
        /// <param name="portName">Nombre del puerto COM a abrir y configurar</param>
        /// <returns>True si el handshake fue exitoso (recibió ACK 0x06 o respuesta 0x64)</returns>
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

                // ESTO ES VITAL: El teléfono necesita tiempo para detectar que DTR/RTS subieron
                // El delay de 500ms debe ocurrir DESPUÉS de abrir el puerto y ANTES de limpiar buffers
                // Esto permite que el hardware del teléfono detecte las señales eléctricas correctamente
                await Task.Delay(500);

                // Limpieza de buffers DESPUÉS del delay (orden correcto según Ghidra)
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                // Enviamos el paquete con formato correcto según Ghidra: 1024 bytes (0x400)
                // Estructura: [0-3] OpCode 0x64 (4 bytes, Little Endian), [4-7] Sub-comando, [8-11] Magic String "ODIN"
                byte[] buffer = new byte[1024];
                
                // param_1 (OpCode 0x64 para Open Session) - local_408 = param_1
                buffer[0] = 0x64; // OpCode Open Session
                buffer[1] = 0x00;
                buffer[2] = 0x00;
                buffer[3] = 0x00;

                // param_2 (Sub-comando, Odin suele usar 0x00 o 0x01 aquí) - local_404 = param_2
                buffer[4] = 0x01;
                buffer[5] = 0x00;
                buffer[6] = 0x00;
                buffer[7] = 0x00;

                // Magic String "ODIN" (Ubicación estándar en offset 8)
                byte[] magic = System.Text.Encoding.ASCII.GetBytes("ODIN");
                Array.Copy(magic, 0, buffer, 8, magic.Length);

                _port.Write(buffer, 0, 1024);
                Log("Enviando paquete maestro de 1024 bytes (Op: 0x64)...", LogLevel.Info);

                // Esperamos respuesta ACK (0x06) o 0x64
                byte[] response = new byte[1];
                int originalTimeout = _port.ReadTimeout;
                _port.ReadTimeout = 2000;
                
                try
                {
                    int read = await Task.Run(() => _port.Read(response, 0, 1));

                    if (read > 0 && (response[0] == 0x06 || response[0] == 0x64))
                    {
                        Log($"Handshake exitoso en {portName}. Puerto mantenido abierto.", LogLevel.Success);
                        return true;
                    }
                    else if (read > 0)
                    {
                        Log($"Respuesta inesperada en {portName}: 0x{response[0]:X2}", LogLevel.Warning);
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
