using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Odin_Flash.Util
{
    public delegate void IsRunningProcessDelegate(bool IsRunning, string process);

    public delegate void FlashCompletedDelegate(TimeSpan elapsed);

    /// <summary>Utilidades de Odin_Flash (rutas, formato de log).</summary>
    public class Util
    {
        /// <summary>Cultura para el log estilo Odin (ej. 9,258 GB).</summary>
        public static readonly CultureInfo OdinLogCulture = CultureInfo.GetCultureInfo("es-ES");

        public static string MyPath =>
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        /// <summary>Versión de la aplicación (desde AssemblyInfo, ej. 1.0.1).</summary>
        public static string AppVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "1.0.1" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public static void CreatFolder(string name)
        {
            try
            {
                if (!Directory.Exists(name))
                    Directory.CreateDirectory(name);
            }
            catch { }
        }

        public static string GetBytesReadable(long i)
        {
            long absolute_i = i < 0 ? -i : i;
            string suffix;
            double readable;
            if (absolute_i >= 0x1000000000000000)
            {
                suffix = "EB";
                readable = i >> 50;
            }
            else if (absolute_i >= 0x4000000000000)
            {
                suffix = "PB";
                readable = i >> 40;
            }
            else if (absolute_i >= 0x10000000000)
            {
                suffix = "TB";
                readable = i >> 30;
            }
            else if (absolute_i >= 0x40000000)
            {
                suffix = "GB";
                readable = i >> 20;
            }
            else if (absolute_i >= 0x100000)
            {
                suffix = "MB";
                readable = i >> 10;
            }
            else if (absolute_i >= 0x400)
            {
                suffix = "KB";
                readable = i;
            }
            else
                return i.ToString("0 B");

            readable /= 1024;
            return readable.ToString("0.### ") + suffix;
        }

        /// <summary>Tamaño total del paquete en GB con tres decimales (estilo ventana Odin).</summary>
        public static string FormatCalculatedSizeGbOdin(long bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb.ToString("F3", OdinLogCulture) + " GB";
        }

        /// <summary>Tiempo transcurrido estilo Odin (ej. 04m: 28s).</summary>
        public static string FormatOdinElapsedTime(TimeSpan elapsed)
        {
            var minutes = (int)elapsed.TotalMinutes;
            return $"{minutes:D2}m: {elapsed.Seconds:D2}s";
        }

        /// <summary>Formato Odin clásico: 04m: 28s</summary>
        public static string FormatElapsedOdin(TimeSpan elapsed)
        {
            return $"{(int)elapsed.TotalMinutes:00}m: {elapsed.Seconds:00}s";
        }
    }
}
