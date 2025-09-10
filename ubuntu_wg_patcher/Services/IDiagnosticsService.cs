using System;
using System.Threading;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public interface IDiagnosticsService
    {
        /// <summary>
        /// Runs a server-side WireGuard deep diagnostic and downloads the resulting folder to the local export path.
        /// Returns the local folder path on success, or null on failure (errors are reported via progress).
        /// </summary>
        Task<string?> RunServerInfoAsync(SessionParams cfg, IProgress<string> progress, CancellationToken ct);
    }
}
