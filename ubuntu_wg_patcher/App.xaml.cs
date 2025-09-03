using System.Configuration;
using System.Data;
using System.Windows;
using Serilog;
using ubuntu_wg_patcher.Logging;

namespace ubuntu_wg_patcher
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            LogService.StartNewLog();
            Log.Information("Application started");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Application exiting");
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

}
