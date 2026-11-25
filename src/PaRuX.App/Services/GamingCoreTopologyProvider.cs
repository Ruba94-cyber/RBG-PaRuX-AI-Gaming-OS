using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>The real display source for Console decisions: the local Core's read-only topology. Replaceable in tests through <see cref="Provider"/>.</summary>
    public sealed class GamingCoreTopologyProvider : IConsoleTopologyProvider
    {
        public static IConsoleTopologyProvider Provider { get; set; } = new GamingCoreTopologyProvider();

        public async Task<GamingDisplaySnapshotDto?> ReadAsync(CancellationToken ct)
        {
            var reply = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync(ct).ConfigureAwait(false);
            return reply.Succeeded && reply.Value is { Supported: true } ? reply.Value : null;
        }
    }
}
