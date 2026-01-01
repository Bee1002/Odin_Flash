using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Odin_Flash.Util;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Tipo de mensaje para logging (compatibilidad con UI)
    /// </summary>
    public enum MsgType
    {
        Message,  // Mensaje informativo normal
        Result    // Resultado de operación (éxito o error)
    }

    /// <summary>
    /// Nivel de logging para el sistema de eventos
    /// </summary>
    public enum LogLevel
    {
        Info,      // <ID:0/001> Added!! - Información general
        Warning,   // Precaución - Situaciones que requieren atención
        Error,     // Fallo crítico - Errores que detienen la operación
        Success,   // Flasheo finalizado - Operaciones exitosas
        Debug      // Detalles del protocolo Loke - Información técnica
    }
    /// <summary>
    /// Estructuras de resultado para operaciones PIT
    /// </summary>
    public class WritePitResult
    {
        public bool status { get; set; }
        public string error { get; set; }
    }

    public class ReadPitResult
    {
        public bool Result { get; set; }
        public byte[] data { get; set; }
        public object Pit { get; set; } // PIT parseado (opcional)
    }

    /// <summary>
    /// Herramienta para descomprimir y procesar archivos PIT de Samsung
    /// </summary>
    public static class PitTool
    {
        /// <summary>
        /// Descomprime y valida un archivo PIT
        /// </summary>
        /// <param name="pitData">Datos del archivo PIT en bytes</param>
        /// <returns>True si el PIT es válido y se pudo descomprimir</returns>
        public static bool UNPACK_PIT(byte[] pitData)
        {
            if (pitData == null || pitData.Length < 4)
            {
                return false;
            }

            try
            {
                // Verificar magic bytes del PIT
                // Los archivos PIT de Samsung suelen empezar con ciertos bytes mágicos
                // Esto es una validación básica - en una implementación completa se parsearía el PIT
                
                // Magic bytes comunes en PITs de Samsung
                // Algunos PITs tienen compresión, otros no
                if (pitData.Length < 20)
                {
                    return false;
                }

                // Validación básica: verificar que no esté completamente vacío o corrupto
                bool hasData = false;
                for (int i = 0; i < Math.Min(100, pitData.Length); i++)
                {
                    if (pitData[i] != 0x00)
                    {
                        hasData = true;
                        break;
                    }
                }

                return hasData;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Métodos wrapper en OdinEngine para operaciones de flasheo
    /// </summary>
    public static partial class OdinEngineWrappers
    {
        /// <summary>
        /// Detecta y configura el modo Download del dispositivo
        /// </summary>
        public static async Task<bool> FindAndSetDownloadModeAsync()
        {
            var result = await OdinEngine.FindAndSetDownloadModeAsync();
            return result.success;
        }

        /// <summary>
        /// Wrapper para PrintInfo - Imprime información del dispositivo
        /// </summary>
        public static async Task PrintInfoAsync(OdinEngine engine)
        {
            if (engine == null) return;

            try
            {
                // Obtener información básica del dispositivo usando detección WMI robusta
                // Usar GetSamsungDownloadPort() para obtener el puerto real detectado por VID/PID
                string portName = engine.GetSamsungDownloadPort();
                if (portName == "Unknown")
                {
                    // Fallback al puerto actual si la detección WMI falla
                    portName = engine.GetCurrentPort()?.PortName ?? "Unknown";
                }
                engine.ReportLog($"Device Port: {portName}", LogLevel.Info);
                engine.ReportLog($"Protocol: LOKE (Native Implementation)", LogLevel.Info);
            }
            catch
            {
                // Silenciar errores en PrintInfo
            }
        }

        /// <summary>
        /// Verifica si el dispositivo está en modo Odin
        /// </summary>
        public static async Task<bool> IsOdinAsync(OdinEngine engine)
        {
            if (engine == null) return false;
            return await engine.IsOdinModeAsync();
        }

        /// <summary>
        /// Inicializa la comunicación LOKE con el dispositivo
        /// </summary>
        public static async Task<bool> LOKE_InitializeAsync(OdinEngine engine, long size)
        {
            if (engine == null) return false;
            return await engine.InitializeCommunicationAsync();
        }

        /// <summary>
        /// Escribe un archivo PIT al dispositivo
        /// </summary>
        public static async Task<WritePitResult> Write_PitAsync(OdinEngine engine, string pitFilePath)
        {
            var result = new WritePitResult();

            if (engine == null)
            {
                result.status = false;
                result.error = "OdinEngine no inicializado";
                return result;
            }

            if (string.IsNullOrEmpty(pitFilePath) || !File.Exists(pitFilePath))
            {
                result.status = false;
                result.error = "Archivo PIT no encontrado";
                return result;
            }

            try
            {
                bool success = await engine.FlashPitFileAsync(pitFilePath);
                result.status = success;
                if (!success)
                {
                    result.error = "Error al escribir PIT en el dispositivo";
                }
            }
            catch (Exception ex)
            {
                result.status = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Lee el PIT desde el dispositivo
        /// </summary>
        public static async Task<ReadPitResult> Read_PitAsync(OdinEngine engine)
        {
            var result = new ReadPitResult();

            if (engine == null)
            {
                result.Result = false;
                result.data = new byte[0];
                return result;
            }

            try
            {
                byte[] pitData = await engine.ReadPitFromDeviceAsync();
                if (pitData != null && pitData.Length > 0)
                {
                    result.Result = true;
                    result.data = pitData;
                    // Pit puede ser null, se usa para almacenar PIT parseado
                    result.Pit = null;
                }
                else
                {
                    result.Result = false;
                    result.data = new byte[0];
                }
            }
            catch (Exception)
            {
                result.Result = false;
                result.data = new byte[0];
            }

            return result;
        }

        /// <summary>
        /// Flashea firmware al dispositivo
        /// </summary>
        public static async Task<bool> FlashFirmwareAsync(OdinEngine engine, List<FileFlash> files, 
            object pit, int efsClear, int bootUpdate, bool autoReboot)
        {
            if (engine == null || files == null || files.Count == 0)
            {
                return false;
            }

            bool allSuccess = true;

            foreach (var file in files)
            {
                if (!file.Enable) continue;

                try
                {
                    // Verificar si el archivo existe
                    if (!File.Exists(file.FilePath))
                    {
                        engine.ReportLog($"Archivo no encontrado: {file.FilePath}", LogLevel.Error);
                        allSuccess = false;
                        continue;
                    }

                    // Usar SendFileWithLokeProtocol para enviar el archivo
                    bool success = await engine.SendFileWithLokeProtocol(file.FilePath, file.RawSize);
                    if (!success)
                    {
                        allSuccess = false;
                    }
                }
                catch (Exception ex)
                {
                    engine.ReportLog($"Error al flashear {file.FileName}: {ex.Message}", LogLevel.Error);
                    allSuccess = false;
                }
            }

            // Si autoReboot está activado, reiniciar dispositivo
            if (autoReboot && allSuccess)
            {
                await engine.RebootToNormalModeAsync();
            }

            return allSuccess;
        }

        /// <summary>
        /// Reinicia el dispositivo a modo normal
        /// </summary>
        public static async Task<bool> PDAToNormalAsync(OdinEngine engine)
        {
            if (engine == null) return false;
            return await engine.RebootToNormalModeAsync();
        }
    }
}

