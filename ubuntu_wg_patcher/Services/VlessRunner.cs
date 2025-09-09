using System;
using System.Threading;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public class VlessRunner : IConfigRunner
    {
        private readonly ISshClientService _ssh;
        public VlessRunner(ISshClientService ssh)
        {
            _ssh = ssh;
        }

        public Task<RunnerResult> RunAsync(SessionParams session, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("VLESS runner: not implemented yet. Please define the deployment steps.");
            return Task.FromResult(new RunnerResult
            {
                PublicIp = string.Empty,
                GeoJson = string.Empty,
                ExportPath = session.ExportPath,
                LogFilePath = ubuntu_wg_patcher.Logging.LogService.CurrentLogFilePath ?? string.Empty
            });
        }
    }
}
