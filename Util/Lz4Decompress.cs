using System;
using System.IO;
using K4os.Compression.LZ4.Streams;

namespace Odin_Flash.Util
{
    /// <summary>
    /// Utilidad para descomprimir archivos .lz4 usando streams
    /// Evita cargar todo el archivo en RAM, especialmente importante para archivos grandes como super.img.lz4
    /// Optimizado para equipos nuevos con procesadores Dimensity/MediaTek que usan .lz4
    /// </summary>
    public static class Lz4Decompress
    {
        /// <summary>
        /// Descomprime un archivo .lz4 a un archivo temporal usando streams
        /// No carga todo el archivo en RAM, sino que descomprime vía Stream
        /// </summary>
        /// <param name="lz4FilePath">Ruta al archivo .lz4 comprimido</param>
        /// <param name="outputPath">Ruta donde guardar el archivo descomprimido (opcional, si es null se crea temporal)</param>
        /// <returns>Ruta al archivo descomprimido</returns>
        public static string DecompressToFile(string lz4FilePath, string outputPath = null)
        {
            if (string.IsNullOrEmpty(lz4FilePath) || !File.Exists(lz4FilePath))
                throw new FileNotFoundException($"Archivo .lz4 no encontrado: {lz4FilePath}");

            // Si no se especifica output, crear archivo temporal
            if (string.IsNullOrEmpty(outputPath))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "OdinFlash");
                Directory.CreateDirectory(tempDir);
                string originalName = Path.GetFileNameWithoutExtension(lz4FilePath);
                outputPath = Path.Combine(tempDir, $"{originalName}_{Guid.NewGuid()}");
            }

            try
            {
                using (FileStream inputStream = new FileStream(lz4FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true))
                using (Stream lz4Stream = LZ4Stream.Decode(inputStream))
                using (FileStream outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
                {
                    // Buffer de 128KB para coincidir con CHUNK_DATA de LokeProtocol
                    byte[] buffer = new byte[131072];
                    int bytesRead;
                    long totalDecompressed = 0;

                    while ((bytesRead = lz4Stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outputStream.Write(buffer, 0, bytesRead);
                        totalDecompressed += bytesRead;
                    }

                    outputStream.Flush();
                }

                return outputPath;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al descomprimir archivo .lz4: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Crea un stream de descompresión para leer un archivo .lz4 sin descomprimirlo completamente
        /// Útil para enviar datos directamente desde el stream comprimido
        /// </summary>
        /// <param name="lz4FilePath">Ruta al archivo .lz4</param>
        /// <returns>Stream de descompresión que puede leerse directamente</returns>
        public static Stream CreateDecompressionStream(string lz4FilePath)
        {
            if (string.IsNullOrEmpty(lz4FilePath) || !File.Exists(lz4FilePath))
                throw new FileNotFoundException($"Archivo .lz4 no encontrado: {lz4FilePath}");

            FileStream inputStream = new FileStream(lz4FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
            Stream lz4Stream = LZ4Stream.Decode(inputStream, leaveOpen: false);

            return lz4Stream;
        }

        /// <summary>
        /// Verifica si un archivo es .lz4 comprimido
        /// </summary>
        /// <param name="filePath">Ruta al archivo</param>
        /// <returns>True si el archivo tiene extensión .lz4</returns>
        public static bool IsLz4File(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            return Path.GetExtension(filePath).Equals(".lz4", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Obtiene el tamaño descomprimido estimado de un archivo .lz4
        /// Nota: LZ4 no almacena el tamaño original en el header, así que esto es una estimación
        /// </summary>
        /// <param name="lz4FilePath">Ruta al archivo .lz4</param>
        /// <returns>Tamaño estimado descomprimido (puede no ser exacto)</returns>
        public static long GetEstimatedDecompressedSize(string lz4FilePath)
        {
            if (string.IsNullOrEmpty(lz4FilePath) || !File.Exists(lz4FilePath))
                return 0;

            FileInfo fileInfo = new FileInfo(lz4FilePath);
            // LZ4 típicamente tiene una ratio de compresión de 2:1 a 4:1
            // Usamos 3:1 como estimación conservadora
            return fileInfo.Length * 3;
        }
    }
}


