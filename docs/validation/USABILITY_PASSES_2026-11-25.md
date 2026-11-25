# Usability passes, 2026-11-25

Scope: reduce steps on the everyday paths and close finish gaps against the competitor reference set
(`COMPETITOR_BENCH_2026-11-25.md`, `AI_COMPETITIVE_GAPS_2026-11-22.md`, `COMPETITOR_FEATURE_MATRIX_2026-11-21.md`).

How the numbers were obtained: counted from the code and XAML of the control a mouse user must operate (one "step" = one click, one key
chord or one drag gesture; typing the question itself and picking an item in a list that is already open are counted separately). They were
NOT measured with a stopwatch or with other people, and no live WinUI session was driven for this pass (see "Not verified").

## Before / after

| Task | Before | After | What changed |
|---|---|---|---|
| Choose or load a model and chat | Chat (1 if not already there) + open the model list (1) + pick (1) = 2-3 | `Ctrl+Shift+M` (1 chord, opens the list) + pick (1) = 2, from any AI workspace | Palette entry "Choose or load a model for the chat" and accelerator. Lands in Chat first. Models that are not installed still go through Model Hub (unchanged: nothing downloads without a choice). |
| Load a document and ask about it | Attach button (1) + find the file in the picker (>=2) + Open (1) + type + Send = >=4 steps before typing | Drag the file(s) onto the chat (1 gesture), or copy in Explorer and `Ctrl+V` in the composer (1 chord), or `Ctrl+Shift+A` for the picker; then type + Enter | Chat accepts drops and pasted files through one shared policy (type, size, count, links refused, folders pointed to Knowledge) with a bilingual message for every refusal. |
| Add a folder or files to Knowledge | Documents page, Add folder (1) + folder picker (>=2) | Drag a folder or files onto the page (1 gesture) | The page already took drops; it now refuses unsupported types explicitly (it used to copy anything up to 4 GB) and names each refusal. |
| Change the frequent chat settings | Chat (1 if elsewhere) + generation button (1) | `Ctrl+Shift+G` (1 chord) from any AI workspace | Palette entry "Chat settings: temperature, reply length, context" + accelerator. The context window itself still lives in Runtime Manager (Studio) and is not reduced by this pass. |
| Create an image | Create (1) + Image tab (1, if another mode was last used) = 2 | `Ctrl+Shift+I` (1 chord), always on the Image tab | Palette entry + accelerator; `CreateWorkspaceView.ShowImageMode()`. |
| Search the conversations | Click the search box (1) | `Ctrl+Shift+F` (1 chord, no mouse), from any AI workspace | Palette entry + accelerator; focus goes to the existing search box. |
| Allow background runtime downloads and online catalog checks | Settings (1) + scroll to Automatic checks + toggle (1); never offered at first start | First start: one visible switch, off, with the explanation (1 toggle, or 0 to keep it off); same switch and wording stay in Settings | See below. |
| Stop a helper app when Console ends | Checkbox existed with a 3-word label | Same checkbox plus a two-sentence explanation under it | See below. |

Palette titles now carry the shortcut (for example "Create an image  (Ctrl+Shift+I)") so people learn it in the place they already look.
All five chords are Ctrl+Shift+letter, never Ctrl+letter, so they cannot swallow typing in the composer or a search box; `Ctrl+K` stays untouched
and the catalog test forbids reusing it.

## Changes in detail

1. Drag and drop (chat and Knowledge). `AttachmentDropPolicy` decides, `ChatView.Drop.cs` and `DocumentSearchView.cs` act.
   - Kinds: image, video, audio, document (PDF, Office, spreadsheets, text, code), folder, unsupported.
   - Limits: the chat uses the same settings as the Attach button (images 4 x 20 MB, video 1 x 500 MB, documents 4 x 100 MB, audio 1 x 25 MB by default),
     counting what is already waiting in the composer. Knowledge takes documents up to 4 GB and folders; images, audio and video are refused with the
     reason "attach them in a chat instead" (they are not indexed).
   - Links and shortcuts are never followed; empty files are refused; a busy chat refuses drops with a message.
   - Preview: while dragging, the drop caption says "Attach N item(s)" / "Add N to Knowledge" or "No supported item" (and the drop is not accepted).
     After dropping, the usual attachment chips (name, remove button) appear before anything is sent.
   - Keyboard: drag and drop is a pointer gesture, so the keyboard equivalents are the Attach buttons (existing), `Ctrl+Shift+A`, and `Ctrl+V` of files copied
     in Explorer into the composer (plain-text paste is untouched).
   - Not added: dropping a folder on the chat does not attach it (it says to use Knowledge); audio/video are not indexed into Knowledge.
2. First-run question for background downloads and online catalog checks (`OnboardingView.BackgroundDownloads.cs`, `BackgroundDownloadsChoice`).
   Shown in the main first-run flow, outside the collapsed manual expander, EN+IT, default off. Corporate shows the same card disabled and off. It saves
   through the existing `/api/v1/maintenance/settings` call, so the server still stamps the consent time and normalises Corporate to off; if saving fails the
   switch goes back to off and says so. Settings uses the same wording and the same Corporate lock. No new server route (so no new line in
   `web-session-route-decisions.txt`).
3. MSI: after removal an empty `Program Files\PaRuX\Redist` folder was left (created at run time by the helper, not by a component). `Package.wxs` now
   has `RemoveFolder On="uninstall"` for `Redist`, `Redist\Drivers` and `Redist\Sunshine` (empty-only by Windows Installer semantics), and the post-setup
   helper removes only empty directories under a folder named `Redist` after the uninstall step. Contract test: `RedistPackagingTests`.
4. Support apps: "Stop when Console ends" gets an explanation under the options (only apps PaRuX started itself, never a copy that was already running,
   nothing stopped if the option is switched off first). Runner behaviour is covered with a fake host: stops once, leaves apps without the option running,
   reports a failed stop as `NotStopped`, honours the option being switched off before the Console ends.
5. Quick actions: see the table; `QuickActionCatalog` is the single list used by the palette, the accelerators and the tests.
6. Coherence: every new XAML string has an Italian companion (`UiLocalizationHookTests` caught and fixed one English-only attribute during this pass); new
   controls use theme resources and styles (no hard-coded colours), so light, dark and high contrast follow the existing themes; each new control has an
   AutomationId and name for keyboard and screen readers. Normal vs Advanced: the quick actions are AI-workspace actions and appear in both; the Studio-only
   workspaces in the palette are unchanged.

## Tests

Server tests, Release: `AttachmentDropPolicyTests`, `OnboardingBackgroundDownloadsChoiceTests`, `QuickActionCatalogTests`, extended `SupportAppTests` and
`RedistPackagingTests`, plus the existing UiLocalization, Accessib*, StartupLazyPage, WorkspaceNavigation, Settings*, Onboarding*, Msi*, Wix*, Packaging*,
SupportApp*, Release* and web-session route-decision suites.

## Not verified (honest list)

- No real WinUI session: drag-over captions, drop of real files, `Ctrl+V` of Explorer files, the palette entries and the accelerators were not exercised
  in the running app, and no FlaUI test was written or run for them.
- The MSI change was verified only by contract tests on the authoring and the helper script. The MSI was not rebuilt (artifacts are out of scope), so
  the "no empty Redist folder after uninstall" outcome has not been observed on a machine.
- Step counts are code-derived estimates, not timings or user studies. Pad (controller) focus order for the new first-run card was not tested on a pad.
- High-contrast and dark rendering of the new card was not looked at on screen.
