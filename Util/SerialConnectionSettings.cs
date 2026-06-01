using OdinFlash.Protocol.Port;
using System;
using System.Configuration;
using System.Globalization;
using System.IO.Ports;

namespace Odin_Flash.Util
{
    /// <summary>
    /// Tunables de conexión serial USB Samsung desde App.config (<see cref="App.OnStartup"/>).
    /// Separado de velocidad LOKE y de Calculated Size / GetCmdBuff.
    /// </summary>
    /// <remarks>
    /// Afecta a <see cref="device.RegisterPort"/>, <see cref="device.ApplySamsungUsbWakeSequenceAsync"/>,
    /// <see cref="OdinHandshakeProbe"/> y <see cref="LokeProtocol.PerformHandshakeAsync"/>.
    ///
    /// Si un modelo concreto falla en handshake o DVIF:
    ///   - Timeouts en ODIN/LOKE → subir Serial:OdinMagicReplyDelayMs (ej. 500–600).
    ///   - Timeouts al abrir/wake → subir Serial:WakeAfterLinesHighMs o probar Serial:Handshake=None.
    /// </remarks>
    public static class SerialConnectionSettings
    {
        public const Handshake DefaultSerialHandshake = Handshake.RequestToSend;
        public const int DefaultWakeToggleLowMs = 100;
        public const int DefaultWakeAfterLinesHighMs = 500;
        public const int DefaultOdinMagicReplyDelayMs = 400;

        public static void ApplyFromConfig()
        {
            device.SerialHandshake = ParseHandshake(
                ConfigurationManager.AppSettings["Serial:Handshake"],
                DefaultSerialHandshake);

            device.WakeToggleLowMs = ClampInt(
                ReadInt("Serial:WakeToggleLowMs", DefaultWakeToggleLowMs),
                0, 5000);

            device.WakeAfterLinesHighMs = ClampInt(
                ReadInt("Serial:WakeAfterLinesHighMs", DefaultWakeAfterLinesHighMs),
                0, 10000);

            device.OdinMagicReplyDelayMs = ClampInt(
                ReadInt("Serial:OdinMagicReplyDelayMs", DefaultOdinMagicReplyDelayMs),
                0, 5000);
        }

        private static Handshake ParseHandshake(string raw, Handshake defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "none":
                case "0":
                    return Handshake.None;
                case "requesttosend":
                case "rts":
                    return Handshake.RequestToSend;
                case "xonxoff":
                    return Handshake.XOnXOff;
                case "requesttosendxonxoff":
                    return Handshake.RequestToSendXOnXOff;
                default:
                    if (Enum.TryParse(raw.Trim(), true, out Handshake parsed))
                        return parsed;
                    return defaultValue;
            }
        }

        private static int ReadInt(string key, int defaultValue)
        {
            var raw = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;

            return defaultValue;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}
