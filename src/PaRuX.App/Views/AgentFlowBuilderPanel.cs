using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Agent Flows: a no-code builder over the AutomationEngine. The person fills a trigger block, optional condition blocks and an
    /// action block; the Core validates and compiles them (nothing is executed by this view). A new flow is created switched off.
    /// Every run is listed with its honest outcome (fired, skipped, failed) and a started run can be stopped.
    /// </summary>
    public sealed class AgentFlowBuilderPanel : StackPanel
    {
        private static string T(string en, string it) => UiText.Pick(en, it);

        private static readonly (string Type, string En, string It)[] Triggers =
        {
            (AutomationTriggerType.Schedule, "On a schedule", "A intervalli regolari"),
            (AutomationTriggerType.FileCreated, "A file appears in a folder", "Compare un file in una cartella"),
            (AutomationTriggerType.GitCommit, "A new Git commit", "Un nuovo commit Git"),
            (AutomationTriggerType.ModelDownloadCompleted, "A model download finishes", "Finisce il download di un modello"),
            (AutomationTriggerType.ComponentHealthChanged, "A component's health changes", "Cambia lo stato di un componente"),
            (AutomationTriggerType.TaskCompleted, "An agent task ends", "Termina un'attività dell'agente")
        };

        private static readonly (string Signal, string En, string It)[] Signals =
        {
            (AutomationConditionSignal.FailedTaskCount, "Failed tasks", "Attività fallite"),
            (AutomationConditionSignal.RunningTaskCount, "Running tasks", "Attività in corso"),
            (AutomationConditionSignal.CompletedTaskCount, "Completed tasks", "Attività completate"),
            (AutomationConditionSignal.FailedDownloadCount, "Failed downloads", "Download falliti"),
            (AutomationConditionSignal.ActiveDownloadCount, "Active downloads", "Download attivi"),
            (AutomationGuardSignal.TriggerFileName, "File name (file trigger)", "Nome del file (trigger file)")
        };

        private readonly TextBox _name = new() { MinWidth = 260 };
        private readonly ComboBox _trigger = new() { MinWidth = 260 };
        private readonly TextBox _interval = new() { Text = "3600", MinWidth = 160 };
        private readonly TextBox _folder = new() { MinWidth = 320 };
        private readonly TextBox _filter = new() { Text = "*", MinWidth = 160 };
        private readonly TextBox _repo = new() { MinWidth = 320 };
        private readonly TextBox _branch = new() { MinWidth = 160 };
        private readonly TextBox _download = new() { MinWidth = 220 };
        private readonly TextBox _component = new() { MinWidth = 220 };
        private readonly TextBox _taskRole = new() { MinWidth = 160 };
        private readonly TextBox _goal = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MinWidth = 420 };
        private readonly TextBox _role = new() { MinWidth = 160 };
        private readonly ToggleSwitch _enableNow = new() { IsOn = false };
        private readonly StackPanel _conditions = new() { Spacing = 6 };
        private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _list = new() { Spacing = 8 };
        private readonly List<(ComboBox Signal, ComboBox Op, TextBox Value, StackPanel Row)> _rows = new();
        private bool _busy;

        public AgentFlowBuilderPanel()
        {
            Spacing = 10;
            _name.Header = T("Flow name", "Nome del flusso");
            _trigger.Header = T("1. When (trigger block)", "1. Quando (blocco trigger)");
            foreach (var t in Triggers) _trigger.Items.Add(new ComboBoxItem { Tag = t.Type, Content = T(t.En, t.It) });
            _trigger.SelectedIndex = 0;
            _trigger.SelectionChanged += (_, _) => UpdateTriggerFields();
            _interval.Header = T("Every how many seconds", "Ogni quanti secondi");
            _folder.Header = T("Folder to watch", "Cartella da osservare");
            _filter.Header = T("File name pattern (for example *.csv)", "Schema del nome file (per esempio *.csv)");
            _repo.Header = T("Git repository folder", "Cartella del repository Git");
            _branch.Header = T("Branch (optional)", "Branch (facoltativo)");
            _download.Header = T("File name contains (optional)", "Il nome del file contiene (facoltativo)");
            _component.Header = T("Component id", "Id del componente");
            _taskRole.Header = T("Agent role (optional)", "Ruolo dell'agente (facoltativo)");
            _goal.Header = T("3. Then (action block): what the agent should do", "3. Allora (blocco azione): cosa deve fare l'agente");
            _role.Header = T("Agent role (optional)", "Ruolo dell'agente (facoltativo)");
            _enableNow.Header = T("Switch the flow on right away (otherwise it is created off)", "Attiva subito il flusso (altrimenti viene creato spento)");
            AutomationProperties.SetAutomationId(_name, "Flows_Name");
            AutomationProperties.SetAutomationId(_trigger, "Flows_Trigger_ComboBox");
            AutomationProperties.SetAutomationId(_goal, "Flows_Goal");
            AutomationProperties.SetAutomationId(_enableNow, "Flows_EnableNow_Switch");

            var addCondition = new Button { Content = T("Add a condition block", "Aggiungi un blocco condizione") };
            addCondition.Click += (_, _) => AddConditionRow();
            AutomationProperties.SetAutomationId(addCondition, "Flows_AddCondition_Button");
            var validate = new Button { Content = T("Check", "Verifica") };
            validate.Click += async (_, _) => await SubmitAsync(createIt: false);
            AutomationProperties.SetAutomationId(validate, "Flows_Validate_Button");
            var create = new Button { Content = T("Create flow", "Crea flusso"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            create.Click += async (_, _) => await SubmitAsync(createIt: true);
            AutomationProperties.SetAutomationId(create, "Flows_Create_Button");
            AutomationProperties.SetAutomationId(_status, "Flows_Status");
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

            Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.78,
                Text = T("Build a flow from blocks: when something happens, and the conditions hold, an agent task is started. PaRuX validates every block before saving; a new flow is created switched off. Runs appear in the history with their real outcome, and a started run can be stopped. Nothing runs during Gaming Mode.",
                    "Costruisci un flusso a blocchi: quando succede qualcosa e le condizioni sono vere, parte un'attività dell'agente. PaRuX convalida ogni blocco prima di salvare; un nuovo flusso viene creato spento. Le esecuzioni compaiono nella cronologia con l'esito reale e un'esecuzione avviata si può fermare. Nulla gira durante la modalità Gaming.")
            });
            Children.Add(_name);
            Children.Add(_trigger);
            Children.Add(_interval); Children.Add(_folder); Children.Add(_filter); Children.Add(_repo); Children.Add(_branch);
            Children.Add(_download); Children.Add(_component); Children.Add(_taskRole);
            Children.Add(new TextBlock { Text = T("2. Only if (condition blocks, all must hold)", "2. Solo se (blocchi condizione, devono valere tutti)"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            Children.Add(_conditions);
            Children.Add(addCondition);
            Children.Add(_goal);
            Children.Add(_hint);
            Children.Add(_role);
            Children.Add(_enableNow);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(validate);
            buttons.Children.Add(create);
            Children.Add(buttons);
            Children.Add(_status);
            Children.Add(new TextBlock { Text = T("Your flows", "I tuoi flussi"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            Children.Add(_list);
            UpdateTriggerFields();
            Loaded += async (_, _) => await RefreshAsync();
        }

        private string TriggerType => _trigger.SelectedItem is ComboBoxItem { Tag: string t } ? t : AutomationTriggerType.Schedule;

        private void UpdateTriggerFields()
        {
            string t = TriggerType;
            _interval.Visibility = t == AutomationTriggerType.Schedule ? Visibility.Visible : Visibility.Collapsed;
            _folder.Visibility = _filter.Visibility = t == AutomationTriggerType.FileCreated ? Visibility.Visible : Visibility.Collapsed;
            _repo.Visibility = _branch.Visibility = t == AutomationTriggerType.GitCommit ? Visibility.Visible : Visibility.Collapsed;
            _download.Visibility = t == AutomationTriggerType.ModelDownloadCompleted ? Visibility.Visible : Visibility.Collapsed;
            _component.Visibility = t == AutomationTriggerType.ComponentHealthChanged ? Visibility.Visible : Visibility.Collapsed;
            _taskRole.Visibility = t == AutomationTriggerType.TaskCompleted ? Visibility.Visible : Visibility.Collapsed;
            string[] ph = AgentFlowPlaceholders(t);
            _hint.Text = ph.Length == 0
                ? T("This trigger offers no placeholders in the goal.", "Questo trigger non offre segnaposto nell'obiettivo.")
                : T("You can put these in the goal: ", "Puoi usare questi nell'obiettivo: ") + string.Join(", ", ph.Select(p => "{" + p + "}"));
        }

        // Mirrors AgentFlowCompiler.PlaceholdersFor (the Core stays authoritative and refuses anything else).
        private static string[] AgentFlowPlaceholders(string trigger) => trigger switch
        {
            AutomationTriggerType.FileCreated => new[] { "fileName", "folderPath" },
            AutomationTriggerType.GitCommit => new[] { "commitSha", "commitMessage", "repositoryPath" },
            AutomationTriggerType.ModelDownloadCompleted => new[] { "downloadFileName", "downloadLocalPath" },
            AutomationTriggerType.ComponentHealthChanged => new[] { "componentId", "componentHealth", "componentPreviousHealth" },
            AutomationTriggerType.TaskCompleted => new[] { "taskId", "taskGoal", "taskStatus", "taskRole" },
            _ => Array.Empty<string>()
        };

        private void AddConditionRow()
        {
            if (_rows.Count >= AgentFlowCompiler_Max) { _status.Text = T("At most 5 condition blocks.", "Al massimo 5 blocchi condizione."); return; }
            var signal = new ComboBox { MinWidth = 200 };
            foreach (var s in Signals) signal.Items.Add(new ComboBoxItem { Tag = s.Signal, Content = T(s.En, s.It) });
            signal.SelectedIndex = 0;
            var op = new ComboBox { MinWidth = 140 };
            void FillOps()
            {
                bool file = signal.SelectedItem is ComboBoxItem { Tag: AutomationGuardSignal.TriggerFileName };
                op.Items.Clear();
                foreach (var (tag, en, it) in file
                    ? new[] { ("contains", "contains", "contiene"), ("not_contains", "does not contain", "non contiene"), ("eq", "is", "è"), ("neq", "is not", "non è") }
                    : new[] { ("gte", "is at least", "è almeno"), ("gt", "is more than", "è più di"), ("lte", "is at most", "è al massimo"), ("lt", "is less than", "è meno di"), ("eq", "is exactly", "è esattamente") })
                    op.Items.Add(new ComboBoxItem { Tag = tag, Content = T(en, it) });
                op.SelectedIndex = 0;
            }
            signal.SelectionChanged += (_, _) => FillOps();
            FillOps();
            var value = new TextBox { MinWidth = 140, Text = "1", PlaceholderText = T("value", "valore") };
            var remove = new Button { Content = T("Remove", "Rimuovi") };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(signal); row.Children.Add(op); row.Children.Add(value); row.Children.Add(remove);
            var entry = (signal, op, value, row);
            remove.Click += (_, _) => { _rows.Remove(entry); _conditions.Children.Remove(row); };
            _rows.Add(entry);
            _conditions.Children.Add(row);
        }

        private const int AgentFlowCompiler_Max = 5;

        private AgentFlowRequest Build() => new()
        {
            Name = _name.Text?.Trim() ?? string.Empty,
            Enabled = _enableNow.IsOn,
            Trigger = new AgentFlowTriggerBlock
            {
                Type = TriggerType,
                IntervalSeconds = int.TryParse(_interval.Text?.Trim(), out int s) ? s : -1,
                FolderPath = _folder.Text?.Trim() ?? string.Empty,
                Filter = string.IsNullOrWhiteSpace(_filter.Text) ? "*" : _filter.Text.Trim(),
                RepositoryPath = _repo.Text?.Trim() ?? string.Empty,
                Branch = _branch.Text?.Trim() ?? string.Empty,
                FileNameContains = _download.Text?.Trim() ?? string.Empty,
                ComponentId = _component.Text?.Trim() ?? string.Empty,
                Role = _taskRole.Text?.Trim() ?? string.Empty
            },
            Conditions = _rows.Select(r => new AutomationGuard
            {
                Signal = (r.Signal.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
                Operator = (r.Op.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
                Value = r.Value.Text?.Trim() ?? string.Empty
            }).ToList(),
            Action = new AgentFlowActionBlock { Goal = _goal.Text?.Trim() ?? string.Empty, Role = string.IsNullOrWhiteSpace(_role.Text) ? null : _role.Text.Trim() }
        };

        private async Task SubmitAsync(bool createIt)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                AgentFlowRequest request = Build();
                if (!createIt)
                {
                    await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/agent-flows/validate", request, CancellationToken.None);
                    _status.Text = T("The blocks are valid. Nothing was saved.", "I blocchi sono validi. Non è stato salvato nulla.");
                    return;
                }
                await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/agent-flows", request, CancellationToken.None);
                _status.Text = request.Enabled
                    ? T("Flow created and switched on.", "Flusso creato e attivato.")
                    : T("Flow created, switched off. Turn it on from the list when you are ready.", "Flusso creato, spento. Accendilo dall'elenco quando sei pronto.");
                await RefreshAsync();
            }
            catch (CoreRequestException ex) { _status.Text = T("Not accepted: ", "Non accettato: ") + ex.Message; }
            catch (Exception ex) { Log.Write("AgentFlowBuilderPanel submit failed: " + ex.Message); _status.Text = T("Could not reach the Core: ", "Impossibile raggiungere il Core: ") + ex.Message; }
            finally { _busy = false; }
        }

        private async Task RefreshAsync()
        {
            try
            {
                JsonElement flows = await CoreJson.SendAsync(HttpMethod.Get, "/api/v1/agent-flows", null, CancellationToken.None);
                _list.Children.Clear();
                if (flows.ValueKind != JsonValueKind.Array || flows.GetArrayLength() == 0)
                {
                    _list.Children.Add(new TextBlock { Text = T("No flows yet.", "Nessun flusso per ora."), Opacity = 0.7 });
                    return;
                }
                foreach (JsonElement f in flows.EnumerateArray()) _list.Children.Add(BuildFlowCard(f));
            }
            catch (Exception ex)
            {
                Log.Write("AgentFlowBuilderPanel refresh failed: " + ex.Message);
                _status.Text = T("Could not read the flows: ", "Impossibile leggere i flussi: ") + ex.Message;
            }
        }

        private UIElement BuildFlowCard(JsonElement flow)
        {
            JsonElement a = flow.GetProperty("automation");
            string id = CoreJson.Str(a, "id");
            var box = new StackPanel { Spacing = 6, Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            box.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            box.Children.Add(new TextBlock { Text = CoreJson.Str(a, "name"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            box.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                Text = T("Trigger: ", "Trigger: ") + CoreJson.Str(a, "triggerType") + " · " + T("goal: ", "obiettivo: ") + CoreJson.Str(flow, "goal")
                    + " · " + (flow.TryGetProperty("conditions", out JsonElement c) ? c.GetArrayLength() : 0) + T(" condition(s)", " condizione/i")
            });
            if (CoreJson.Str(a, "lastResult").Length > 0)
                box.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Text = T("Last result: ", "Ultimo esito: ") + CoreJson.Str(a, "lastResult") });

            // The header names what the switch controls; its own On/Off text shows the state. A header that read "On"
            // above a switch showing "Off" contradicted itself.
            var enabled = new ToggleSwitch { IsOn = CoreJson.Bool(a, "enabled"), Header = T("Flow switched on", "Flusso attivo") };
            AutomationProperties.SetAutomationId(enabled, "Flows_Enabled_" + id);
            AutomationProperties.SetName(enabled, T("Flow switched on: ", "Flusso attivo: ") + CoreJson.Str(a, "name"));
            bool loading = true;
            enabled.Toggled += async (_, _) =>
            {
                if (loading) return;
                try { await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/automations/" + Uri.EscapeDataString(id) + "/enabled", new { enabled = enabled.IsOn }, CancellationToken.None); }
                catch (Exception ex) { _status.Text = T("Could not change it: ", "Impossibile cambiare: ") + ex.Message; await RefreshAsync(); }
            };
            loading = false;
            var historyText = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, FontSize = 12 };
            var run = new Button { Content = T("Run now", "Esegui ora") };
            var stop = new Button { Content = T("Stop last run", "Ferma l'ultima esecuzione") };
            var history = new Button { Content = T("History", "Cronologia") };
            var delete = new Button { Content = T("Delete", "Elimina") };
            AutomationProperties.SetAutomationId(run, "Flows_Run_" + id);
            run.Click += async (_, _) =>
            {
                try
                {
                    JsonElement r = await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/agent-flows/" + Uri.EscapeDataString(id) + "/run", null, CancellationToken.None);
                    _status.Text = T("Run ", "Esecuzione ") + CoreJson.Str(r, "outcome") + ": " + CoreJson.Str(r, "detail");
                    await RefreshAsync();
                }
                catch (CoreRequestException ex) { _status.Text = T("Not started: ", "Non avviata: ") + ex.Message; }
                catch (Exception ex) { _status.Text = T("Could not reach the Core: ", "Impossibile raggiungere il Core: ") + ex.Message; }
            };
            stop.Click += async (_, _) =>
            {
                try
                {
                    JsonElement runs = await CoreJson.SendAsync(HttpMethod.Get, "/api/v1/agent-flows/" + Uri.EscapeDataString(id) + "/runs?limit=1", null, CancellationToken.None);
                    string task = runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0 ? CoreJson.Str(runs[0], "taskId") : string.Empty;
                    if (task.Length == 0) { _status.Text = T("The last run did not start a task, so there is nothing to stop.", "L'ultima esecuzione non ha avviato un'attività, quindi non c'è nulla da fermare."); return; }
                    await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(task) + "/cancel", null, CancellationToken.None);
                    _status.Text = T("Stop requested for the task of the last run.", "Interruzione richiesta per l'attività dell'ultima esecuzione.");
                }
                catch (Exception ex) { _status.Text = T("Could not stop it: ", "Impossibile fermarla: ") + ex.Message; }
            };
            history.Click += async (_, _) =>
            {
                try
                {
                    JsonElement runs = await CoreJson.SendAsync(HttpMethod.Get, "/api/v1/agent-flows/" + Uri.EscapeDataString(id) + "/runs?limit=10", null, CancellationToken.None);
                    historyText.Text = runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0
                        ? string.Join("\n", runs.EnumerateArray().Select(r => CoreJson.Str(r, "atUtc") + "  " + CoreJson.Str(r, "outcome") + "  " + CoreJson.Str(r, "detail")))
                        : T("No runs yet.", "Nessuna esecuzione per ora.");
                    historyText.Visibility = historyText.Visibility == Visibility.Visible && history.Tag is true ? Visibility.Collapsed : Visibility.Visible;
                    history.Tag = historyText.Visibility == Visibility.Visible;
                }
                catch (Exception ex) { _status.Text = T("Could not read the history: ", "Impossibile leggere la cronologia: ") + ex.Message; }
            };
            delete.Click += async (_, _) =>
            {
                try { await CoreJson.SendAsync(HttpMethod.Delete, "/api/v1/automations/" + Uri.EscapeDataString(id), null, CancellationToken.None); await RefreshAsync(); }
                catch (Exception ex) { _status.Text = T("Could not delete it: ", "Impossibile eliminarlo: ") + ex.Message; }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(enabled); row.Children.Add(run); row.Children.Add(stop); row.Children.Add(history); row.Children.Add(delete);
            box.Children.Add(row);
            box.Children.Add(historyText);
            return box;
        }
    }
}
