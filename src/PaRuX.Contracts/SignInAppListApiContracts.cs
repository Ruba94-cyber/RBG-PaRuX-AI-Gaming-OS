using System.Text.Json.Serialization;

namespace PaRuX.Contracts;

/// <summary>Why a sign-in trigger did or did not start the list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignInTriggerDecision
{
    Started, Disabled, EmptyList, GamingActive, AlreadyRanThisSignIn, NotAtSignIn, SignInUnknown, AlreadyRunning,
}

/// <summary>An entry plus a read-only problem text (for example the exe was moved); null when the entry is runnable.</summary>
public sealed record SignInAppEntryStatus(SignInAppEntry Entry, string? Problem);

/// <summary>What Settings shows: the saved list, the live run snapshot and the last run/trigger facts.</summary>
public sealed record SignInAppListStatus(
    bool Enabled,
    SignInAppListSettings Settings,
    IReadOnlyList<SignInAppEntryStatus> Apps,
    SignInRunSnapshot Run,
    string? LastRunSessionKey,
    bool StoreDamaged,
    int MaxApps);

/// <summary>Whole-list save (add, remove and reorder are all an edit of the list). Enabled is the owner's explicit opt-in.</summary>
public sealed record SignInAppListSaveRequest(
    bool Enabled,
    SignInAppListSettings? Settings,
    IReadOnlyList<SignInAppEntry>? Apps);

/// <summary>
/// Sent once by the shell after it started. SessionKey/SessionStartUtc identify the Windows sign-in session
/// (start of the session's explorer.exe), ShellStartUtc is when the PaRuX shell process started, ConsoleActive is the shell's
/// real Console/TV state at that moment.
/// </summary>
public sealed record SignInTriggerRequest(
    string? SessionKey,
    DateTimeOffset? SessionStartUtc,
    DateTimeOffset? ShellStartUtc,
    bool ConsoleActive);

public sealed record SignInTriggerResponse(SignInTriggerDecision Decision, SignInRunSnapshot Run);

/// <summary>The shell's live Console/TV state, reported while a run is in progress so SkipInConsole follows reality.</summary>
public sealed record SignInShellStateRequest(bool ConsoleActive);

/// <summary>Path of an explicitly chosen legacy DelayedApps.json (read only).</summary>
public sealed record SignInAppImportRequest(string? Path, bool Confirm = false, bool Append = false);

public sealed record SignInAppImportPreview(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    SignInAppListSettings? Settings,
    IReadOnlyList<SignInAppEntry> Apps);
