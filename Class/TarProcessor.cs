using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Tar;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Información de un archivo dentro de un .tar
    /// </summary>
    public class TarFileInfo
    {
        public string Filename { get; set; }
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
    }

    /// <summary>
    /// Procesador de archivos .tar usando SharpZipLib
    /// Todos los métodos son async para evitar bloqueos
    /// </summary>
    public static class TarProcessor
    {
        /// <summary>
        /// Obtiene la lista de archivos dentro de un archivo .tar
        /// </summary>
        /// <param name="tarFilePath">Ruta al archivo .tar</param>
        /// <returns>Lista de información de archivos dentro del .tar</returns>
        public static async Task<List<TarFileInfo>> GetTarFileListAsync(string tarFilePath)
        {
            if (string.IsNullOrEmpty(tarFilePath) || !File.Exists(tarFilePath))
            {
                return new List<TarFileInfo>();
            }

            List<TarFileInfo> fileList = new List<TarFileInfo>();

            try
            {
                await Task.Run(() =>
                {
                    using (FileStream fs = File.OpenRead(tarFilePath))
                    using (TarInputStream tarStream = new TarInputStream(fs))
                    {
                        TarEntry entry;
                        while ((entry = tarStream.GetNextEntry()) != null)
                        {
                            fileList.Add(new TarFileInfo
                            {
                                Filename = entry.Name,
                                Size = entry.Size,
                                IsDirectory = entry.IsDirectory
                            });
                        }
                    }
                });

                return fileList;
            }
            catch (Exception)
            {
                return new List<TarFileInfo>();
            }
        }

        /// <summary>
        /// Extrae un archivo específico de un .tar a memoria
        /// Reemplaza Odin.tar.ExtractFileFromTar()
        /// </summary>
        /// <param name="tarFilePath">Ruta al archivo .tar</param>
        /// <param name="fileName">Nombre del archivo a extraer</param>
        /// <returns>Array de bytes con el contenido del archivo, o array vacío si no se encuentra</returns>
        public static async Task<byte[]> ExtractFileFromTarAsync(string tarFilePath, string fileName)
        {
            if (string.IsNullOrEmpty(tarFilePath) || !File.Exists(tarFilePath))
            {
                return new byte[0];
            }

            if (string.IsNullOrEmpty(fileName))
            {
                return new byte[0];
            }

            try
            {
                byte[] fileData = null;

                await Task.Run(() =>
                {
                    using (FileStream fs = File.OpenRead(tarFilePath))
                    using (TarInputStream tarStream = new TarInputStream(fs))
                    {
                        TarEntry entry;
                        while ((entry = tarStream.GetNextEntry()) != null)
                        {
                            // Comparar nombre del archivo (case-insensitive)
                            if (entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!entry.IsDirectory && entry.Size > 0)
                                {
                                    fileData = new byte[entry.Size];
                                    int bytesRead = 0;
                                    int totalRead = 0;

                                    // Leer el archivo completo
                                    while (totalRead < entry.Size)
                                    {
                                        bytesRead = tarStream.Read(fileData, totalRead, (int)entry.Size - totalRead);
                                        if (bytesRead == 0)
                                            break;
                                        totalRead += bytesRead;
                                    }

                                    break; // Archivo encontrado, salir del bucle
                                }
                            }
                        }
                    }
                });

                return fileData ?? new byte[0];
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }

        /// <summary>
        /// Procesa un archivo .tar y ejecuta una acción para cada archivo encontrado
        /// Permite procesar archivos sin extraerlos a disco
        /// </summary>
        /// <param name="tarFilePath">Ruta al archivo .tar</param>
        /// <param name="fileAction">Acción a ejecutar para cada archivo (nombre, tamaño, stream)</param>
        /// <returns>True si el procesamiento fue exitoso</returns>
        public static async Task<bool> ProcessTarFileAsync(string tarFilePath, 
            Func<string, long, Stream, Task<bool>> fileAction)
        {
            if (string.IsNullOrEmpty(tarFilePath) || !File.Exists(tarFilePath))
            {
                return false;
            }

            try
            {
                await Task.Run(async () =>
                {
                    using (FileStream fs = File.OpenRead(tarFilePath))
                    using (TarInputStream tarStream = new TarInputStream(fs))
                    {
                        TarEntry entry;
                        while ((entry = tarStream.GetNextEntry()) != null)
                        {
                            if (entry.IsDirectory)
                                continue;

                            // Crear un stream wrapper que solo permita leer el tamaño del entry
                            Stream entryStream = new TarEntryStream(tarStream, entry.Size);

                            bool continueProcessing = await fileAction(entry.Name, entry.Size, entryStream);
                            if (!continueProcessing)
                            {
                                break; // Detener procesamiento si la acción retorna false
                            }
                        }
                    }
                });

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Stream wrapper que limita la lectura al tamaño del entry del tar
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
                long remaining = _size - _position;
                if (remaining <= 0)
                    return 0;

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

