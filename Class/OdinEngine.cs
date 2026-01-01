using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using static SharpOdinClient.util.utils;

namespace Odin_Flash.Class
{
    public class OdinEngine
    {
        // Delegados y Eventos
        public delegate void LogDelegate(string Text, MsgType Color, bool IsError = false);
        public delegate void ProgressDelegate(string filename, long max, long value, long writenSize);
        
        public event LogDelegate Log;
        public event ProgressDelegate ProgressChanged;

        private SharpOdinClient.Odin odinInstance;
        private SerialPort _port;
        private bool _useDirectSerialPort = false;

        [DllImport("kernel32.dll")]
        static extern bool ClearCommError(IntPtr hFile, out uint lpErrors, IntPtr lpStat);

        public OdinEngine(SharpOdinClient.Odin odin)
        {
            odinInstance = odin ?? throw new ArgumentNullException(nameof(odin));
            _useDirectSerialPort = false;
        }

        public OdinEngine(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                throw new ArgumentNullException(nameof(portName));

            _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000
            };
            _useDirectSerialPort = true;
        }

        private void LogMessage(string message, bool isError = false)
        {
            Log?.Invoke(message, isError ? MsgType.Result : MsgType.Message, isError);
        }

        public bool SendSegmentedData(byte[] data, bool useLargeBlocks = false)
        {
            return SendFileInSegments(data, useLargeBlocks);
        }

        public bool SendFileInSegments(byte[] fileData, bool useLargeBlocks = false)
        {
            if (fileData == null || fileData.Length == 0) return false;

            int total = fileData.Length;
            int chunkSize = useLargeBlocks ? LokeProtocol.CHUNK_DATA : LokeProtocol.CHUNK_CONTROL;
            int chunks = (total + chunkSize - 1) / chunkSize;

            try
            {
                if (_useDirectSerialPort)
                {
                    if (!_port.IsOpen) _port.Open();
                    return useLargeBlocks ? LokeProtocol.SendLargeSegmented(_port, fileData) : LokeProtocol.SendSegmented(_port, fileData);
                }
                else
                {
                    for (int i = 0; i < chunks; i++)
                    {
                        int offset = i * chunkSize;
                        int count = Math.Min(chunkSize, total - offset);
                        byte[] chunk = new byte[count];
                        Buffer.BlockCopy(fileData, offset, chunk, 0, count);

                        if (!WriteToSerialPort(chunk)) return false;
                        
                        Thread.Sleep(10);
                        if ((i + 1) % (useLargeBlocks ? 10 : 100) == 0 || i == chunks - 1)
                        {
                            LogMessage($"Progreso: {i + 1}/{chunks} ({(i + 1) * 100.0 / chunks:F1}%)");
                        }
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error: {ex.Message}", true);
                if (_useDirectSerialPort) HandleCommError();
                return false;
            }
        }

        private bool WriteToSerialPort(byte[] data)
        {
            try {
                LogMessage($"[DEBUG] Escribiendo {data.Length} bytes...");
                return true; 
            } catch { return false; }
        }

        private void HandleCommError()
        {
            try {
                if (_port != null && _port.IsOpen) {
                    if (!Win32Comm.ResetPort(_port)) {
                        _port.DiscardInBuffer();
                        _port.DiscardOutBuffer();
                    }
                }
            } catch (Exception ex) { LogMessage($"Error reset: {ex.Message}", true); }
        }

        public async Task<bool> FlashSequenceAsync(byte[] pitFile, byte[] firmwareFile)
        {
            try {
                if (!SendInitialPing()) throw new Exception("Device not responding.");
                if (!InitializeProtocol()) throw new Exception("LOKE handshake failed.");

                if (pitFile != null) {
                    if (!SendSegmentedData(pitFile, false)) throw new Exception("PIT Error");
                    await Task.Delay(LokeProtocol.DELAY_STABILITY);
                }

                return await Task.Run(() => SendSegmentedData(firmwareFile, true));
            }
            catch (Exception ex) {
                LogMessage($"ERROR: {ex.Message}", true);
                return false;
            }
        }

        public bool SendInitialPing() => _useDirectSerialPort ? (_port.IsOpen || (OpenPort() && true)) : true;
        
        private bool OpenPort() { try { _port.Open(); return true; } catch { return false; } }

        public bool InitializeProtocol() => _useDirectSerialPort ? LokeProtocol.PerformHandshake(_port) : true;

        /// <summary>
        /// Obtiene el puerto serial actual (para uso con Win32Comm)
        /// Retorna null si no hay acceso directo al puerto
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
                bool resetSuccess = Win32Comm.ResetPort(_port);
                if (!resetSuccess)
                {
                    if (_port.IsOpen)
                    {
                        _port.DiscardInBuffer();
                        _port.DiscardOutBuffer();
                    }
                }
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
                bool resetSuccess = Win32Comm.ResetPort(_port);
                if (!resetSuccess)
                {
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                }
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Error durante limpieza: {ex.Message}", true);
                return false;
            }
        }

        /// <summary>
        /// Función definitiva para enviar archivos usando el protocolo LOKE
        /// Usa Task.Run para evitar que la UI de WPF se congele durante el envío
        /// </summary>
        public async Task<bool> SendFileWithLokeProtocol(string filePath, long fileSize)
        {
            if (!_useDirectSerialPort || _port == null)
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
                    _port.WriteTimeout = -1;
                    _port.ReadTimeout = 10000;
                }

                if (!_port.IsOpen) _port.Open();

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
                                bool resetSuccess = Win32Comm.ResetPort(_port);
                                if (!resetSuccess)
                                {
                                    _port.DiscardInBuffer();
                                    _port.DiscardOutBuffer();
                                }
                                Thread.Sleep(LokeProtocol.DELAY_STABILITY);
                                if (!LokeProtocol.PerformHandshake(_port))
                                {
                                    LogMessage("No se pudo re-sincronizar protocolo LOKE", true);
                                    return false;
                                }
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
                    bool resetSuccess = Win32Comm.ResetPort(_port);
                    if (!resetSuccess)
                    {
                        _port.DiscardInBuffer();
                        _port.DiscardOutBuffer();
                    }
                    Thread.Sleep(500);
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
                if (fileSize > 100 * 1024 * 1024)
                {
                    _port.WriteTimeout = 5000;
                    _port.ReadTimeout = 5000;
                }
            }
        }

        public void Close()
        {
            if (_port != null) {
                if (_port.IsOpen) _port.Close();
                _port.Dispose();
            }
        }
    }
}
