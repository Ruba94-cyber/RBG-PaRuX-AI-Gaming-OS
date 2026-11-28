using System.Text.Json;

namespace PaRuX.Server.Tunnel;

/// <summary>
/// Persisted choices for the Internet tunnel. The optional named-tunnel token is a credential: it is stored only through
/// <see cref="SecretProtection"/> (Windows DPAPI for the current user), in its own file, and is never written to
/// remote-tunnel.json, a log, an API response or a command line (cloudflared receives it through its environment).
/// </summary>
internal sealed class TunnelSettingsStore
{
    private const string TokenPurpose = "parux.remote-tunnel.named-token.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    internal TunnelSettingsStore(string stateDirectory, ISecretProtector? protector = null)
    {
        _directory = stateDirectory;
        _protector = protector ?? SecretProtection.For(stateDirectory);
    }

    internal string StateDirectory => _directory;

    private string SettingsPath => Path.Combine(_directory, "remote-tunnel.json");
    private string TokenPath => Path.Combine(_directory, "remote-tunnel-token.bin");

    internal sealed class Document
    {
        public bool Enabled { get; set; }
        public DateTime? ConsentUtc { get; set; }
        public string NamedPublicUrl { get; set; } = string.Empty;
    }

    internal Document Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new Document();
                return JsonSerializer.Deserialize<Document>(File.ReadAllText(SettingsPath), Json) ?? new Document();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable settings mean "off, no consent": the tunnel never starts from a file it cannot trust.
                return new Document();
            }
        }
    }

    internal void Save(Document document)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
            File.Move(temp, SettingsPath, overwrite: true);
        }
    }

    internal bool HasToken { get { lock (_gate) return File.Exists(TokenPath); } }

    internal void SaveToken(string token)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            string temp = TokenPath + ".tmp";
            File.WriteAllText(temp, _protector.Protect(TokenPurpose, token.Trim()));
            File.Move(temp, TokenPath, overwrite: true);
        }
    }

    internal void ClearToken()
    {
        lock (_gate)
        {
            try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Null when there is no token or it cannot be decrypted (other machine or account).</summary>
    internal string? ReadToken()
    {
        lock (_gate)
        {
            try { return File.Exists(TokenPath) ? _protector.Unprotect(TokenPurpose, File.ReadAllText(TokenPath).Trim()) : null; }
            catch (IOException) { return null; }
        }
    }
}
