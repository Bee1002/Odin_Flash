using System;
using System.IO;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Tar;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Parser para archivos .tar de firmware Samsung
    /// Extrae y flashea archivos directamente desde el .tar sin guardar en disco
    /// Usa SharpZipLib (estándar y abierta) para procesamiento de archivos .tar
    /// </summary>
    public static class OdinParser
    {
        /// <summary>
        /// Extrae y flashea todos los archivos de un .tar directamente al dispositivo
        /// Sin necesidad de guardar archivos en disco - procesamiento en memoria
        /// </summary>
        /// <param name="tarPath">Ruta al archivo .tar de firmware</param>
        /// <param name="engine">Instancia de OdinEngine para flashear</param>
        /// <returns>True si todos los archivos se flashearon exitosamente</returns>
        public static async Task<bool> ExtractAndFlash(string tarPath, OdinEngine engine)
        {
            if (string.IsNullOrEmpty(tarPath) || !File.Exists(tarPath))
            {
                engine?.ReportLog($"Archivo .tar no encontrado: {tarPath}", LogLevel.Error);
                return false;
            }

            if (engine == null)
            {
                return false;
            }

            try
            {
                using (FileStream fs = File.OpenRead(tarPath))
                using (TarInputStream tarStream = new TarInputStream(fs))
                {
                    TarEntry entry;
                    int fileCount = 0;
                    int successCount = 0;

                    while ((entry = tarStream.GetNextEntry()) != null)
                    {
                        // Saltar directorios
                        if (entry.IsDirectory)
                            continue;

                        // Saltar archivos vacíos
                        if (entry.Size <= 0)
                            continue;

                        fileCount++;
                        string fileName = entry.Name;

                        try
                        {
                            engine.ReportLog($"Procesando archivo {fileCount}: {fileName} ({entry.Size / (1024.0 * 1024.0):F2} MB)", LogLevel.Info);

                            // Crear un stream wrapper que limita la lectura al tamaño del entry
                            // Esto asegura que solo leamos el archivo actual, no el siguiente
                            using (var entryStream = new TarEntryStream(tarStream, entry.Size))
                            {
                                // Enviar el stream directamente al motor sin guardar en disco
                                bool success = await engine.FlashStreamAsync(entryStream, entry.Size, fileName);
                                
                                if (success)
                                {
                                    successCount++;
                                    engine.ReportLog($"Archivo {fileName} flasheado exitosamente", LogLevel.Success);
                                }
                                else
                                {
                                    engine.ReportLog($"Error al flashear archivo: {fileName}", LogLevel.Error);
                                    // Continuar con el siguiente archivo en lugar de detener todo
                                }
                            }

                            // Pequeño delay entre archivos para estabilidad
                            await Task.Delay(500);
                        }
                        catch (Exception ex)
                        {
                            engine.ReportLog($"Excepción al procesar {fileName}: {ex.Message}", LogLevel.Error);
                            // Continuar con el siguiente archivo
                        }
                    }

                    engine.ReportLog($"Procesamiento completado: {successCount}/{fileCount} archivos flasheados", 
                        successCount == fileCount ? LogLevel.Success : LogLevel.Warning);

                    return successCount == fileCount;
                }
            }
            catch (Exception ex)
            {
                engine?.ReportLog($"Error crítico al procesar .tar: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Stream wrapper que limita la lectura al tamaño del entry del tar
        /// Asegura que solo se lea el archivo actual, no el siguiente entry
        /// </summary>
        private class TarEntryStream : Stream
        {
            private readonly TarInputStream _tarStream;
            private readonly long _size;
            private long _position;

            public TarEntryStream(TarInputStream tarStream, long size)
            {
                _tarStream = tarStream;
                _size = size;
                _position = 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _size;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _size)
                    return 0;

                long remaining = _size - _position;
                int bytesToRead = (int)Math.Min(count, remaining);
                int bytesRead = _tarStream.Read(buffer, offset, bytesToRead);
                _position += bytesRead;
                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
        }
    }
}


