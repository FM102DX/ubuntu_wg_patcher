using System;
using System.Threading;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public interface IConfigRunner
    {
        Task<RunnerResult> RunAsync(SessionParams session, IProgress<string> progress, CancellationToken ct);
    }
}
