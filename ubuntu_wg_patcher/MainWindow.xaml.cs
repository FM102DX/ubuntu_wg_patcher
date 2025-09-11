using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ubuntu_wg_patcher.Services;
using ubuntu_wg_patcher.ViewModels;

namespace ubuntu_wg_patcher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SshClientService _ssh = new();
        private readonly SessionStorage _storage = new();
        private MainViewModel? _vm;

        public MainWindow()
        {
            InitializeComponent();
            var wgRunner = new WireGuardRunner(_ssh);
            var vlessRunner = new VlessRunner(_ssh);
            var servrinfo = new ServerInfoReportService(_ssh);
            var diagnostics = new DiagnosticsService(_ssh, servrinfo);
            _vm = new MainViewModel(_storage, _ssh, wgRunner, vlessRunner, diagnostics);
            DataContext = _vm;

            Loaded += async (_, __) =>
            {
                await _vm!.LoadLastSessionAsync();
            };

            Closing += (_, __) =>
            {
                _ssh.Dispose();
            };
        }
    }
}