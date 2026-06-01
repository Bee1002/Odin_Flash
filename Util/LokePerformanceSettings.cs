using OdinFlash.Protocol;
using OdinFlash.Protocol.Port;
using System;
using System.Configuration;
using System.Globalization;

namespace Odin_Flash.Util
{
    /// <summary>
    /// Tunables LOKE desde App.config (<see cref="ApplyFromConfig"/> al arranque)
    /// y perfil por Capa DVIF (<see cref="ApplyCapaRuntimeProfile"/> tras conectar).
    /// </summary>
    /// <remarks>
    /// Dos niveles según Capa (umbral 128):
    ///   Capa ≥ 128 — paquete NAND/LOKE 0x64/5 configurable (512 KB en auto; A326B ~7–8 min).
    ///   Capa &lt; 128 — paquete fijo 1 MiB (<see cref="Odin.FlashChunkBytes"/>); delay inter-partición mayor (G950F).
    ///
    /// El fix G950F es alinear LOKE 0x64/5 con el trozo NAND real vía <see cref="Odin.LokeSessionPacketBytes"/>,
    /// no un «modo legacy» aparte con lógica duplicada.
    /// </remarks>
    public static class LokePerformanceSettings
    {
        public const int DefaultFlashAckDelayMsHighCapaMin = 3;
        public const int DefaultNandPacketBytesHighCapa = 262144;
        public const int DefaultInterPartitionDelayMs = 50;
        public const int DefaultInterPartitionDelayMsLowCapa = 100;
        public const int DefaultSerialBufferSize = 4096;
        public const int DefaultProgressThrottleMs = 0;

        public static int ProgressThrottleMs { get; private set; } = DefaultProgressThrottleMs;

        private static int _flashAckDelayMsHighCapaMin = DefaultFlashAckDelayMsHighCapaMin;
        private static int _nandPacketBytesHighCapa = DefaultNandPacketBytesHighCapa;
        private static int _interPartitionDelayMsHighCapa = DefaultInterPartitionDelayMs;
        private static int _interPartitionDelayMsLowCapa = DefaultInterPartitionDelayMsLowCapa;

        public static void ApplyFromConfig()
        {
            _flashAckDelayMsHighCapaMin = ClampInt(
                ReadInt("Loke:FlashAckDelayMsHighCapaMin", DefaultFlashAckDelayMsHighCapaMin),
                0, 100);
            _nandPacketBytesHighCapa = ClampInt(
                ReadInt("Loke:NandPacketBytesHighCapa", DefaultNandPacketBytesHighCapa),
                4096, 1048576);
            _interPartitionDelayMsHighCapa = ClampInt(
                ReadInt("Loke:InterPartitionDelayMs", DefaultInterPartitionDelayMs),
                0, 5000);
            _interPartitionDelayMsLowCapa = ClampInt(
                ReadInt("Loke:InterPartitionDelayMsLegacy", DefaultInterPartitionDelayMsLowCapa),
                0, 5000);

            Odin.FlashAckDelayMsHighCapaMin = _flashAckDelayMsHighCapaMin;
            Odin.NandPacketBytesWhenHighCapa = _nandPacketBytesHighCapa;

            var capaThreshold = ReadInt("Loke:NandSmallPacketCapaThreshold", Odin.NandSmallPacketCapaThreshold);
            if (capaThreshold > 0)
                Odin.NandSmallPacketCapaThreshold = capaThreshold;

            var chunkBytes = ReadInt("Loke:FlashChunkBytes", Odin.FlashChunkBytes);
            if (chunkBytes >= 4096 && chunkBytes <= 1048576)
                Odin.FlashChunkBytes = chunkBytes;

            device.SerialReadBufferSize = ClampInt(
                ReadInt("Serial:ReadBufferSize", DefaultSerialBufferSize),
                4096, 1048576);

            device.SerialWriteBufferSize = ClampInt(
                ReadInt("Serial:WriteBufferSize", DefaultSerialBufferSize),
                4096, 1048576);

            ProgressThrottleMs = ClampInt(
                ReadInt("Ui:ProgressThrottleMs", DefaultProgressThrottleMs),
                0, 2000);
        }

        /// <summary>
        /// Tras DVIF: fija paquete LOKE/NAND e inter-partición según Capa. Llamar antes de SetFlashTotal y flash.
        /// </summary>
        public static void ApplyCapaRuntimeProfile(int? capa)
        {
            bool highCapa = capa.HasValue && capa.Value >= Odin.NandSmallPacketCapaThreshold;

            if (highCapa)
            {
                ApplyHighCapaNandTuning();
                Odin.InterPartitionDelayMs = _interPartitionDelayMsHighCapa;
                Odin.LokeSessionPacketBytes = Odin.NandPacketBytesWhenHighCapa;
            }
            else
            {
                // Capa &lt; 128 o sin DVIF: 1 MiB (LOKE 0x64/5 debe coincidir con SendAsync — fix G950F).
                Odin.InterPartitionDelayMs = _interPartitionDelayMsLowCapa;
                Odin.LokeSessionPacketBytes = Odin.FlashChunkBytes;
            }
        }

        private static void ApplyHighCapaNandTuning()
        {
            switch (ReadHighCapaProfile())
            {
                case HighCapaProfileKind.Fast:
                case HighCapaProfileKind.Auto:
                    Odin.FlashAckDelayMsHighCapaMin = _flashAckDelayMsHighCapaMin;
                    Odin.NandPacketBytesWhenHighCapa = _nandPacketBytesHighCapa;
                    break;
                case HighCapaProfileKind.Safe:
                    Odin.FlashAckDelayMsHighCapaMin = 3;
                    Odin.NandPacketBytesWhenHighCapa = 262144;
                    break;
                default:
                    Odin.FlashAckDelayMsHighCapaMin = 1;
                    Odin.NandPacketBytesWhenHighCapa = 524288;
                    break;
            }
        }

        private enum HighCapaProfileKind
        {
            Auto,
            Balanced,
            Fast,
            Safe
        }

        private static HighCapaProfileKind ReadHighCapaProfile()
        {
            var raw = ConfigurationManager.AppSettings["Loke:HighCapaProfile"];
            if (string.IsNullOrWhiteSpace(raw))
                return HighCapaProfileKind.Auto;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "fast":
                    return HighCapaProfileKind.Fast;
                case "balanced":
                    return HighCapaProfileKind.Balanced;
                case "safe":
                    return HighCapaProfileKind.Safe;
                default:
                    return HighCapaProfileKind.Auto;
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
