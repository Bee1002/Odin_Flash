using OdinFlash.Protocol;
using OdinFlash.Protocol.Port;
using System;
using System.Configuration;
using System.Globalization;

namespace Odin_Flash.Util
{
    /// <summary>
    /// Aplica tunables de velocidad LOKE desde App.config al arranque (<see cref="App.OnStartup"/>).
    /// </summary>
    /// <remarks>
    /// Separado a propósito de Calculated Size / GetCmdBuff / LOKE_Initialize (barra teléfono).
    ///
    /// Capa del dispositivo (DVIF):
    ///   - Capa &lt; 128: SendAsync usa FlashChunkBytes (1 MiB) sin delay extra de Capa alta.
    ///   - Capa &gt;= 128: paquete limitado por NandPacketBytesWhenHighCapa y opcional FlashAckDelayMsHighCapaMin.
    ///
    /// Referencia real (mismo cable/USB):
    ///   SM-A326B, 8,521 GB, Capa 128: ~17m → ~8m tras perfil fast (MediaTek).
    ///   SM-G950F, 5,695 GB, Capa 64:  ~5m 41s (sobre todo gana delay inter-partición + UI + buffers).
    ///
    /// Perfil Capa alta (<see cref="ApplyHighCapaRuntimeProfile"/> tras DVIF):
    ///   fast     — claves Loke:FlashAckDelayMsHighCapaMin / NandPacketBytesHighCapa (A326B validado).
    ///   balanced — 1 ms ACK + 512 KB (default; mejor compatibilidad Qualcomm Capa 128, ej. A235M).
    ///   safe     — 3 ms + 256 KB (rollback conservador).
    ///
    /// Siguiente paso opcional (solo si A326B sigue estable en fast): NandPacketBytesHighCapa=1048576.
    /// No implementar aquí: LZ4 comprimido por cable (requiere investigación LOKE aparte).
    /// </remarks>
    public static class LokePerformanceSettings
    {
        /// <summary>Default código si falta clave en App.config (perfil conservador pre-optimización).</summary>
        public const int DefaultFlashAckDelayMsHighCapaMin = 3;

        public const int DefaultNandPacketBytesHighCapa = 262144;
        public const int DefaultInterPartitionDelayMs = 300;
        public const int DefaultSerialBufferSize = 4096;

        /// <summary>0 = actualizar progreso en cada trozo NAND (sin throttle).</summary>
        public const int DefaultProgressThrottleMs = 0;

        public static int ProgressThrottleMs { get; private set; } = DefaultProgressThrottleMs;

        private static int _fastFlashAckDelayMsHighCapaMin = DefaultFlashAckDelayMsHighCapaMin;
        private static int _fastNandPacketBytesHighCapa = DefaultNandPacketBytesHighCapa;

        public static void ApplyFromConfig()
        {
            _fastFlashAckDelayMsHighCapaMin = ClampInt(
                ReadInt("Loke:FlashAckDelayMsHighCapaMin", DefaultFlashAckDelayMsHighCapaMin),
                0, 100);
            _fastNandPacketBytesHighCapa = ClampInt(
                ReadInt("Loke:NandPacketBytesHighCapa", DefaultNandPacketBytesHighCapa),
                4096, 1048576);

            Odin.FlashAckDelayMsHighCapaMin = _fastFlashAckDelayMsHighCapaMin;
            Odin.NandPacketBytesWhenHighCapa = _fastNandPacketBytesHighCapa;

            Odin.InterPartitionDelayMs = ClampInt(
                ReadInt("Loke:InterPartitionDelayMs", DefaultInterPartitionDelayMs),
                0, 5000);

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
        /// Tras leer Capa en DVIF: solo Capa ≥ umbral. Capa &lt; 128 no se toca (1 MiB, sin delay Capa alta).
        /// </summary>
        public static void ApplyHighCapaRuntimeProfile(int? capa)
        {
            if (!capa.HasValue || capa.Value < Odin.NandSmallPacketCapaThreshold)
                return;

            switch (ReadHighCapaProfile())
            {
                case HighCapaProfileKind.Fast:
                    Odin.FlashAckDelayMsHighCapaMin = _fastFlashAckDelayMsHighCapaMin;
                    Odin.NandPacketBytesWhenHighCapa = _fastNandPacketBytesHighCapa;
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
            Balanced,
            Fast,
            Safe
        }

        private static HighCapaProfileKind ReadHighCapaProfile()
        {
            var raw = ConfigurationManager.AppSettings["Loke:HighCapaProfile"];
            if (string.IsNullOrWhiteSpace(raw))
                return HighCapaProfileKind.Balanced;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "fast":
                    return HighCapaProfileKind.Fast;
                case "safe":
                    return HighCapaProfileKind.Safe;
                default:
                    return HighCapaProfileKind.Balanced;
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
