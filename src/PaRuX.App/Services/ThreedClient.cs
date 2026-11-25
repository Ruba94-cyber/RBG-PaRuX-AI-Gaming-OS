using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class ThreedClient
    {
        public static ThreedClient Current { get; } = new ThreedClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(10));
        private ThreedClient() { }

        public async Task<ThreedEngineStatus> GetStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/threed/engine");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new ThreedEngineStatus { Installed = false };
            return await response.Content.ReadFromJsonAsync<ThreedEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new ThreedEngineStatus { Installed = false };
        }

        public async Task<string> InstallEngineAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/threed/engine/install");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement error))
                        throw new LocalAiException(error.GetString() ?? "3D Studio install failed.");
                }
                catch (System.Text.Json.JsonException) { }
                throw new LocalAiException("3D Studio install failed (" + (int)response.StatusCode + ").");
            }
            return body;
        }

        public async Task<ThreedGenerateResponse> GenerateAsync(string sourceImagePath, CancellationToken ct)
        {
            byte[] bytes = await File.ReadAllBytesAsync(sourceImagePath, ct).ConfigureAwait(false);
            return await GenerateFromBase64Async(
                Convert.ToBase64String(bytes),
                Path.GetFileName(sourceImagePath),
                mode: "image-to-3d",
                prompt: null,
                ct).ConfigureAwait(false);
        }

        public async Task<ThreedGenerateResponse> GenerateFromBase64Async(
            string sourceImageBase64,
            string fileName,
            string mode,
            string? prompt,
            CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            var payload = new ThreedGenerateRequest
            {
                SourceImageBase64 = sourceImageBase64,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "source.png" : fileName,
                Seed = -1
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/threed/generate");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(payload);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("3D generation failed (" + (int)response.StatusCode + "): " + detail);
            }
            ThreedGenerateResponse? result = await response.Content.ReadFromJsonAsync<ThreedGenerateResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null || string.IsNullOrEmpty(result.Base64Data)) throw new LocalAiException("PaRuX Core returned an empty 3D model.");

            // Preserve a real managed .glb behind the generated response so Artifact Workspace and
            // local OBJ/STL/USDZ export have a durable file reference under generated/3d.
            try
            {
                byte[] modelBytes = Convert.FromBase64String(result.Base64Data);
                AppPaths.EnsureCreated();
                StorageQuotaService.EnsureCapacity(modelBytes.LongLength);
                string managedPath = Path.Combine(AppPaths.GeneratedThreedDir, Guid.NewGuid().ToString("N") + ".glb");
                await File.WriteAllBytesAsync(managedPath, modelBytes, CancellationToken.None).ConfigureAwait(false);
                result.SavedPath = managedPath;
                await ArtifactCaptureService.Current.CaptureFileAsync(
                    managedPath,
                    "3d",
                    string.IsNullOrWhiteSpace(result.FileName) ? "model.glb" : result.FileName,
                    "3d-generation",
                    new
                    {
                        mode,
                        prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt,
                        sourceImage = fileName,
                        mediaType = result.MediaType,
                        durationSeconds = result.DurationSeconds
                    },
                    markFinal: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("3D artifact capture skipped after successful generation: " + ex.Message);
            }

            return result;
        }

        public async Task<ThreedExportResponse> ExportAsync(string sourcePath, string format, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            var payload = new ThreedExportRequest
            {
                SourcePath = sourcePath,
                Format = format
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/threed/export");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(payload);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(detail);
                    if (doc.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement error))
                        throw new LocalAiException(error.GetString() ?? "3D export failed.");
                }
                catch (System.Text.Json.JsonException) { }
                throw new LocalAiException("3D export failed (" + (int)response.StatusCode + "): " + detail);
            }

            ThreedExportResponse? result = await response.Content.ReadFromJsonAsync<ThreedExportResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null || string.IsNullOrWhiteSpace(result.OutputPath))
                throw new LocalAiException("PaRuX Core returned an empty 3D export response.");

            try
            {
                if (File.Exists(result.OutputPath))
                {
                    await ArtifactCaptureService.Current.CaptureFileAsync(
                        result.OutputPath,
                        "3d",
                        result.FileName,
                        "3d-export",
                        new
                        {
                            format = result.Format,
                            sourceGlb = Path.GetFileName(sourcePath),
                            mediaType = result.MediaType,
                            vertexCount = result.VertexCount,
                            triangleCount = result.TriangleCount
                        },
                        markFinal: true,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("3D export artifact capture skipped: " + ex.Message);
            }

            return result;
        }
    }
}
