using OdinFlash.Protocol.structs;
using OdinFlash.Protocol.util;
using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace OdinFlash.Protocol.Port
{
    public class device
    {
        /// <summary>
        /// port of selected devices;
        /// </summary>
        public SerialPort Port;

        /// <summary>Candado: serializar lecturas/escrituras (antes mezclaban Task.Run y Port.Read directo en Cmd).</summary>
        private readonly SemaphoreSlim _serialIo = new SemaphoreSlim(1, 1);

        /// <summary>USB Samsung: muchos equipos van bien con RTS; si hay timeouts en handshake, prueba <see cref="Handshake.None"/>.</summary>
        public static Handshake SerialHandshake { get; set; } = Handshake.RequestToSend;

        /// <summary>Delay tras bajar DTR/RTS antes de subirlos (misma idea que escaneo Odin).</summary>
        public static int WakeToggleLowMs { get; set; } = 100;

        /// <summary>Delay tras subir DTR/RTS antes de purgar buffers RX/TX.</summary>
        public static int WakeAfterLinesHighMs { get; set; } = 500;

        /// <summary>Tras enviar magic ODIN: espera antes de <see cref="SerialPort.ReadExisting"/> (<see cref="Odin.IsOdin"/>, <see cref="LokeProtocol.PerformHandshakeAsync"/>).</summary>
        public static int OdinMagicReplyDelayMs { get; set; } = 400;

        /// <summary>
        /// Set your serialport
        /// </summary>
        /// <param name="portName"></param>
        /// <returns>true if port can be opened</returns>
        public bool RegisterPort(ItypePort portName)
        {
            Close();
            (Port = new SerialPort(portName.COM)).BaudRate = 115200;
            Port.Parity = Parity.None;
            Port.DataBits = 8;
            Port.StopBits = StopBits.One;
            Port.Handshake = SerialHandshake;
            Port.DtrEnable = false;
            SetRtsIfManual(false);
            Port.ReadTimeout = 120000;
            Port.WriteTimeout = 120000;
            Port.ReadBufferSize = 4096;
            Port.WriteBufferSize = 4096;

            return Open();
        }

        /// <summary>
        /// Secuencia DTR/RTS → espera → purge, serializada con el resto de E/S del puerto (evita interferencias con Cmd/flash).
        /// </summary>
        public async Task ApplySamsungUsbWakeSequenceAsync()
        {
            await _serialIo.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Port == null || !Port.IsOpen)
                    return;
                Port.DtrEnable = false;
                SetRtsIfManual(false);
                await Task.Delay(WakeToggleLowMs).ConfigureAwait(false);
                Port.DtrEnable = true;
                SetRtsIfManual(true);
                await Task.Delay(WakeAfterLinesHighMs).ConfigureAwait(false);
                try
                {
                    Port.DiscardInBuffer();
                    Port.DiscardOutBuffer();
                }
                catch { }
            }
            finally
            {
                _serialIo.Release();
            }
        }


        /// <summary>
        /// write on seriaport
        /// </summary>
        /// <param name="data">buffer u want to write on device</param>
        /// <param name="len">length of buffer</param>
        /// <returns></returns>
        public async Task WritePort(byte[] data, int len)
        {
            await _serialIo.WaitAsync().ConfigureAwait(false);
            try
            {
                if (utils.Stop) throw new Exception("Stopped By User");
                EnsurePortOpen();
                if (data == null)
                    throw new ArgumentNullException(nameof(data));
                if (len < 0 || len > data.Length)
                    throw new ArgumentOutOfRangeException(nameof(len), "Write length is outside the buffer.");
                Port.Write(data, 0, len);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Serial write timed out after {Port.WriteTimeout} ms while writing {len} bytes.", ex);
            }
            catch (IOException ex)
            {
                throw new IOException($"Serial write failed while writing {len} bytes.", ex);
            }
            finally
            {
                _serialIo.Release();
            }
        }

        public async Task<int> ReadExactAsync(byte[] buffer, int offset, int count)
        {
            await _serialIo.WaitAsync().ConfigureAwait(false);
            try
            {
                if (utils.Stop) throw new Exception("Stopped By User");
                EnsurePortOpen();
                return ReadExactUnlocked(buffer, offset, count);
            }
            finally
            {
                _serialIo.Release();
            }
        }

        /// <summary>
        /// Lectura de texto disponible (DVIF / ODIN).
        /// </summary>
        public async Task<string> ReadExistingAsync()
        {
            await _serialIo.WaitAsync().ConfigureAwait(false);
            try
            {
                return Port.ReadExisting();
            }
            finally
            {
                _serialIo.Release();
            }
        }

        /// <summary>Vacía RX (útil antes del primer bloque NAND si quedó basura en buffer).</summary>
        public async Task DiscardInBufferAsync()
        {
            await _serialIo.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Port != null && Port.IsOpen)
                    Port.DiscardInBuffer();
            }
            finally
            {
                _serialIo.Release();
            }
        }

        /// <summary>
        /// open Registered Port;
        /// </summary>
        /// <returns>true if can be opened</returns>
        private bool Open()
        {
            Port.Open();
            return true;
        }

        public void Close()
        {
            try
            {
                if (Port != null)
                {
                    if (Port.IsOpen)
                        Port.Close();
                    Port.Dispose();
                }
            }
            catch { }
            finally
            {
                Port = null;
            }
        }


        /// <summary>
        /// Read Response
        /// </summary>
        /// <param name="len">length of you needed</param>
        /// <returns>byte array</returns>
        /// <exception cref="Exception"></exception>
        public async Task<byte[]> ReadPort(int len = 0)
        {
            await _serialIo.WaitAsync().ConfigureAwait(false);
            try
            {
                if (utils.Stop) throw new Exception("Stopped By User");
                EnsurePortOpen();
                if (len != 0)
                {
                    var recvBuf = new byte[len];
                    ReadExactUnlocked(recvBuf, 0, len);
                    return recvBuf;
                }
                else
                {
                    var read = Port.BytesToRead;
                    if (read > 0)
                    {
                        var recvBuf = new byte[read];
                        Port.Read(recvBuf, 0, read);
                        return recvBuf;
                    }
                }
                return null;
            }
            finally
            {
                _serialIo.Release();
            }
        }

        private void EnsurePortOpen()
        {
            if (Port == null || !Port.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");
        }

        private void SetRtsIfManual(bool enabled)
        {
            if (Port == null)
                return;
            if (Port.Handshake == Handshake.RequestToSend || Port.Handshake == Handshake.RequestToSendXOnXOff)
                return;
            Port.RtsEnable = enabled;
        }

        private int ReadExactUnlocked(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "Read range is outside the buffer.");

            int total = 0;
            while (total < count)
            {
                if (utils.Stop)
                    throw new Exception("Stopped By User");

                int read;
                try
                {
                    read = Port.Read(buffer, offset + total, count - total);
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException($"Serial read timed out after {Port.ReadTimeout} ms. Expected {count} bytes, received {total}.", ex);
                }
                catch (IOException ex)
                {
                    throw new IOException($"Serial read failed. Expected {count} bytes, received {total}.", ex);
                }

                if (read <= 0)
                    throw new IOException($"Serial read returned {read}. Expected {count} bytes, received {total}.");

                total += read;
            }

            return total;
        }



    }
}
