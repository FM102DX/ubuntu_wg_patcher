using System;
using System.Threading;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public interface IServerInfoReportService
    {
        /// <summary>
        /// Collects WireGuard Servrinfo (keys & peers) from the remote host and returns a ready-to-append text block.
        /// Should NOT reveal private keys or PSK values. Safe for logging.
        /// </summary>
        Task<string> CollectWireGuardServrinfoAsync(SessionParams cfg, IProgress<string> progress, CancellationToken ct);
    }
}
