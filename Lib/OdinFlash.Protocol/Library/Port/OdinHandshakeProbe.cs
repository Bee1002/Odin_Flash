using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace OdinFlash.Protocol.Port
{
    /// <summary>
    /// Escaneo COM si WMI no encuentra "Samsung Mobile USB Modem". Usa los mismos tiempos que <see cref="device"/>.
    /// </summary>
    public static class OdinHandshakeProbe
    {
        /// <summary>Timeout ms para el fallback <c>Read(..., 4)</c>.</summary>
        public static int FallbackReadFourTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Recorre COM y devuelve el primero que valida Odin (<see cref="LokeProtocol.PerformHandshakeAsync"/> o fallback).
        /// </summary>
        public static async Task<string> DetectOdinPortAsync()
        {
            foreach (string portName in SerialPort.GetPortNames())
            {
                try
                {
                    using (var testPort = CreateScanPort(portName))
                    {
                        testPort.Open();
                        await ApplyDtrRtsWakeThenPurgeAsync(testPort).ConfigureAwait(false);

                        try
                        {
                            if (await LokeProtocol.PerformHandshakeAsync(testPort).ConfigureAwait(false))
                                return portName;
                        }
                        catch { }

                        try
                        {
                            byte[] buffer = LokeProtocol.CreateControlPacket("ODIN");
                            testPort.Write(buffer, 0, Math.Min(buffer.Length, 500));
                            await Task.Delay(100).ConfigureAwait(false);

                            testPort.ReadTimeout = FallbackReadFourTimeoutMs;
                            var response = new byte[4];
                            int read = 0;
                            try
                            {
                                read = testPort.Read(response, 0, 4);
                            }
                            catch (TimeoutException)
                            {
                                read = 0;
                            }

                            string responseStr = Encoding.ASCII.GetString(response, 0, Math.Max(0, Math.Min(4, read)));
                            if (read > 0 &&
                                (responseStr.IndexOf(LokeProtocol.MAGIC_LOKE, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 response[0] == LokeProtocol.ACK_BYTE))
                                return portName;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>Prueba un único puerto ya abierto.</summary>
        public static async Task<bool> TryOdinPingOnOpenPortAsync(SerialPort port)
        {
            if (port == null || !port.IsOpen)
                return false;
            await ApplyDtrRtsWakeThenPurgeAsync(port).ConfigureAwait(false);
            return await LokeProtocol.PerformHandshakeAsync(port).ConfigureAwait(false);
        }

        /// <summary>Bajar DTR/RTS → esperar → subir → esperar → purgar (usa tiempos <see cref="device"/>).</summary>
        public static async Task ApplyDtrRtsWakeThenPurgeAsync(SerialPort port)
        {
            port.DtrEnable = false;
            SetRtsIfManual(port, false);
            await Task.Delay(device.WakeToggleLowMs).ConfigureAwait(false);
            port.DtrEnable = true;
            SetRtsIfManual(port, true);
            await Task.Delay(device.WakeAfterLinesHighMs).ConfigureAwait(false);
            try
            {
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
            }
            catch { }
        }

        private static SerialPort CreateScanPort(string portName)
        {
            var port = new SerialPort(portName, 115200)
            {
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = device.SerialHandshake,
                ReadTimeout = 1500,
                WriteTimeout = 1000,
                ReadBufferSize = device.SerialReadBufferSize,
                WriteBufferSize = device.SerialWriteBufferSize,
                DtrEnable = false
            };
            SetRtsIfManual(port, false);
            return port;
        }

        private static void SetRtsIfManual(SerialPort port, bool enabled)
        {
            if (port == null)
                return;
            if (port.Handshake == Handshake.RequestToSend || port.Handshake == Handshake.RequestToSendXOnXOff)
                return;
            port.RtsEnable = enabled;
        }
    }
}
