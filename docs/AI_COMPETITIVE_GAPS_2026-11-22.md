# AI competitive gaps and performance plan (2026-11-22)

Scope: Unsloth Studio / Unsloth Desktop, AnythingLLM (Desktop), Locally Uncensored (LU) against PaRuX, AI side only. Docs-only deliverable; nothing was built, run on the GPU or driven through the UI.

## Method and honesty

- Competitor facts: the official release pages (GitHub releases) and docs pages fetched on 2026-11-22. Summaries only; no code read, no long quotes. Release pages were truncated at about 100k characters, so the oldest releases on each page were not read.
- PaRuX facts: current code in this worktree (tip 67224802 = `main`), found by name/symbol grep and narrow reads. Status words: DONE (implementation found and wired), PARTIAL, MISSING. "Name-level" means a file named for the feature exists but its body was not read. I did not run tests, so "DONE" here means "implemented in code", not "verified on hardware" (CLAUDE.md Definition of Done is stricter; real-runtime proof is listed per slice).
- The 2026-11-21 matrix is stale on three rows, corrected here from code: image-to-3D has a client, a Create section and a GLB result registered in Artifact Workspace (`src/PaRuX.App/Services/ThreedClient.cs`, `src/PaRuX.App/Views/CreateWorkspaceView.cs` lines about 169, 443, 691-702); hybrid keyword plus embedding retrieval is the default when an embedder is installed (`src/PaRuX.Server/Knowledge/KnowledgeRetrieval.cs` lines 5, 19, 61-66) with an HNSW index (`src/PaRuX.Server/HnswVectorIndex.cs`) and a local reranker (`src/PaRuX.Contracts/RerankerContracts.cs`); the hard-coded `--device Vulkan0` loss from the 2026-11-21 measurement was replaced by device choice by discrete/dedicated VRAM/fit/health (commit f78ac9f9, `src/PaRuX.Server/RuntimeLaunchOptimizer.cs`). That fix has NOT been re-measured.
- Could not verify: Unsloth Studio at runtime (install was BLOCKED-EXTERNAL on 2026-11-21, see `docs/COMPETITOR_MEASURED_2026-11-21.md`), so every Unsloth performance number below is a vendor claim; AnythingLLM multi-user, Docker-only features and connector behaviour (docs pages `/features/desktop` and `/features/data-connectors` returned 404; I relied on the release notes and the introduction page); anything on LU's paid "LU Cloud"; whether LU 3.1.2 differs on Windows (its notes say Windows is unchanged). The release dates and versions are what the pages state; I did not cross-check them elsewhere.
- Installed on this PC (read-only look): LU 3.1.1 in `C:\Program Files\Locally Uncensored` (product version 3.1.1; latest is 3.1.2, a macOS-only fix). The "Locally Uncensored (CUDA)" desktop shortcut runs `C:\Users\paolo\AppData\Local\llama.cpp\Start-LU-CUDA.ps1`, which starts an external llama.cpp CUDA `llama-server` (build folder `b12621`) on `Qwen3-4B-Q4_K_M.gguf` with `-ngl 99 -c 16384 -np 1 --jinja` on port 8080, then launches LU and stops the server when LU exits. So LU "(CUDA)" here means LU using an owner-supplied CUDA engine, not LU's own bundled Vulkan engine used in the 2026-11-21 measurement. Reuse it as the LU test engine; it is the fairest GPU engine for LU. Unsloth is not installed; AnythingLLM 1.20.0 was installed by the 2026-11-21 session.

Latest versions seen: Unsloth v0.1.922-beta (2026-11-20, Studio is Beta); AnythingLLM v1.20.0 (2026-11-17); LU 3.1.2 (2026-11-19).

## 1. Feature matrix per competitor

### 1a. Unsloth Studio / Desktop

| Feature (Unsloth) | PaRuX | Evidence (code) and note |
|---|---|---|
| GGUF + safetensors chat on llama.cpp, auto fit/offload | DONE | `RuntimeLaunchOptimizer.cs`, `LaunchPlanner.cs`, `LocalAiProcessManager.cs`; fit: `src/PaRuX.App/Services/ModelHardwareFitEstimator.cs` |
| Multi-GPU inference | DONE | `--tensor-split` / split modes in `RuntimeLaunchOptimizer.cs` (about lines 155, 487-493) |
| Model Arena (side by side) | DONE | `src/PaRuX.App/Services/ModelArenaClient.cs`, `ModelArenaPresenter.cs` |
| Reuse already-downloaded GGUFs | DONE | `ExternalModelImportPolicy.cs`, `ExternalModelImportPicker.cs` |
| Image/doc/audio uploads in chat; scanned-PDF OCR | DONE (name-level for audio-in-chat) | `Services/DocumentTextExtractor.cs`, `Server/Knowledge/Ocr.cs` |
| Auto context compaction of long chats | DONE | `PaRuX.Intelligence/ConversationWindowPlanner.cs`, `Server/ActiveContextWindow.cs` |
| Self-healing tool calling (claimed +50% accuracy) | MISSING | no repair/retry-on-malformed-tool-call symbol found (`ToolCallRepair`, `SelfHeal` absent). Tool loops exist: `Server/StreamingMcpToolLoop.cs`, `McpToolSchemaRouter.cs`, `ToolSchemaIntentGate.cs` |
| Sandboxed Bash/Python execution in chat, OS-enforced file limits | PARTIAL | Only inside the coding agent: `run_command` with permission profiles (`Services/CodingAgentPermissionMode.cs`, `CodingTaskSandboxWorkspacePolicy.cs`, `ChildProcessJob.cs`). No chat-level "run this Python" tool, no trusted-folder setting. `Server/ManagedPythonRuntime.cs` could host it |
| Canvas: HTML preview of code output | MISSING | no preview symbol found in `Views/` for chat output (Artifact Workspace exists for media, `Services/ArtifactWorkspaceClient.cs`) |
| Browser use (navigate, forms, PDFs) | DONE | `Services/BrowserDevToolsService.cs`, `BrowserDomActionScriptBuilder.cs`, `Server/KnowledgeBrowserTools.cs` |
| Computer use (screen, mouse), permission controls | DONE | `Services/ComputerUse*.cs`, `Server/RemoteComputerUseApprovalService.cs` (PaRuX also has remote approval) |
| Web search that visits pages, cites sources | DONE (single no-key provider) | `Server/WebSearchService.cs` (DuckDuckGo HTML/lite; opt-in SearXNG via env var `PARUX_SEARXNG_LOCAL_URL`), `DeepResearchService.cs` |
| MCP client incl. creative/game-engine MCPs | DONE (client) | `McpClientRegistry.cs`, `McpOAuthCoordinator.cs`, `McpServerEndpointPolicy.cs`, presets `Services/McpServerPresetCatalog.cs`; specific Unity/Unreal/Godot/Blender/Figma presets not verified |
| Scheduled tasks whose replies land in a chat | PARTIAL | `Server/Automation/AutomationEngine.cs` (Schedule, FileCreated, GitCommit, ModelDownloadCompleted, TaskCompleted triggers), `Server/ChatSchedulingEndpointExtensions.cs` (name-level); reply-into-chat not verified |
| Data Recipes (PDF/CSV/JSON/DOCX/TXT to dataset, graph nodes) | PARTIAL | column-mapped SFT/DPO/KTO recipes: `Server/DatasetCurationService.TrainingRecipes.cs`; PDF/DOCX reviewed Q&A authoring not present; no graph-node UI |
| No-code SFT/LoRA/QLoRA training with presets | DONE | `Server/TrainingLabService.cs`, `PaRuX.Intelligence/TrainingRunPlanner.cs`, `Views/LifecycleLabView.TrainingWizard.cs`; precisions are only `qlora-4bit` and `lora-16bit` (`PaRuX.Contracts/TrainingContracts.cs` lines 74-86) |
| DPO / ORPO / KTO / GRPO / continued pretraining / vision SFT / TTS SFT | DONE (code); GSPO MISSING | `TrainingMethod.All` in `TrainingContracts.cs` line 49-58 |
| Sequence packing | DONE | `TrainingLabService.cs` lines about 537, 1435 |
| Full fine-tuning, FP8 training, DoRA, embedding-model and diffusion LoRA training | MISSING | contract comment says full-model/FP8 are "deliberately not advertised"; no `use_dora`; no diffusion-LoRA trainer (Create has LoRA stacks for inference only, `Services/CreateLoraStackPresenter.cs`) |
| Multi-GPU training | MISSING | none found |
| Training observability: loss, grad norm, GPU util, charts, remote view, history | PARTIAL | metrics parsed (`PaRuX.Intelligence/TrainingMetricsParser.cs`) and shown as a text line with loss and gradient norm (`LifecycleLabView.TrainingWizard.cs` about 597-603); no chart control found, no GPU-utilisation series, no phone/remote view of a run |
| Export: safetensors / 16-bit / GGUF / merge | PARTIAL | adapter merge `AdapterManagerService.MergeToBaseModelAsync`, GGUF conversion `LoraGgufConversionService.cs`; vLLM/Ollama/LM Studio one-click targets and Q4NX (AMD NPU) not found |
| Dynamic / importance-weighted quants (Unsloth Dynamic) | PARTIAL | `QuantLadderPolicy.cs` selects existing quants; imatrix appears only as policy text; PaRuX does not produce quants |
| Speculative decoding, per-alias ctx/KV config | DONE | `Server/SpeculativeDraftDiscovery.cs`, `Contracts/RuntimePerformanceTuningContracts.cs`, `tools/quant-ladder-bench.py` measures it |
| API: OpenAI-compatible endpoint; grammar / JSON-schema constrained output | PARTIAL | gateway exists (`Server/Gateway/`, `ApiKeyService.cs`); JSON-schema/grammar pass-through on the gateway not confirmed (JSON-schema hits are in `App/Services/ChatClient.cs`, not `Gateway/`) |
| Connect Claude Code / Codex to the local model | DONE | `Services/Integrations/ClientIntegrations.cs`, `CoreGatewayBridge.cs` |
| External providers (OpenAI, Anthropic, vLLM) as connections | PARTIAL | `Server/OpenAiCompatibleProviderService.cs` (generic local OpenAI-compatible), cloud fallback client; named presets not verified; local-only core by design |
| Desktop app on Windows/macOS/Linux, Docker, Colab | PARTIAL | Windows WinUI + Android only; no Linux/macOS (`src/PaRuX.App` is WinUI) |
| Secure remote access (HTTPS tunnel) | DONE (different design) | `Server/RemoteAccessService.cs`, paired-device policy, QR in `Views/SettingsView.xaml.cs`; no third-party tunnel by design |
| Theme export/import, Translate page, AMD NPU models | MISSING | accent presets only (`AccentPaletteTests`); no NPU runtime; low value |
| Model hub, 500+ models, diffusion/TTS/embedding | PARTIAL | Model Hub Start board and categories incl. embedding/reranker/audio (`Views/ModelHubView.cs` about line 174); catalogue breadth not measured |
| Telemetry-free, token auth | DONE | local-first, API keys and pairing (`ApiKeyService.cs`) |

### 1b. AnythingLLM Desktop (v1.20.0)

| Feature (AnythingLLM) | PaRuX | Evidence and note |
|---|---|---|
| Workspaces with separate document sets | DONE | `Server/ProjectManager/`, `Views/ProjectsWorkspaceView.cs`, `Services/ProjectKnowledgeClient.cs` |
| Built-in local LLM + one-click install | DONE | managed llama.cpp runtime, onboarding auto-setup (`Views/OnboardingView.AutoSetup.cs`) |
| Many LLM providers (local list: LM Studio, LocalAI, Ollama, KoboldCPP; ~15 cloud) | PARTIAL (by design) | generic OpenAI-compatible provider; cloud only as fallback/optional; strict-local core is the product rule |
| Selectable embedder (default, LM Studio, LocalAI, Ollama, cloud) | PARTIAL | `Server/EmbeddingService.cs`, `LlamaCppRetrievalProviders.cs`, switchable keyword-only toggle (`KnowledgeRetrieval.cs` `SwitchableEmbedder`); no embedder picker in UI found (`SettingsView.Knowledge.cs` shows threads/profile only) |
| Vector DB choice (LanceDB, Chroma, Milvus, Qdrant, Pinecone...) | DONE (embedded) / n/a | built-in HNSW index (`HnswVectorIndex.cs`, `RagStore.cs`); external DBs are cloud/outside the local-only goal |
| Hybrid retrieval plus reranker | DONE (PaRuX lead) | `KnowledgeRetrieval.cs`, `Contracts/RerankerContracts.cs`, `RetrievalEvaluationService.cs` |
| Document ingestion incl. OCR, large PDFs, automotive formats | DONE (PaRuX lead) | `Services/DocumentTextExtractor.cs`, `Knowledge/KnowledgeIndex.LargePdf.cs`, `Knowledge/A2lExtractor.cs` etc. |
| Document pinning in prompt | MISSING (not found by name) | no `Pin*` document symbol |
| Data connectors: website crawl, watch folder | DONE | `Knowledge/WebKnowledgeCrawler.cs`, `KnowledgeFolderWatcher.cs`, `Views/KnowledgeWebPanel.cs` |
| Data connectors: GitHub, YouTube, Confluence, Paperless-ngx | MISSING | no symbols found |
| Citations / sources in answers | DONE | `Server/ContextEngineService.cs`, `Views/DocumentSearchView.cs`, `Services/KnowledgeClient.cs` |
| Agents with skills (RAG search, web, save file, chart, SQL, doc generation, file system) | PARTIAL | built-in skills: `skills/built-in/*.json` (rag, web-search, deep-research, presentation, coding, image, voice, memory-recall...); `Server/Knowledge/DocumentSkills.cs`; no SQL-query skill and no chart skill found |
| Gmail / Google Calendar / Outlook agent skills | MISSING (by design?) | no mail/calendar skills found; they need cloud accounts, which conflicts with local-only core; treat as optional user-owned integration |
| Custom agent skills (`plugin.json` + handler) | DONE | `.parux-plugin` bundles, `Views/SettingsView.PluginBundles.cs` |
| Agent Flows (no-code builder) | PARTIAL | `Views/WorkflowWorkspaceView.cs` exists (body not read), engine `Server/Automation/AutomationEngine.cs` |
| MCP client; MCP management API; per-server timeout | PARTIAL | client DONE; a REST surface for add/start/stop/reload MCP servers not confirmed |
| Background / scheduled jobs | DONE (engine) | `AutomationEngine.cs`, `Views/ToolsAutomationView.cs` |
| Meeting assistant (local transcription and notes) | MISSING | no meeting symbol except unrelated hits; STT building blocks exist (`Services/SpeechClient.cs`, `Server/ContinuousSpeechV2Endpoints.cs`) |
| Magic Echo style system-wide dictation | PARTIAL | composer push-to-talk only (`Services/DictationText.cs`) |
| Developer API with keys, OpenAPI docs | PARTIAL | `Server/Gateway/`, `ApiKeyService.cs`; no OpenAPI/Swagger document found |
| Browser extension chat sidebar | MISSING | none; low value for a local-first desktop |
| Embeddable chat widget | MISSING | none (Docker feature in AnythingLLM) |
| Multi-user with roles | MISSING | design only: `docs/ROLES_AND_POLICY_DESIGN_2026-11-20.md` (Docker feature in AnythingLLM, not Desktop as far as I could tell) |
| Workspace system-prompt variables, per-workspace model | PARTIAL | per-project model/knowledge exist; prompt variables not found |
| Linux/macOS/Windows ARM builds | MISSING | Windows x64 only |

### 1c. Locally Uncensored 3.1.x

| Feature (LU) | PaRuX | Evidence and note |
|---|---|---|
| Guided first run, engine and ComfyUI install, skip allowed | DONE | `Views/OnboardingView.AutoSetup.cs`, `OnboardingView.FirstModel.cs` |
| Import ChatGPT/Claude/Gemini history | DONE (ChatGPT confirmed; others unverified) | `Services/ChatImportService.cs`, `Server/ConversationImporter.cs` |
| Hardware-fit badges, quant advice, memory planning from GGUF header | DONE | `Services/ModelHubQuantAdvice.cs`, `ModelFitClassifier.cs`, `Intelligence/KvCacheSizeEstimator.cs` |
| Group chat with per-speaker context, regenerate one speaker, reorder | PARTIAL | `Services/GroupChatClient.cs`; per-speaker redo and reordering listed as missing in `docs/COMPETITOR_PARITY_AUDIT_2026-11-06.md` and not re-verified |
| Persistent memory, memory tool, search earlier chats | PARTIAL | `Services/MemoryClient.cs`, skill `memory-recall.json`; searching older conversations from the agent not verified |
| Coding agent: plan/ask modes, hunk review, project rules, AGENTS.md | DONE | `Services/CodingAgentService.cs`, `CodingSelectiveHunkApplyService.cs`, `CodingProjectInstructionResolver.cs` (AGENTS.md read in `CodingContextCompiler.cs`) |
| Steer a running agent task mid-run | MISSING | no steer symbol |
| Rewind files and chat to a chosen message | PARTIAL | chat rewind + undo `Services/ConversationRewind.cs`; applied-change undo `CodeWorktreeApplyService`; file rewind to message N open |
| Credit brake, cloud agent | n/a | cloud product, outside local-only core |
| Agent loop guard, shorter tool lists for local models | PARTIAL | `Server/ToolSchemaIntentGate.cs` (intent-gated tool lists); loop guard not verified |
| Dangerous-command and secret-file blocks in every mode | DONE | `CodingAgentPermissionMode.cs`, `CodingPermissionProfile.cs`, sandbox policy |
| Web search, 5 keyless providers, local Readability port, numbered sources | PARTIAL | one keyless provider (DuckDuckGo) + opt-in SearXNG; page reading and citations exist (`WebSearchService.cs`, `DeepResearchService.cs`) |
| LU browser: agent reads pages, page text treated as data | DONE | `BrowserDevToolsService.cs`, `BrowserDevToolsEvaluationPolicy.cs` |
| Watchers / scheduled agent turns with notifications while in tray | DONE (engine) / PARTIAL (no-code creator) | `AutomationEngine.cs` |
| Image/video generation, ComfyUI workflows, LoRA stack, inpaint, background removal | DONE | `Views/CreateWorkspaceView*.cs`, `Server/ComfyWorkflowImporter.cs`; PaRuX lead: any ComfyUI workflow import |
| Video 15 s, end image, preview during render | PARTIAL (name-level) | `Services/MediaJobClient.cs`; render preview not verified |
| 3D from text/image/video (GLB/OBJ/STL/USDZ) | PARTIAL | image-to-3D GLB only (`ThreedClient.cs`); no text-to-3D, no OBJ/STL/USDZ export, no Blender hand-off found |
| Character hub (train/upload/create; swap, motion, talk) | PARTIAL | `Services/AvatarClient.cs`, `Server/AvatarEngineService.cs` (talking avatar); no character-LoRA hub |
| Voice: local STT/TTS, interrupt, Parakeet dictation | DONE | `Services/VoiceConversationController.cs`, `SpeechClient.cs`, Parakeet in `Contracts/SpeechContracts.cs` and `ModelManagerContracts.cs` |
| Phone pairing by QR, chats continue on phone, speak to PC | DONE (Android client) | `Server/RemoteAccessService.cs`, `src/PaRuX.Android`, QR in `SettingsView.xaml.cs` |
| Local API starting with the app, OpenAI-compatible | DONE | `Server/Gateway/` |
| Chat appearance: colours, fonts, per-chat backgrounds, custom CSS | PARTIAL | accent palette and theme only |
| Chat: HTML preview in sandboxed frame, `/usage` tokens | MISSING (not found) | no HTML-preview symbol; per-reply token display not verified |
| LU changes its own settings from chat with confirmation | MISSING (not found) | no such tool symbol; PaRuX has `Services/CapabilityModePresenter.cs` only for routing |
| Abliteration (refusal-reduced model copy) | MISSING | only classifiers (`ModelCategoryClassifier.cs`); legal/policy review needed before any slice |
| Plugins with on/off switches | DONE | `.parux-plugin` bundles |
| Linux AppImage, macOS | MISSING | Windows only |
| Engine memory planning, two GPUs assigned to chat vs ComfyUI | PARTIAL | `Server/GpuResidencyReservationStore.cs`, `EngineNeutralVramScheduler.cs`; explicit per-task GPU assignment UI not verified |

### Where PaRuX already exceeds the three
- Gaming hard-off: AI frees the GPU while a game runs (`Server/GamingHardOff.cs`). None of the three has an equivalent.
- Hybrid retrieval with local reranker, an in-repo retrieval benchmark (`Server/Knowledge/KnowledgeBenchmark.cs`: precision/recall/MRR/nDCG/latency/index speed on a synthetic corpus) and negative-query ("not enough information") scoring. AnythingLLM exposes no such evidence.
- Document breadth: OCR, 30k-page PDF path, automotive formats (A2L, ARXML, DBC, ODX, CDD).
- Hardware-aware launch ladder with speculative-draft discovery, KV-cache estimator and tensor-split, plus first-party measurement harnesses (`tools/perf-launch-bench.py`, `tools/quant-ladder-bench.py`, `scripts/benchmark-competitive-inference.ps1`, `benchmark-local-ai-competitors.ps1`, `benchmark-reference-stream.ps1` with attested parity manifest).
- Resource footprint measured 2026-11-21: working set without a model 458 MB vs LU 632 MB and AnythingLLM 808 MB; graceful `--exit` in 0.7 s with no leftovers while LU and AnythingLLM only hide to tray; VRAM 4.0 GB vs 7.8/8.5 GB (but with a smaller context, so not like for like).
- Arbitrary ComfyUI workflow import, LoRA stack, inpaint; capability routing across models; Windows accessibility audit; backup-safe history; strict offline/corporate policy; Android client.
- Training breadth in methods: SFT, DPO, ORPO, KTO, GRPO, continued pretraining, vision SFT and TTS SFT in one wizard (Unsloth lists LoRA/FP8/FFT/PT; its RL methods are in the library, I did not verify them in the Studio UI).

## 2. Ranked backlog: top 15 gaps by user value

Ranking weighs: user-visible AI value, how directly it closes a gap to Unsloth/AnythingLLM/LU, size, and whether it keeps shipped core AI local. Every slice is end-to-end, honest-failure, cancellable, and obeys Gaming hard-off. "GPU" = needs the GPU for real verification (unit/contract tests never need it). Conflict groups (CG-x): slices in the same group touch the same files and must be serialized; different groups can run in parallel in separate worktrees.

| # | Gap and why it ranks here | Size | GPU |
|---|---|---|---|
| 1 | Trim the per-request context pack so a plain chat costs near raw-engine TTFT | S | Measure only |
| 2 | Competitor performance runner (one command, identical model, all apps) | M | Run only |
| 3 | Chat code execution (sandboxed Python) with HTML canvas preview | L | No |
| 4 | Training observability: loss/grad-norm/GPU charts, run history, re-export | M | Verify only |
| 5 | Multi-provider keyless web search with fallback chain | S | No |
| 6 | Embedder picker + retrieval mode + RAG benchmark in the UI | S | No (CPU embedder ok) |
| 7 | Self-healing tool calls and JSON-schema/grammar-constrained output | M | Verify only |
| 8 | Steer a running coding task and rewind files to message N | M | No |
| 9 | Export targets and imatrix/dynamic-quant production in Lifecycle Lab | M | CPU ok, GPU faster |
| 10 | Training breadth: DoRA, 16-bit full fine-tune, embedding-model fine-tune | L | Yes |
| 11 | Agent skills: read-only SQL, chart, document generation | M | No |
| 12 | Connectors: GitHub repo, YouTube transcript, Confluence | M | No |
| 13 | Meeting assistant (local transcription and summary) | M | Optional |
| 14 | No-code agent flow / watcher creator | M | No |
| 15 | OpenAPI docs + MCP management endpoints + gateway constrained output docs | S | No |

Unranked, tracked: multi-user roles (design only, `docs/ROLES_AND_POLICY_DESIGN_2026-11-20.md`), Linux/macOS builds, embeddable widget, browser extension, theme import/export, AMD NPU, text-to-3D and OBJ/STL/USDZ, character-LoRA hub, document pinning, abliteration (needs policy decision first).

### Slice details

**1. Lean context pack (perf; file group CG-chat)**
- Why: 2026-11-21 measured about 920 added prompt tokens per request (`prompt_n` 16 + `cache_n` 929 on a 12-token question); 3.4 s on a cold slow-GPU first request, 87 ms vs 54 ms raw TTFT on the 5080, and it eats context.
- Touch: `src/PaRuX.Server/ChatProxyService.cs` (`ApplyContextPack` about line 1438, skill-instruction injection about 1341-1411, caller line 306), new test in `tests/PaRuX.Server.Tests/`.
- Accept: a plain no-tool chat adds at most a bounded token count (target under 150) of system text; skills/tools text is injected only when the intent gate selects it (reuse `ToolSchemaIntentGate.cs`); behaviour for tool/RAG turns unchanged; the pack is byte-stable between requests so the prompt cache hits.
- Tests: `ChatProxyService` unit test counting injected tokens for plain, RAG and tool turns; prefix-stability test (two requests share an identical system prefix).
- GPU: no for the code; re-measure TTFT on the 5080 afterwards with `tools/perf-launch-bench.py` (slice 2).

**2. Competitor performance runner (CG-bench)**
- Touch: new `scripts/benchmark-ai-competitors-unified.ps1` that composes the existing `scripts/benchmark-local-ai-competitors.ps1`, `benchmark-competitive-inference.ps1`, `tools/perf-launch-bench.py`, `KnowledgeBenchmark.cs` CLI entry; results to Git-ignored `.ai/artifacts/competitors/`; summary table doc `docs/COMPETITOR_MEASURED_<date>.md`.
- Accept: one command produces the section 3 measurements for PaRuX, LU-with-external-engine (the owner shortcut), AnythingLLM/lemond, and bare llama.cpp, with a parity manifest (artifact SHA, ctx, KV type, parallel, flash-attn); refuses to run on a busy machine (reuse `busy_reason`); always kills what it started; records driver/build versions.
- Tests: `tools/test-reference-stream-benchmark.py` style fixture test against `tests/PaRuX.RuntimeStub`; manifest-mismatch refusal test.
- GPU: needed to run, not to write.

**3. Chat code execution with canvas preview (CG-chat-tools; conflicts with 7 on tool-loop files)**
- Touch: new `src/PaRuX.Server/ChatCodeExecutionService.cs` (reuse `ManagedPythonRuntime.cs`, `ChildProcessJob.cs`, `CodingTaskSandboxWorkspacePolicy.cs`), tool registration in `McpTools.cs` / `StreamingMcpToolLoop.cs`, chat view output block in `src/PaRuX.App/Views/ChatView*.cs`, a sandboxed HTML preview pane (WebView2 with no network, no file access), settings for trusted folders.
- Accept: model asks to run Python, user approves (default) or auto-approves per folder; code runs in a job object with CPU/memory/time caps and a working folder only; output, plots and HTML shown inline; cancel kills the whole tree; blocked while Gaming; no network by default.
- Tests: server tests for timeout, memory cap, path escape refusal, cancellation, no-network; UI test for approval card and preview. Real run on this PC with a plot-producing script.
- GPU: no.

**4. Training observability (CG-lab)**
- Touch: `src/PaRuX.Intelligence/TrainingMetricsParser.cs`, `src/PaRuX.Server/TrainingLabService.cs` (persist per-step history and sampled GPU util/VRAM), `src/PaRuX.App/Views/LifecycleLabView.TrainingWizard.cs` (chart control replacing the text line at about line 597), optional read-only paired-device view via `RemoteAccessService.cs`.
- Accept: live loss, gradient norm, learning rate, tokens/s, GPU util and VRAM series; run history list that reopens past curves; a finished run can be re-exported; charts survive restart; Gaming hard-off cancels cleanly.
- Tests: parser tests on captured log fixtures; history round-trip test; UI reachability test.
- GPU: yes for the final proof on a tiny LoRA (a 0.5B model, a few steps).

**5. Multi-provider keyless web search (CG-search)**
- Touch: `src/PaRuX.Server/WebSearchService.cs`, `LocalSearxngSearchProvider.cs`, new provider classes; settings entry; keep egress policy (`KnowledgeSecurity.cs` / offline policy).
- Accept: ordered fallback among DuckDuckGo, a Wikipedia API provider, optional SearXNG (settings, not only the env var), optional keyed providers; one provider failing never disables search; results show which provider answered; offline policy blocks all with an honest message.
- Tests: provider fakes via the existing handler abstraction, fallback order, timeout, ad-redirect stripping (existing logic), offline block.
- GPU: no.

**6. Embedder picker, retrieval mode and in-app RAG benchmark (CG-rag)**
- Touch: `src/PaRuX.App/Views/SettingsView.Knowledge.cs`, `src/PaRuX.Server/Knowledge/KnowledgeSettings.cs`, `KnowledgeRetrieval.cs`, `Server/EmbeddingService.cs`; expose `KnowledgeBenchmark` as a "Check retrieval quality" action.
- Accept: user sees installed embedders and rerankers, picks one, sees "Hybrid / keyword only" truthfully, changing the embedder triggers a bounded re-index with progress and cancel; the benchmark button reports precision/recall/MRR/latency and refuses while Gaming.
- Tests: settings round-trip, re-index on embedder change, benchmark determinism (seeded), UI reachability.
- GPU: no (CPU embedder acceptable; GPU only to compare speed).

**7. Self-healing tool calls and constrained output (CG-chat-tools)**
- Touch: `src/PaRuX.Server/StreamingMcpToolLoop.cs`, `McpToolCallBatchExecutor.cs`, `McpToolUseTelemetry.cs`, `src/PaRuX.Server/ChatProxyService.cs`/gateway request mapping for `response_format` json_schema and grammar.
- Accept: a malformed or schema-invalid tool call triggers a bounded (max 2) repair re-prompt with the validation error, then fails honestly; JSON-schema and grammar constraints pass through the gateway to llama-server; telemetry counts repairs.
- Tests: replay fixtures of broken tool calls (truncated JSON, wrong arg type, XML-style Qwen calls); gateway pass-through test against `tests/PaRuX.RuntimeStub`.
- GPU: no for tests; a small real model pass to quantify repair rate.

**8. Steer a running task and rewind files to message N (CG-coding)**
- Touch: `src/PaRuX.App/Services/CodingAgentService.cs`, `CodingAgentCheckpointStore.cs`, `CodingOwnChangeReverter.cs`, `ConversationRewind.cs`, `Views/ChatView.MessageBranching.cs`.
- Accept: a user message injected mid-run is delivered at the next step boundary; rewinding to message N restores chat and the files the agent changed after N, leaves user edits untouched, and is undoable.
- Tests: extend `ConversationRewindTests.cs` and `CodeWorktreeApplyServiceTests.cs`; steering test with a scripted runtime stub.
- GPU: no.

**9. Export targets and quant production (CG-lab-export)**
- Touch: `src/PaRuX.Server/LoraGgufConversionService.cs`, `AdapterManagerService.cs`, `llama-cpp-conversion/`, `src/PaRuX.Intelligence/QuantLadderPolicy.cs`, export UI in `LifecycleLabView*.cs`.
- Accept: merged model to safetensors-16 and GGUF at a chosen quant, optional imatrix computed on a user-selected calibration set; "use in Ollama/LM Studio/vLLM" produces files plus a verified instruction (not silent changes to USER_MANAGED software); the exported GGUF loads in managed llama.cpp and generates tokens.
- Tests: tiny-model fixture export, `tools/quant-ladder-bench.py` KLD comparison of imatrix vs plain quant; refusal when disk is short.
- GPU: CPU works for a tiny model; real model proof on GPU.

**10. Training breadth (CG-lab; serialize after 4 and 9)**
- Touch: `PaRuX.Contracts/TrainingContracts.cs` (precision list, comment at about line 71), `TrainingLabService.cs` runner script (about line 1435), `TrainingRunPlanner.cs`, `TrainingRuntimeCatalog`.
- Accept: DoRA flag first (small), then 16-bit full fine-tune admitted only when VRAM fit says yes, then embedding-model fine-tune; each is advertised only when a real tiny run passed on this machine; GSPO/FP8 stay unadvertised.
- Tests: planner admission tests, contract tests, one real tiny run per capability.
- GPU: yes.

**11. Agent skills: SQL, chart, document generation (CG-skills)**
- Touch: `skills/built-in/*.json` (new `sql-read.json`, `charts.json`), `src/PaRuX.Server/Knowledge/DocumentSkills.cs`, tool handlers.
- Accept: read-only SQLite/CSV query tool with row/time caps and no write path; chart tool writes a PNG/SVG to the Artifact Workspace; docx/xlsx/pptx writers behind the existing `presentation` skill pattern.
- Tests: query refusal of non-SELECT, caps, chart golden-image hash, skill manifest validation tests.
- GPU: no.

**12. Connectors: GitHub repo, YouTube transcript, Confluence (CG-rag-connectors)**
- Touch: `src/PaRuX.Server/Knowledge/WebKnowledgeSources.cs`, `WebKnowledgeCrawler.cs`, `KnowledgeWebWatchEndpoints.cs`, `Views/KnowledgeWebPanel.cs`, reuse `KnowledgeSecurity.cs`.
- Accept: public GitHub repo (zip/tree API) and YouTube captions ingested with per-source citations; Confluence only with a user-supplied token stored in the credential store; every connector is explicit user action, blocked under offline policy, honest failure on rate limits.
- Tests: local HTTP fixture servers for each source, offline-block test, citation URL test.
- GPU: no.

**13. Meeting assistant (CG-voice)**
- Touch: `src/PaRuX.App/Services/SpeechClient.cs`, `Server/ContinuousSpeechV2Endpoints.cs`, new `MeetingNotesService`, new view reachable from Chat.
- Accept: import an audio/video file or record mic (loopback optional) for transcription locally, speaker-agnostic transcript, summary with action items by the local model, saved as a knowledge source; no egress in strict-local test; cancel and Gaming refusal.
- Tests: feed a synthetic WAV; transcript and summary saved; egress probe empty.
- GPU: optional (faster STT).

**14. No-code agent flow / watcher creator (CG-automation)**
- Touch: `src/PaRuX.App/Views/WorkflowWorkspaceView.cs`, `Views/ToolsAutomationView.cs`, `Server/Automation/AutomationDefinitionValidator.cs`.
- Accept: "when X (schedule/file/commit) do Y (prompt + tools) and notify" built from forms, validated by the existing validator, runs while the window is in the tray, history of runs with honest failure.
- Tests: validator tests for the new templates, engine test with fake clock, UI reachability.
- GPU: no.

**15. Gateway docs and management API (CG-gateway)**
- Touch: `src/PaRuX.Server/Gateway/`, `Program.cs` endpoint registration, a generated OpenAPI document, MCP server list/start/stop/reload routes behind owner auth.
- Accept: `/openapi.json` served locally; routes require the right key scope; documented constrained-output parameters from slice 7.
- Tests: schema snapshot test, auth scope tests.
- GPU: no.

### Parallel plan (no shared files)

| Wave | Slices that can run at once | Why safe |
|---|---|---|
| A | 1 (ChatProxyService), 2 (scripts only), 4 (training UI/parser), 5 (WebSearchService), 6 (Knowledge settings/retrieval), 8 (coding agent services), 11 (skills), 14 (automation views), 15 (gateway) | disjoint file sets; note 1 and 15 both touch gateway-adjacent code only if 15 edits `ChatProxyService.cs`, so keep 15 to `Gateway/` and `Program.cs` registration |
| B | 3, 7 after each other (both edit tool-loop files) in parallel with 9, 12, 13 | 3 and 7 serialized; 9 touches export files only; 12 touches connector files; 13 touches speech files |
| C | 10 after 4 and 9 (shared `TrainingLabService.cs` and `LifecycleLabView*.cs`) | serialized by file overlap |

`PARUX_EXECUTION_PLAN.md` edits and `Program.cs` registrations are shared by many slices: have each slice append its own plan line at merge time, never edit the same hunk in parallel.

## 3. Performance plan

### 3.1 Measurement matrix (identical model and quant, each app's own runtime)

Model for all: `Qwen3-4B-Q4_K_M.gguf` (2,497,280,256 bytes, SHA-256 `7485FE6F...4FDF5`, already copied into LU's models folder; AnythingLLM uses the byte-near-identical `unsloth/Qwen3-4B-GGUF` Q4_K_M, see the 2026-11-21 doc). Second model class for coverage: one MoE or 8B-class Q4_K_M that fits the 5080. Lock for every row: temperature 0, `max_tokens` 128, unique nonce prefix, 1 warm-up + at least 20 measured rounds for any claim (`docs/PERFORMANCE_BENCHMARKING.md` requires 20 with a paired bootstrap lower bound; the 5-run numbers from 2026-11-21 are smoke-level only), same context (force 16384) and KV type f16 AND q8_0 reported separately because defaults differ.

| Metric | How | Apps and runtime | Existing tool | Status |
|---|---|---|---|---|
| Decode tok/s (short, 1k, 4k) | stream timings from engine `timings`, not UI | PaRuX CUDA, PaRuX Vulkan on the 5080 (not the iGPU), LU with the external CUDA `b12621` engine (owner shortcut) and LU bundled Vulkan, AnythingLLM lemond CUDA, bare llama.cpp CUDA same flags | `tools/perf-launch-bench.py`, `scripts/benchmark-competitive-inference.ps1` | Measured 2026-11-21 (5-run): PaRuX CUDA 193/186/178, AnythingLLM 227/213/190, LU UI 162/157/137, bare CUDA b12233 214/192/181; needs 20-run repeat with equal KV type |
| TTFT (cold prompt, warm cache) | request send to first token; also UI-level (Send click to first token) for all three apps via CDP/UIA | all | `perf-launch-bench.py` (API), `scripts/benchmark-reference-stream.ps1` (attested) | API-level measured; UI-level NOT measured for PaRuX or AnythingLLM |
| Prompt processing tok/s | `prompt_per_second` from engine at 2k and 8k prompts | all | `perf-launch-bench.py` (2k prefill) | partial: no 8k, no cross-app table |
| Prefix-cache reuse TTFT | second request same prefix | all | `perf-launch-bench.py` | PaRuX only |
| Model load time | process start to `/health` ready, cold file cache and warm | all | none unified | NOT measured except PaRuX provider-ready 6.7-11.5 s (iGPU) |
| App cold start / shutdown | `tools/measure-app-startup.ps1`; competitors by UIA/CDP | all | exists | measured: PaRuX 1.06-1.38 s vs LU 0.8 s and AnythingLLM 3 s (LU and AnythingLLM only hide to tray) |
| Idle RAM/CPU, no model and model loaded | 60 s settle, 30 s window | all | `tools/soak-core-idle.ps1` | measured once; PaRuX idle CPU 1.3-3.5% vs LU 0.2% is an open deficit, not profiled |
| VRAM, model loaded | `nvidia-smi` per process at fixed ctx and KV | all | `scripts/benchmark-gpu.ps1` | measured as shipped only (contexts differ); repeat at forced equal ctx |
| RAG indexing time and query latency | one synthetic corpus (use `KnowledgeBenchmark` generator; add a real-text set of 200 pages) indexed by each app; 30 questions with known answers | PaRuX (hybrid and keyword), AnythingLLM (default embedder), LU (needs its 84 MB embedder) | `Server/Knowledge/KnowledgeBenchmark.cs`, `scripts/benchmark-embedding-quality.py` | NOT measured for any app; the Core RAG routes are desktop-owner gated, so script through a test owner token or the harness inside the server tests |
| RAG answer quality | correct/total, citation present, no-answer on unanswerable | same | `RetrievalEvaluationService.cs` | 5/5 for AnythingLLM and LU on tiny files only; PaRuX NOT measured |
| Embedding throughput | docs/s on the same embedder model | PaRuX vs AnythingLLM default embedder | `benchmark-embedding-quality.py` | NOT measured |
| Fine-tune throughput | tokens/s, step time, peak VRAM, final loss on the same 1k-row SFT set, same base model (Qwen3-1.7B or 0.5B), QLoRA r=16, same seq length | PaRuX Training Lab vs Unsloth (isolated venv, `UNSLOTH_NO_TORCH` is for inference only so training needs the PyTorch stack) | `TrainingMetricsParser.cs` output; `tools/bic13/eval_adapter.py` for quality | NOT measured. Unsloth was BLOCKED-EXTERNAL because its installer needs machine-level Git/CMake/VS Build Tools/CUDA. Options: owner approval for the installer, or an isolated `pip`/venv install of `unsloth` in a fresh folder; if neither, publish no training-speed claim |
| Unsloth Studio inference | same table as above using Studio's own llama.cpp | Unsloth | none | BLOCKED-EXTERNAL |
| Tool-call accuracy and agent task success | 20 scripted tool tasks (fixtures in `tests/` and `tools/browser-agent-fixture.mjs`) | PaRuX, LU, AnythingLLM agent | `scripts/benchmark-coding-agent-competitors.ps1`, `tools/coding-model-benchmark.ps1` | NOT measured across apps |
| Quality parity of quants | KL divergence vs Q8 on the synthetic QA set | PaRuX quant ladder only | `tools/quant-ladder-kld.py` | exists, PaRuX only |

### 3.2 Rules that keep the numbers honest
- Same GGUF bytes (hash in the manifest), same ctx, same KV type, same parallel slots, same flash-attention setting, same prompt text with chat-template hash recorded; where an app forces different settings, report it in a column instead of hiding it.
- Engine-level and UI-level numbers are separate tables; never compare an API number with a DOM number.
- Quiet machine gate (no `testhost`, no other `llama-server`), power plan fixed, GPU clocks logged, driver version recorded, at least 20 rounds and a bootstrap bound before any "faster" word; otherwise write "no difference shown".
- Thinking tokens: record whether reasoning is on; count only visible tokens for TTFT-to-answer, and report reasoning tokens separately (AnythingLLM counted reasoning tokens by default on 2026-11-21).
- Each competitor uses its own runtime on purpose; also run bare llama.cpp CUDA with PaRuX's flags and with each competitor's flags to separate "engine flags" from "app overhead".

### 3.3 What is already measured (summary, from `docs/COMPETITOR_MEASURED_2026-11-19.md` and `-21.md`)
PaRuX 4.4.0 vs LU 3.1.1 vs AnythingLLM 1.20.0, Ryzen 7 8840U + RTX 5080 16 GB: cold start, shutdown, decode and TTFT at three prompt lengths, idle RAM/CPU, VRAM as shipped, and a 5-question document test (not PaRuX). Open deficits recorded there: wrong default device (since fixed in code, unmeasured), 920-token context pack (slice 1), startup (WinUI window construction), idle CPU, decode 162-194 vs 213-227 tok/s on the same GPU (not isolated: q8 KV, flash-attn, `--poll 45`, Vulkan vs CUDA build). Not measured anywhere: model load time, RAG indexing/query latency, embedding throughput, prompt processing at 8k, fine-tune throughput, tool-use tasks, Unsloth anything.

### 3.4 Performance work items (feed the backlog)
- P1 Re-measure PaRuX as shipped (portable, no manual CUDA) on this PC to prove the Vulkan device-choice fix puts the model on the 5080. Files: none; run `tools/perf-launch-bench.py` plus `nvidia-smi`. GPU yes. Do first.
- P2 Isolate the 162-194 vs 213-227 tok/s decode gap: run bare llama.cpp b12621 with (a) PaRuX flags, (b) LU flags, (c) AnythingLLM flags; flip one flag at a time (KV q8_0 vs f16, `--poll`, ctx 16k vs 40k, flash-attn). Then adopt the fastest default that does not cost quality. Files: `src/PaRuX.Server/RuntimeLaunchOptimizer.cs`, `LocalAiProcessManager.cs`. GPU yes.
- P3 Idle CPU: profile the 1.3-3.5% (UI timers, Core polling, `/health` loops); target below 0.5% idle with no model. Files: App timers, `src/PaRuX.Server` poll loops; verify with `tools/soak-core-idle.ps1`. GPU no.
- P4 Model load time table across apps (cold and warm file cache) using `/health` readiness. GPU yes.
- P5 PaRuX RAG baseline (indexing seconds, query latency, answer accuracy) with `KnowledgeBenchmark` plus the 3-file question set, then the same on AnythingLLM. GPU no (CPU embedder).
- P6 Fine-tune throughput only if Unsloth can be installed in isolation with owner approval; otherwise record PaRuX's own numbers (tokens/s, VRAM) and make no comparison claim.

## 4. Suggested next actions
1. Run P1 and P2 (GPU, short) and slice 2 so every later claim has a harness.
2. Start wave A in parallel worktrees: slices 1, 4, 5, 6, 8 first (highest value per size, all independent).
3. Ask the owner before: installing Unsloth (machine-level or isolated venv), any abliteration feature, any cloud-account skills (Gmail/Outlook).
