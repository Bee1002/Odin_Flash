using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace OdinFlash.Protocol.Port
{
    /// <summary>
    /// Handshake liviano tipo respuesta a magic <c>ODIN</c>, alineado con <see cref="Odin.IsOdin"/>.
    /// </summary>
    public static class LokeProtocol
    {
        public const string MAGIC_LOKE = "LOKE";

        /// <summary>Primer byte que algunos firmwares envían como ACK genérico antes del texto; ajustar si tu traza difiere.</summary>
        public const byte ACK_BYTE = 0x4C;

        /// <summary>
        /// Envía los 4 bytes <c>ODIN</c>, espera y lee con <see cref="SerialPort.ReadExisting"/> (no lectura fija de 4 bytes).
        /// El puerto debe estar abierto; la secuencia DTR/RTS/purge va aparte (p. ej. <see cref="OdinHandshakeProbe.ApplyDtrRtsWakeThenPurgeAsync"/>).
        /// </summary>
        public static async Task<bool> PerformHandshakeAsync(SerialPort port)
        {
            if (port == null || !port.IsOpen)
                return false;

            byte[] odin = { 0x4F, 0x44, 0x49, 0x4E };
            port.Write(odin, 0, odin.Length);
            await Task.Delay(device.OdinMagicReplyDelayMs).ConfigureAwait(false);
            string text = port.ReadExisting();
            return text != null && text.IndexOf(MAGIC_LOKE, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Buffer de 500 bytes con cabecera ASCII (fallback de escaneo).</summary>
        public static byte[] CreateControlPacket(string tag)
        {
            var buf = new byte[500];
            if (string.IsNullOrEmpty(tag))
                return buf;
            byte[] enc = Encoding.ASCII.GetBytes(tag);
            Buffer.BlockCopy(enc, 0, buf, 0, Math.Min(enc.Length, buf.Length));
            return buf;
        }
    }
}
