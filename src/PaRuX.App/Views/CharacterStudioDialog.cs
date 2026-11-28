using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace PaRuX.Views;

/// <summary>
/// Character Studio wizard: Character, Pictures, Training, Done. A non-technical user can press Next on the defaults;
/// rank, steps, learning rate and resolution sit behind "Advanced". Every string is English with an Italian pair.
/// All state lives in PaRuX Core (/api/v2/characters), so closing the dialog never stops a running training.
/// </summary>
internal sealed class CharacterStudioDialog
{
    private static string T(string en, string it) => UiText.Pick(en, it);

    private readonly XamlRoot _root;
    private ContentDialog? _dialog;
    private CharacterProfile? _character;
    private int _step;
    private readonly CancellationTokenSource _life = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _polling;
    private readonly HashSet<string> _shown = new();

    private readonly TextBlock _stepTitle = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), Visibility = Visibility.Collapsed };
    private readonly StackPanel[] _steps = { new() { Spacing = 10 }, new() { Spacing = 10 }, new() { Spacing = 10 }, new() { Spacing = 10 } };
    private readonly Button _back = new() { Content = T("Back", "Indietro"), MinHeight = 40 };
    private readonly Button _next = new() { Content = T("Next", "Avanti"), MinHeight = 40 };

    // step 1
    private readonly TextBox _name = new() { Header = T("Name", "Nome"), PlaceholderText = T("For example Mira", "Ad esempio Mira"), MaxLength = 80 };
    private readonly TextBox _appearance = new()
    {
        Header = T("What does the character look like?", "Che aspetto ha il personaggio?"),
        PlaceholderText = T("Young woman, short red hair, green eyes, freckles, wears a denim jacket",
            "Giovane donna, capelli rossi corti, occhi verdi, lentiggini, giacca di jeans"),
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1200, MinHeight = 90
    };
    private readonly ComboBox _persona = new() { Header = T("Attach to a chat persona (optional)", "Collega a un personaggio della chat (facoltativo)"), HorizontalAlignment = HorizontalAlignment.Stretch };

    // step 2
    private readonly TextBlock _picturesInfo = new() { TextWrapping = TextWrapping.Wrap };
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.None, MaxHeight = 360 };
    private readonly Button _makePictures = new() { Content = T("Make pictures for me", "Crea le immagini per me"), MinHeight = 40 };
    private readonly Button _addPhotos = new() { Content = T("Add my own photos…", "Aggiungi le mie foto…"), MinHeight = 40 };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 24, Height = 24, Visibility = Visibility.Collapsed };

    // step 3
    private readonly RadioButtons _preset = new() { Header = T("How much time do you want to spend?", "Quanto tempo vuoi dedicare?") };
    private readonly TextBlock _planText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _trainerText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _trainerButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button _start = new() { Content = T("Start training", "Avvia l'addestramento"), MinHeight = 44 };
    private readonly Button _cancel = new() { Content = T("Cancel", "Annulla"), Visibility = Visibility.Collapsed };
    private readonly Button _resume = new() { Content = T("Resume", "Riprendi"), Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock _progressText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _samples = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly NumberBox _rank = new() { Header = T("Detail capacity (rank)", "Capacità di dettaglio (rank)"), Minimum = 4, Maximum = 128, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, PlaceholderText = T("Automatic", "Automatico") };
    private readonly NumberBox _steps2 = new() { Header = T("Steps", "Passi"), Minimum = 10, Maximum = 8000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, PlaceholderText = T("Automatic", "Automatico") };
    private readonly NumberBox _lr = new() { Header = T("Learning rate", "Velocità di apprendimento"), Minimum = 0.000001, Maximum = 0.001, SmallChange = 0.00001, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, PlaceholderText = T("Automatic", "Automatico") };
    private readonly NumberBox _res = new() { Header = T("Picture size", "Dimensione immagini"), Minimum = 256, Maximum = 1280, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, PlaceholderText = T("Automatic", "Automatico") };

    // step 4
    private readonly TextBlock _doneText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _portrait = new() { Width = 220, Height = 220, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _testPicture = new() { Content = T("Make a test picture", "Crea un'immagine di prova"), MinHeight = 40 };
    private readonly Image _testImage = new() { Width = 320, Height = 320, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _testStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    private CharacterStudioDialog(XamlRoot root) { _root = root; }

    internal static async Task ShowAsync(XamlRoot root, string? personaId = null)
    {
        var studio = new CharacterStudioDialog(root);
        await studio.RunAsync(personaId).ConfigureAwait(true);
    }

    private async Task RunAsync(string? personaId)
    {
        Build();
        await LoadPersonasAsync(personaId);
        try
        {
            CharacterListResponse list = await CharacterStudioClient.Current.ListAsync(_life.Token);
            // Resume the most recent unfinished character instead of making the user start over.
            CharacterProfile? open = list.Characters.OrderByDescending(c => c.UpdatedUtc).FirstOrDefault(c => c.Binding == null && c.Images.Count > 0
                && (personaId == null || c.PersonaId == personaId));
            if (open != null) { SetCharacter(open); _step = StepFor(open); }
        }
        catch (Exception ex) { ShowError(ex); }
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        ShowStep();
        await _dialog!.ShowAsync();
        _timer.Stop();
        _life.Cancel();
    }

    private static int StepFor(CharacterProfile c) => c.Binding != null ? 3 : c.Training.Stage is CharacterStage.Training or CharacterStage.Failed or CharacterStage.Cancelled or CharacterStage.Interrupted ? 2 : 1;

    private void Build()
    {
        _steps[0].Children.Add(Para(T("Give your character a name and describe the look. PaRuX will make pictures of them in many poses and teach the image model to draw them the same way every time.",
            "Dai un nome al personaggio e descrivi il suo aspetto. PaRuX creerà immagini in molte pose e insegnerà al modello a disegnarlo sempre allo stesso modo.")));
        _steps[0].Children.Add(_name);
        _steps[0].Children.Add(_appearance);
        _steps[0].Children.Add(_persona);

        _steps[1].Children.Add(_picturesInfo);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_makePictures);
        row.Children.Add(_addPhotos);
        row.Children.Add(_busy);
        _steps[1].Children.Add(row);
        _steps[1].Children.Add(Para(T("Click a picture's cross to remove it. Keep only clear pictures where the face is visible.",
            "Fai clic sulla croce di un'immagine per rimuoverla. Tieni solo immagini nitide con il volto visibile."), 0.7));
        _steps[1].Children.Add(_grid);

        foreach ((string key, string en, string it) in new[]
        {
            (CharacterTrainingPreset.Quick, "Quick (about 15 minutes)", "Veloce (circa 15 minuti)"),
            (CharacterTrainingPreset.Balanced, "Balanced", "Equilibrato"),
            (CharacterTrainingPreset.Best, "Best quality", "Qualità massima")
        })
            _preset.Items.Add(new RadioButton { Content = T(en, it), Tag = key });
        _preset.SelectedIndex = 1;
        _preset.SelectionChanged += async (_, _) => await RefreshPlanAsync();
        _steps[2].Children.Add(_preset);
        _steps[2].Children.Add(_planText);
        _steps[2].Children.Add(_trainerText);
        _steps[2].Children.Add(_trainerButtons);
        var adv = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
        adv.Children.Add(Para(T("Leave these empty unless you know what they do; PaRuX picks values that fit your graphics card.",
            "Lasciali vuoti se non sai cosa fanno; PaRuX sceglie valori adatti alla tua scheda grafica."), 0.7));
        foreach (NumberBox nb in new[] { _rank, _steps2, _lr, _res }) { nb.Value = double.NaN; adv.Children.Add(nb); }
        _steps[2].Children.Add(new Expander { Header = T("Advanced", "Avanzate"), Content = adv, HorizontalAlignment = HorizontalAlignment.Stretch });
        var trainRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        trainRow.Children.Add(_start); trainRow.Children.Add(_cancel); trainRow.Children.Add(_resume);
        _steps[2].Children.Add(trainRow);
        _steps[2].Children.Add(_progress);
        _steps[2].Children.Add(_progressText);
        _steps[2].Children.Add(new ScrollViewer { Content = _samples, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        _steps[3].Children.Add(_doneText);
        _steps[3].Children.Add(_portrait);
        _steps[3].Children.Add(Para(T("From now on, pictures of this character use the trained look automatically, in Create and in chat.",
            "D'ora in poi le immagini di questo personaggio usano automaticamente l'aspetto addestrato, in Crea e in chat.")));
        _steps[3].Children.Add(_testPicture);
        _steps[3].Children.Add(_testStatus);
        _steps[3].Children.Add(_testImage);

        var content = new StackPanel { Spacing = 12, MinWidth = 640, MaxWidth = 760 };
        content.Children.Add(_stepTitle);
        content.Children.Add(_error);
        foreach (StackPanel s in _steps) content.Children.Add(s);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        nav.Children.Add(_back); nav.Children.Add(_next);
        content.Children.Add(nav);

        _dialog = new ContentDialog
        {
            Title = T("Character Studio", "Studio personaggi"),
            Content = new ScrollViewer { Content = content, MaxHeight = 640 },
            CloseButtonText = T("Close", "Chiudi"),
            XamlRoot = _root
        };
        Name(_name, "CharStudio_Name"); Name(_appearance, "CharStudio_Appearance"); Name(_next, "CharStudio_Next"); Name(_back, "CharStudio_Back");
        Name(_makePictures, "CharStudio_MakePictures"); Name(_addPhotos, "CharStudio_AddPhotos"); Name(_start, "CharStudio_StartTraining");
        Name(_cancel, "CharStudio_Cancel"); Name(_resume, "CharStudio_Resume"); Name(_testPicture, "CharStudio_TestPicture");

        _back.Click += (_, _) => { if (_step > 0) { _step--; ShowStep(); } };
        _next.Click += async (_, _) => await NextAsync();
        _makePictures.Click += async (_, _) => await MakePicturesAsync();
        _addPhotos.Click += async (_, _) => await AddPhotosAsync();
        _start.Click += async (_, _) => await StartTrainingAsync(resume: false);
        _resume.Click += async (_, _) => await StartTrainingAsync(resume: true);
        _cancel.Click += async (_, _) => await Run(async () => SetCharacter(await CharacterStudioClient.Current.CancelTrainingAsync(_character!.Id, _life.Token)));
        _testPicture.Click += async (_, _) => await TestPictureAsync();
    }

    private static void Name(DependencyObject d, string id) => AutomationProperties.SetAutomationId(d, id);

    private static TextBlock Para(string text, double opacity = 1) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

    // ---- navigation -------------------------------------------------------------------------------------------------------------------

    private void ShowStep()
    {
        for (int i = 0; i < _steps.Length; i++) _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        string[] names =
        {
            T("Character", "Personaggio"), T("Pictures", "Immagini"), T("Training", "Addestramento"), T("Done", "Fatto")
        };
        _stepTitle.Text = T("Step ", "Passo ") + (_step + 1) + T(" of 4: ", " di 4: ") + names[_step];
        _back.Visibility = _step is 0 or 3 ? Visibility.Collapsed : Visibility.Visible;
        _next.Visibility = _step == 3 ? Visibility.Collapsed : Visibility.Visible;
        _error.Visibility = Visibility.Collapsed;
        RenderCharacter();
        if (_step == 2) _ = RefreshPlanAsync();
        if (_step == 3) _ = ShowPortraitAsync();
    }

    private async Task NextAsync()
    {
        if (_step == 0)
        {
            await Run(async () =>
            {
                string? personaId = (_persona.SelectedItem as ComboBoxItem)?.Tag as string;
                if (_character == null)
                    SetCharacter(await CharacterStudioClient.Current.CreateAsync(new CreateCharacterRequest { Name = _name.Text, Appearance = _appearance.Text, PersonaId = personaId }, _life.Token));
                else
                    SetCharacter(await CharacterStudioClient.Current.UpdateAsync(_character.Id, new UpdateCharacterRequest { Name = _name.Text, Appearance = _appearance.Text, PersonaId = personaId ?? string.Empty }, _life.Token));
                _step = 1;
                ShowStep();
            });
        }
        else if (_step == 1)
        {
            if (_character == null) return;
            await Run(async () =>
            {
                CharacterTrainingPlan plan = await CharacterStudioClient.Current.PlanAsync(_character.Id, PresetKey(), _life.Token);
                CharacterDatasetFinding? block = plan.Findings.FirstOrDefault(f => f.Blocking && f.Code != "trainer_missing");
                if (block != null) throw new CharacterStudioApiException(block.Code == "too_few" ? "too_few_pictures" : block.Code, block.Message, block.NextAction);
                _step = 2;
                ShowStep();
            });
        }
        else if (_step == 2)
        {
            if (_character?.Binding != null) { _step = 3; ShowStep(); }
            else ShowError(new CharacterStudioApiException("not_trained", "Training has not finished yet.", "Press Start training and wait until it is done."));
        }
    }

    private async Task LoadPersonasAsync(string? preselect)
    {
        _persona.Items.Add(new ComboBoxItem { Content = T("None", "Nessuno"), Tag = null });
        try
        {
            PersonaListResponse all = await PersonaClient.Current.GetAllAsync(_life.Token);
            foreach (Persona p in all.Personas.Where(p => !p.IsBuiltIn))
            {
                _persona.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
                if (p.Id == preselect) { _persona.SelectedIndex = _persona.Items.Count - 1; if (_name.Text.Length == 0) _name.Text = p.Name; }
            }
        }
        catch (Exception) { /* personas are optional */ }
        if (_persona.SelectedIndex < 0) _persona.SelectedIndex = 0;
    }

    // ---- state ------------------------------------------------------------------------------------------------------------------------

    private void SetCharacter(CharacterProfile c)
    {
        _character = c;
        if (_name.Text.Length == 0) _name.Text = c.Name;
        if (_appearance.Text.Length == 0) _appearance.Text = c.Appearance;
        RenderCharacter();
    }

    private string PresetKey() => (_preset.SelectedItem as RadioButton)?.Tag as string ?? CharacterTrainingPreset.Balanced;

    private async Task PollAsync()
    {
        if (_polling || _character == null || _step is 0 or 3) return;
        _polling = true;
        try { SetCharacter(await CharacterStudioClient.Current.GetAsync(_character.Id, _life.Token)); }
        catch (Exception) { }
        finally { _polling = false; }
        if (_character?.Binding != null && _character.Training.Stage == CharacterStage.Completed && _step == 2) { _step = 3; ShowStep(); }
    }

    private void RenderCharacter()
    {
        if (_character == null) return;
        CharacterProfile c = _character;
        bool collecting = c.Training.Stage == CharacterStage.Collecting && c.Training.Message.StartsWith("Making", StringComparison.Ordinal);
        _busy.IsActive = collecting; _busy.Visibility = collecting ? Visibility.Visible : Visibility.Collapsed;
        _makePictures.IsEnabled = !collecting && c.Training.Stage != CharacterStage.Training;
        _addPhotos.IsEnabled = c.Training.Stage != CharacterStage.Training;
        _picturesInfo.Text = c.Images.Count + T(" pictures. ", " immagini. ") + T("15 to 25 give the best result; at least 8 are needed.", "Da 15 a 25 danno il risultato migliore; ne servono almeno 8.")
            + (collecting ? "  " + T("Making pictures, this takes a few minutes...", "Creo le immagini, ci vorranno alcuni minuti...") : string.Empty);
        if (c.Training.ErrorCode.Length > 0 && c.Training.Stage != CharacterStage.Training) ShowFriendly(c.Training.ErrorCode, c.Training.Message, c.Training.NextAction);
        _ = RenderGridAsync(c);
        RenderTraining(c);
        if (c.Binding != null)
            _doneText.Text = T("Done. ", "Fatto. ") + c.Name + T(" now has a consistent look. Trigger word: ", " ha ora un aspetto coerente. Parola di attivazione: ") + c.TriggerWord;
    }

    private async Task RenderGridAsync(CharacterProfile c)
    {
        string signature = string.Join(",", c.Images.Select(i => i.Id));
        if (!_shown.Add(signature)) return;
        _grid.Items.Clear();
        foreach (CharacterImage image in c.Images)
        {
            var img = new Image { Width = 104, Height = 104, Stretch = Stretch.UniformToFill };
            var remove = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.9 };
            ToolTipService.SetToolTip(remove, T("Remove this picture", "Rimuovi questa immagine"));
            AutomationProperties.SetName(remove, T("Remove this picture", "Rimuovi questa immagine"));
            string imageId = image.Id;
            remove.Click += async (_, _) => await Run(async () => SetCharacter(await CharacterStudioClient.Current.RemoveImageAsync(c.Id, imageId, _life.Token)));
            var cell = new Grid { Width = 104, Height = 104, Margin = new Thickness(2) };
            cell.Children.Add(img); cell.Children.Add(remove);
            ToolTipService.SetToolTip(cell, image.Label);
            _grid.Items.Add(cell);
            _ = LoadImageAsync(c.Id, "images", image.File, img);
        }
    }

    private async Task LoadImageAsync(string id, string kind, string file, Image target)
    {
        try
        {
            byte[]? bytes = await CharacterStudioClient.Current.GetFileAsync(id, kind, file, _life.Token);
            if (bytes == null) return;
            target.Source = await ToBitmapAsync(bytes);
        }
        catch (Exception) { }
    }

    private static async Task<BitmapImage> ToBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = 640 };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private async Task ShowPortraitAsync()
    {
        if (_character?.PortraitFile == null) return;
        await LoadImageAsync(_character.Id, "images", _character.PortraitFile, _portrait);
        _ = ChatPortrait.RefreshAsync();
    }

    // ---- actions ----------------------------------------------------------------------------------------------------------------------

    private async Task Run(Func<Task> action)
    {
        _error.Visibility = Visibility.Collapsed;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        if (ex is CharacterStudioApiException api) ShowFriendly(api.Code, api.Message, api.NextAction);
        else ShowFriendly("failed", ex.Message, string.Empty);
    }

    /// <summary>Plain-language messages with a next step. Known codes are translated; anything else shows the server text.</summary>
    private void ShowFriendly(string code, string serverMessage, string serverNext)
    {
        (string en, string it, string nEn, string nIt)? known = code switch
        {
            "out_of_vram" => ("The graphics card ran out of memory.", "La scheda grafica ha esaurito la memoria.", "Close other apps that use the GPU, choose Quick and try again.", "Chiudi le altre app che usano la GPU, scegli Veloce e riprova."),
            "trainer_missing" => ("The training engine is not set up yet.", "Il motore di addestramento non è ancora pronto.", "Press Install the training engine below.", "Premi Installa il motore di addestramento qui sotto."),
            "too_few_pictures" => ("There are too few pictures to learn a face.", "Ci sono troppe poche immagini per imparare un volto.", "Make more pictures or add your own photos (at least 8, ideally 15 to 25).", "Crea altre immagini o aggiungi le tue foto (almeno 8, meglio da 15 a 25)."),
            "gaming_active" => ("Gaming Mode is on, so AI work is paused.", "La modalità Gaming è attiva, quindi il lavoro AI è in pausa.", "Leave Gaming Mode, then continue; training resumes from the last saved step.", "Esci dalla modalità Gaming e continua; l'addestramento riparte dall'ultimo passo salvato."),
            "download_failed" => ("The base model could not be downloaded.", "Non è stato possibile scaricare il modello di base.", "Check the internet connection and press Resume.", "Controlla la connessione a Internet e premi Riprendi."),
            "disk_full" => ("The disk is full.", "Il disco è pieno.", "Free some space, then press Resume.", "Libera dello spazio, poi premi Riprendi."),
            "gpu_too_small" => ("This graphics card is too small for character training.", "Questa scheda grafica è troppo piccola per addestrare un personaggio.", "Character training needs an NVIDIA card with 12 GB or more.", "L'addestramento richiede una scheda NVIDIA con almeno 12 GB."),
            "trainer_unsupported" => ("Character training needs an NVIDIA graphics card.", "L'addestramento richiede una scheda grafica NVIDIA.", "You can still add pictures and use other tools.", "Puoi comunque aggiungere immagini e usare altri strumenti."),
            "image_failed" => ("The pictures could not be made.", "Non è stato possibile creare le immagini.", "Open Create once to check that the image model is installed, then try again.", "Apri Crea una volta per verificare che il modello di immagini sia installato, poi riprova."),
            "forbidden" => ("Only the desktop app can use Character Studio.", "Solo l'app desktop può usare Studio personaggi.", "Open this on the PC.", "Aprilo sul PC."),
            _ => null
        };
        string message = known != null ? T(known.Value.en, known.Value.it) : serverMessage;
        string next = known != null ? T(known.Value.nEn, known.Value.nIt) : serverNext;
        _error.Text = message + (next.Length > 0 ? "\n" + T("Next: ", "Ora: ") + next : string.Empty);
        _error.Visibility = Visibility.Visible;
    }

    private async Task MakePicturesAsync()
    {
        if (_character == null) return;
        await Run(async () =>
        {
            int make = Math.Max(1, Math.Min(20, 25 - _character.Images.Count));
            SetCharacter(await CharacterStudioClient.Current.GenerateReferencesAsync(_character.Id, make, _life.Token));
        });
    }

    private async Task AddPhotosAsync()
    {
        if (_character == null) return;
        await Run(async () =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
            if (files == null) return;
            foreach (Windows.Storage.StorageFile file in files)
                SetCharacter(await CharacterStudioClient.Current.AddImageAsync(_character.Id, file.Name, await File.ReadAllBytesAsync(file.Path, _life.Token), _life.Token));
        });
    }

    private async Task RefreshPlanAsync()
    {
        if (_character == null) return;
        try
        {
            CharacterTrainingPlan plan = await CharacterStudioClient.Current.PlanAsync(_character.Id, PresetKey(), _life.Token);
            CharacterTrainerStatus trainer = await CharacterStudioClient.Current.TrainerStatusAsync(_life.Token);
            _planText.Text = plan.Supported
                ? T("About ", "Circa ") + plan.EstimatedMinutes + T(" minutes on this PC. ", " minuti su questo PC. ") + (plan.Note.Length > 0 ? plan.Note : string.Empty)
                : plan.Note;
            RenderTrainer(trainer);
            _start.IsEnabled = plan.CanStart && _character.Training.Stage != CharacterStage.Training;
            foreach (CharacterDatasetFinding f in plan.Findings.Where(f => f.Blocking && f.Code != "trainer_missing"))
            { ShowFriendly(f.Code == "too_few" ? "too_few_pictures" : f.Code, f.Message, f.NextAction); break; }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderTrainer(CharacterTrainerStatus t)
    {
        _trainerButtons.Children.Clear();
        _trainerText.Text = t.State switch
        {
            "ready" => T("Training engine: ready.", "Motore di addestramento: pronto."),
            "broken" => T("The training engine is damaged.", "Il motore di addestramento è danneggiato."),
            "skipped" => T("You skipped the training engine. Install it to train.", "Hai saltato il motore di addestramento. Installalo per addestrare."),
            _ => T("The training engine is not installed. It is a one-time download of a few GB; PaRuX keeps it in its own folder.",
                "Il motore di addestramento non è installato. È un download una tantum di alcuni GB; PaRuX lo tiene nella sua cartella.")
        } + (t.Installing ? " " + T("Installing, please wait...", "Installazione in corso...") : string.Empty);
        if (t.State == "ready" || t.Installing) return;
        var install = new Button { Content = t.State == "broken" ? T("Repair the training engine", "Ripara il motore di addestramento") : T("Install the training engine", "Installa il motore di addestramento") };
        install.Click += async (_, _) => await Run(async () =>
        {
            install.IsEnabled = false;
            _trainerText.Text = T("Installing the training engine. This can take several minutes...", "Installazione del motore di addestramento. Può richiedere diversi minuti...");
            RenderTrainer(await CharacterStudioClient.Current.InstallTrainerAsync(_life.Token));
            await RefreshPlanAsync();
        });
        Name(install, "CharStudio_InstallTrainer");
        var locate = new Button { Content = T("I already have one…", "Ne ho già uno…") };
        locate.Click += async (_, _) => await Run(async () =>
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null) return;
            var pyPicker = new FileOpenPicker();
            pyPicker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(pyPicker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var py = await pyPicker.PickSingleFileAsync();
            if (py == null) return;
            RenderTrainer(await CharacterStudioClient.Current.LocateTrainerAsync(folder.Path, py.Path, _life.Token));
            await RefreshPlanAsync();
        });
        var skip = new Button { Content = T("Skip for now", "Salta per ora") };
        skip.Click += async (_, _) => await Run(async () => RenderTrainer(await CharacterStudioClient.Current.SkipTrainerAsync(_life.Token)));
        _trainerButtons.Children.Add(install); _trainerButtons.Children.Add(locate); _trainerButtons.Children.Add(skip);
    }

    private async Task StartTrainingAsync(bool resume)
    {
        if (_character == null) return;
        await Run(async () =>
        {
            CharacterTrainingAdvanced adv = new()
            {
                Rank = double.IsNaN(_rank.Value) ? null : (int)_rank.Value,
                Steps = double.IsNaN(_steps2.Value) ? null : (int)_steps2.Value,
                LearningRate = double.IsNaN(_lr.Value) ? null : _lr.Value,
                Resolution = double.IsNaN(_res.Value) ? null : (int)_res.Value
            };
            SetCharacter(resume
                ? await CharacterStudioClient.Current.ResumeTrainingAsync(_character.Id, _life.Token)
                : await CharacterStudioClient.Current.StartTrainingAsync(_character.Id, new StartCharacterTrainingRequest { Preset = PresetKey(), Advanced = adv }, _life.Token));
        });
    }

    private void RenderTraining(CharacterProfile c)
    {
        CharacterTrainingStatus t = c.Training;
        bool training = t.Stage == CharacterStage.Training;
        _cancel.Visibility = training ? Visibility.Visible : Visibility.Collapsed;
        _resume.Visibility = t.CanResume && !training ? Visibility.Visible : Visibility.Collapsed;
        if (training) _start.IsEnabled = false;
        _progress.Visibility = training ? Visibility.Visible : Visibility.Collapsed;
        if (training)
        {
            _progress.Value = t.TotalSteps > 0 ? 100.0 * t.Step / t.TotalSteps : 0;
            _progress.IsIndeterminate = t.Step == 0;
            string eta = t.EtaSeconds is int s ? T(" - about ", " - circa ") + Math.Max(1, (s + 59) / 60) + T(" min left", " min rimanenti") : string.Empty;
            _progressText.Text = t.Step == 0
                ? T("Getting ready (loading the model, the first start takes longer)...", "Preparazione (caricamento del modello, il primo avvio richiede più tempo)...")
                : T("Epoch ", "Epoca ") + t.Epoch + "/" + Math.Max(1, t.TotalEpochs) + T(", step ", ", passo ") + t.Step + "/" + t.TotalSteps + eta;
        }
        else
        {
            _progressText.Text = t.Stage is CharacterStage.Cancelled or CharacterStage.Interrupted or CharacterStage.Failed or CharacterStage.Completed ? t.Message : string.Empty;
        }
        _ = RenderSamplesAsync(c);
    }

    private async Task RenderSamplesAsync(CharacterProfile c)
    {
        foreach (string file in c.Training.Samples.Skip(Math.Max(0, c.Training.Samples.Count - 6)))
        {
            if (!_shown.Add("sample:" + file)) continue;
            var img = new Image { Width = 128, Height = 128, Stretch = Stretch.UniformToFill };
            ToolTipService.SetToolTip(img, T("Preview made during training", "Anteprima creata durante l'addestramento"));
            _samples.Children.Add(img);
            await LoadImageAsync(c.Id, "samples", file, img);
        }
    }

    private async Task TestPictureAsync()
    {
        if (_character == null) return;
        await Run(async () =>
        {
            _testPicture.IsEnabled = false;
            _testStatus.Text = T("Making a test picture...", "Creo un'immagine di prova...");
            try
            {
                var request = new MediaJobRequest
                {
                    Type = "image", Prompt = "portrait photo, smiling, soft daylight, in a cafe", Width = 1024, Height = 1024, CharacterId = _character.Id
                };
                MediaAsset asset = await MediaJobClient.Current.RunAsync(request, new Progress<MediaJobStatus>(s => _testStatus.Text = s.Stage), _life.Token);
                byte[] bytes = Convert.FromBase64String(asset.Base64Data);
                _testImage.Source = await ToBitmapAsync(bytes);
                _testStatus.Text = T("This picture used the trained look.", "Questa immagine ha usato l'aspetto addestrato.");
            }
            finally { _testPicture.IsEnabled = true; }
        });
    }
}

/// <summary>Caches the portrait of the character attached to the active chat persona so the chat avatar can show it.</summary>
internal static class ChatPortrait
{
    internal static ImageSource? Source { get; private set; }
    internal static event Action? Changed;

    internal static async Task RefreshAsync()
    {
        try
        {
            byte[]? bytes = await CharacterStudioClient.Current.GetActivePortraitAsync(CancellationToken.None);
            if (bytes == null) { Source = null; Changed?.Invoke(); return; }
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 96 };
            await bitmap.SetSourceAsync(stream);
            Source = bitmap;
            Changed?.Invoke();
        }
        catch (Exception) { Source = null; }
    }
}
