using Odin_Flash.Util;
using System.Windows;

namespace Odin_Flash
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Tunables App.config antes de crear ventana/Flash.
            LokePerformanceSettings.ApplyFromConfig();
            SerialConnectionSettings.ApplyFromConfig();
            base.OnStartup(e);
        }
    }
}
