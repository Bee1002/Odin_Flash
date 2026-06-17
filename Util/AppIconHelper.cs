using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Odin_Flash.Util
{
    /// <summary>
    /// Carga icon.ico desde la carpeta del exe (evita pack URI window/icon.ico en XAML).
    /// </summary>
    public static class AppIconHelper
    {
        public static void ApplyTo(Window window)
        {
            if (window == null) return;

            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (!File.Exists(path)) return;

                var icon = new BitmapImage();
                icon.BeginInit();
                icon.UriSource = new Uri(path, UriKind.Absolute);
                icon.CacheOption = BitmapCacheOption.OnLoad;
                icon.EndInit();
                icon.Freeze();
                window.Icon = icon;
            }
            catch
            {
                // Sin icono de ventana: el .exe conserva ApplicationIcon embebido.
            }
        }
    }
}
