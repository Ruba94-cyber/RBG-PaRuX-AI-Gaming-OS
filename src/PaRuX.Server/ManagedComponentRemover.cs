using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Deletes the PaRuX-installed copy of an optional engine. Refuses anything that is not a strict child of
/// PaRuX's own runtime folder, a linked folder, a running engine or software the owner supplied.</summary>
public sealed class ManagedComponentRemover
{
    private readonly RuntimeManagerService _runtime;
    public ManagedComponentRemover(RuntimeManagerService runtime) => _runtime = runtime;

    public sealed record Result(bool Removed, string Message);

    public async Task<Result> RemoveAsync(string componentId, CancellationToken ct)
    {
        (string backend, string folder)? target = componentId switch
        {
            "llama-server-cuda" => ("text-cuda", LocalAiPaths.RuntimeCudaPath),
            "llama-server-vulkan" => ("text-vulkan", LocalAiPaths.RuntimeVulkanPath),
            _ => null
        };
        if (target == null)
            return new Result(false, "PaRuX does not remove this component.");

        RuntimeManagerStatus status = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
        RuntimeBackendStatus? backendStatus = status.Backends.FirstOrDefault(b => b.Backend == target.Value.backend);
        ManagedComponentRemovalPolicy.Decision decision = ManagedComponentRemovalPolicy.Evaluate(
            componentId, ComponentOwnership.PARUX_MANAGED, backendStatus?.Installed == true, backendStatus?.Running == true,
            target.Value.folder, LocalAiPaths.RuntimeRoot);
        if (!decision.Allowed) return new Result(false, decision.Reason);

        string folder = target.Value.folder;
        if (!Directory.Exists(folder)) return new Result(false, "This component is not installed.");
        if ((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0)
            return new Result(false, "The install folder is a link, so PaRuX will not delete it.");
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, "Could not remove the files (in use or protected): " + ex.Message);
        }
        return new Result(true, "Removed.");
    }
}
