using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Reflection;
using static SharpOdinClient.util.utils;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Motor de protocolo Odin refactorizado y robusto
    /// Usa Streams para soportar archivos de varios GB sin OutOfMemory
    /// Basado en análisis de Ghidra: FUN_00435ad0, FUN_00434170, FUN_00434d50, FUN_00434fb0, FUN_00438342
    /// </summary>
    public class OdinEngine : IDisposable
    {
        // Eventos para la UI (compatibles con el sistema existente)
        public delegate void LogDelegate(string Text, MsgType Color, bool IsError = false);
        public delegate void ProgressDelegate(string filename, long max, long value, long writenSize);
        
        public event LogDelegate Log;
        public event ProgressDelegate ProgressChanged;

        private SerialPort _port;
        private readonly string _portName;
        private SharpOdinClient.Odin odinInstance;
        private bool _useDirectSerialPort = false;

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
        /// Constructor para modo compatibilidad con SharpOdinClient
        /// </summary>
        public OdinEngine(SharpOdinClient.Odin odin)
        {
            odinInstance = odin ?? throw new ArgumentNullException(nameof(odin));
            _useDirectSerialPort = false;
        }

        /// <summary>
        /// Constructor para modo directo con puerto serial
        /// </summary>
        public OdinEngine(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                throw new ArgumentNullException(nameof(portName));
            
            _portName = portName;
            _useDirectSerialPort = true;
        }

        private void LogMessage(string msg, bool error = false)
        {
            Log?.Invoke(msg, error ? MsgType.Result : MsgType.Message, error);
        }

        /// <summary>
        /// Flasheo robusto usando FileStream para evitar OutOfMemory
        /// Soporta archivos de varios GB sin cargar todo en memoria
        /// </summary>
        public async Task<bool> FlashFileAsync(string filePath, bool isLargeFile)
        {
            if (!File.Exists(filePath))
            {
                LogMessage($"Archivo no encontrado: {filePath}", true);
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
                    LogMessage($"Iniciando transferencia de {fileName} ({totalSize / (1024.0 * 1024.0):F2} MB)...");

                    if (!PrepareConnection()) return false;

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
                            ProgressChanged?.Invoke(fileName, totalSize, bytesSent, bytesSent);
                            lastProgressReport = bytesSent;
                        }
                    }

                    ProgressChanged?.Invoke(fileName, totalSize, totalSize, totalSize);
                    LogMessage($"Transferencia completada: {fileName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Fallo crítico: {ex.Message}", true);
                return false;
            }
        }

        /// <summary>
        /// Función definitiva para enviar archivos usando el protocolo LOKE
        /// Usa Task.Run para evitar que la UI de WPF se congele durante el envío
        /// </summary>
        public async Task<bool> SendFileWithLokeProtocol(string filePath, long fileSize)
        {
            if (!_useDirectSerialPort)
            {
                LogMessage("SendFileWithLokeProtocol requiere acceso directo al puerto serial", true);
                return false;
            }

            if (!File.Exists(filePath))
            {
                LogMessage($"Archivo no encontrado: {filePath}", true);
                return false;
            }

            int currentChunkSize = (fileSize > 1024 * 1024) ? LokeProtocol.CHUNK_DATA : LokeProtocol.CHUNK_CONTROL;
            string fileName = Path.GetFileName(filePath);

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

                LogMessage($"Iniciando envío de {fileName} ({fileSize / (1024.0 * 1024.0):F2} MB)");

                Task<bool> sendTask = Task.Run(async () =>
                {
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, currentChunkSize, useAsync: true))
                    {
                        byte[] buffer = new byte[currentChunkSize];
                        int bytesRead;
                        long totalSent = 0;
                        long lastProgressReport = 0;
                        const long PROGRESS_REPORT_INTERVAL = 1024 * 1024;
                        DateTime lastWriteTime = DateTime.Now;
                        const int MAX_IDLE_MS = 400;

                        while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
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

                                if (totalSent - lastProgressReport >= PROGRESS_REPORT_INTERVAL || totalSent == fileSize)
                                {
                                    ProgressChanged?.Invoke(fileName, fileSize, totalSent, totalSent);
                                    lastProgressReport = totalSent;
                                }

                                if (currentChunkSize == LokeProtocol.CHUNK_DATA && totalSent % (LokeProtocol.CHUNK_DATA * 10) == 0)
                                {
                                    await Task.Delay(1);
                                }
                            }
                            catch (IOException ex)
                            {
                                LogMessage($"Atasco de E/S detectado. Aplicando corrección Odin...", true);
                                if (!RecoverPortAfterError()) return false;
                                try
                                {
                                    _port.Write(buffer, 0, bytesRead);
                                    totalSent += bytesRead;
                                }
                                catch (Exception retryEx)
                                {
                                    LogMessage($"Error al reintentar: {retryEx.Message}", true);
                                    return false;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogMessage($"Error inesperado: {ex.Message}", true);
                                return false;
                            }
                        }
                        return true;
                    }
                });

                bool sendResult = await sendTask;
                if (!sendResult) return false;

                if (fileSize > 100 * 1024 * 1024)
                {
                    LogMessage("Archivo grande finalizado. Limpiando buffers...");
                    ClearPortAfterLargeFile();
                }

                ProgressChanged?.Invoke(fileName, fileSize, fileSize, fileSize);
                LogMessage($"Archivo {fileName} enviado exitosamente ({fileSize / (1024.0 * 1024.0):F2} MB)");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Error Crítico: {ex.Message}", true);
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

        private bool PrepareConnection()
        {
            try
            {
                if (!_useDirectSerialPort)
                {
                    return true; // Modo compatibilidad
                }

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
                
                return LokeProtocol.PerformHandshake(_port);
            }
            catch (Exception ex)
            {
                LogMessage($"Error en PrepareConnection: {ex.Message}", true);
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
                LogMessage($"Error al enviar buffer: {ex.Message}", true);
                return false;
            }
        }

        /// <summary>
        /// Obtiene el puerto serial actual (para uso con Win32Comm)
        /// </summary>
        public System.IO.Ports.SerialPort GetCurrentPort()
        {
            if (_useDirectSerialPort && _port != null)
            {
                return _port;
            }
            return null;
        }

        /// <summary>
        /// Recupera el puerto después de un error usando funciones nativas y re-sincroniza LOKE
        /// </summary>
        public bool RecoverPortAfterError()
        {
            if (!_useDirectSerialPort || _port == null)
            {
                Thread.Sleep(500);
                return true;
            }

            try
            {
                LogMessage("Error detectado. Intentando recuperar puerto...");
                PurgePortBuffers();
                Thread.Sleep(500);
                
                if (!_port.IsOpen) _port.Open();
                
                bool handshakeSuccess = LokeProtocol.PerformHandshake(_port);
                if (handshakeSuccess)
                {
                    LogMessage("Puerto recuperado y protocolo LOKE re-sincronizado");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                LogMessage($"Error durante recuperación: {ex.Message}", true);
                return false;
            }
        }

        /// <summary>
        /// Limpieza después de archivos grandes para evitar ERROR_IO_PENDING
        /// </summary>
        public bool ClearPortAfterLargeFile()
        {
            if (!_useDirectSerialPort || _port == null)
            {
                Thread.Sleep(500);
                return true;
            }

            try
            {
                LogMessage("Waiting for device buffer to clear...");
                Thread.Sleep(500);
                
                if (!_port.IsOpen) _port.Open();
                
                PurgePortBuffers();
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Error durante limpieza: {ex.Message}", true);
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
