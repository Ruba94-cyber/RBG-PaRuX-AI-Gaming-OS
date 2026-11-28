using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings > Privacy &amp; Network > "Remote access from anywhere". Off by default. An optional Cloudflare tunnel that lets a paired
    /// phone reach this PC away from home. It exposes ONLY the paired-device API (never the PaRuX app, settings, files or the browser
    /// door), a new phone must be approved here on the PC, and Gaming Mode or Corporate Lite switch it off.
    /// </summary>
    public sealed partial class SettingsView
    {
        private const string TunnelIdPrefix = "Settings_RemoteTunnel";
        private ToggleSwitch? _tunnelToggle;
        private TextBlock? _tunnelState, _tunnelDetail, _tunnelComponent, _tunnelListenerHint;
        private Button? _tunnelAction;
        private StackPanel? _tunnelPendingList;
        private TextBox? _tunnelLocatePath, _tunnelNamedUrl;
        private PasswordBox? _tunnelToken;
        private bool _tunnelLoading;
        private bool _tunnelOnline;
        private string _tunnelNextAction = RemoteTunnelActions.None;
        private DispatcherTimer? _tunnelTimer;

        private StackPanel BuildRemoteTunnelSection()
        {
            StackPanel section = OwnerSection("Privacy & Network", T("REMOTE ACCESS FROM ANYWHERE", "ACCESSO REMOTO DA OVUNQUE"),
                T("Off by default. Lets your paired phone reach this PC when you are away from home, through a free Cloudflare quick tunnel (no account needed). "
                  + "What is exposed: only the paired-device API that the PaRuX phone app uses, protected by each phone's own token. The PaRuX app, settings, files, API keys and the browser sign-in are never reachable this way. "
                  + "A new phone must be approved here on this PC. Turning it off, starting Gaming Mode, or closing PaRuX closes the tunnel. Traffic passes through Cloudflare's network (HTTPS).",
                  "Disattivato di default. Permette al telefono associato di raggiungere questo PC quando sei fuori casa, tramite un tunnel rapido Cloudflare gratuito (nessun account necessario). "
                  + "Cosa viene esposto: solo l'API dei dispositivi associati usata dall'app PaRuX sul telefono, protetta dal token di ciascun telefono. L'app PaRuX, le impostazioni, i file, le chiavi API e l'accesso dal browser non sono mai raggiungibili in questo modo. "
                  + "Un nuovo telefono va approvato qui su questo PC. Spegnendolo, avviando la modalità Gaming o chiudendo PaRuX il tunnel si chiude. Il traffico passa attraverso la rete di Cloudflare (HTTPS)."),
                out _tunnelPendingList, out _tunnelDetail, TunnelIdPrefix,
                (T("Refresh", "Aggiorna"), () => _ = RefreshTunnelAsync()));
            var card = (StackPanel)((Border)section.Children[1]).Child;

            _tunnelToggle = new ToggleSwitch
            {
                Header = T("Reach this PC from anywhere (Internet tunnel)", "Raggiungi questo PC da ovunque (tunnel Internet)"),
                OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento"),
            };
            AutomationProperties.SetAutomationId(_tunnelToggle, TunnelIdPrefix + "_Enabled_Toggle");
            _tunnelToggle.Toggled += async (_, _) => { if (!_tunnelLoading) await ToggleTunnelAsync(_tunnelToggle.IsOn); };
            card.Children.Insert(1, _tunnelToggle);

            _tunnelState = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true };
            AutomationProperties.SetAutomationId(_tunnelState, TunnelIdPrefix + "_State_Text");
            card.Children.Insert(2, _tunnelState);

            _tunnelAction = new Button { Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_tunnelAction, TunnelIdPrefix + "_NextAction_Button");
            _tunnelAction.Click += async (_, _) => await RunTunnelNextActionAsync();
            card.Children.Insert(3, _tunnelAction);

            _tunnelComponent = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(_tunnelComponent, TunnelIdPrefix + "_Component_Text");
            card.Children.Add(_tunnelComponent);

            var componentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var install = new Button { Content = T("Install with PaRuX", "Installa con PaRuX") };
            AutomationProperties.SetAutomationId(install, TunnelIdPrefix + "_Install_Button");
            install.Click += async (_, _) => await InstallTunnelComponentAsync();
            var skip = new Button { Content = T("Skip", "Salta") };
            AutomationProperties.SetAutomationId(skip, TunnelIdPrefix + "_Skip_Button");
            skip.Click += async (_, _) => await TunnelCallAsync(() => RemoteAccessClient.Current.SkipTunnelComponentAsync(CancellationToken.None));
            componentRow.Children.Add(install);
            componentRow.Children.Add(skip);
            card.Children.Add(componentRow);

            var locateRow = new Grid { ColumnSpacing = 8 };
            locateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            locateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _tunnelLocatePath = new TextBox
            {
                Header = T("Or use a cloudflared you already have (PaRuX will never change it)", "Oppure usa un cloudflared che hai già (PaRuX non lo modificherà mai)"),
                PlaceholderText = @"C:\Tools\cloudflared.exe",
            };
            AutomationProperties.SetAutomationId(_tunnelLocatePath, TunnelIdPrefix + "_Locate_TextBox");
            var locate = new Button { Content = T("Use this file", "Usa questo file"), VerticalAlignment = VerticalAlignment.Bottom };
            AutomationProperties.SetAutomationId(locate, TunnelIdPrefix + "_Locate_Button");
            locate.Click += async (_, _) =>
            {
                string path = _tunnelLocatePath.Text.Trim().Trim('"');
                if (path.Length > 0) await TunnelCallAsync(() => RemoteAccessClient.Current.LocateTunnelComponentAsync(path, CancellationToken.None));
            };
            Grid.SetColumn(locate, 1);
            locateRow.Children.Add(_tunnelLocatePath);
            locateRow.Children.Add(locate);
            card.Children.Add(locateRow);

            card.Children.Add(new TextBlock { Text = T("Waiting for approval on this PC", "In attesa di approvazione su questo PC"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            card.Children.Add(_tunnelPendingList!);

            var advanced = new Expander
            {
                Header = T("Advanced: use my own Cloudflare named tunnel (optional)", "Avanzate: usa il mio tunnel con nome Cloudflare (facoltativo)"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var advancedBody = new StackPanel { Spacing = 8 };
            advancedBody.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                Text = T("If you already have a Cloudflare tunnel, paste its token and the https address it serves. The token is stored encrypted for your Windows account and is never shown again or written to logs. Leave both empty to use the free quick tunnel.",
                         "Se hai già un tunnel Cloudflare, incolla il suo token e l'indirizzo https che serve. Il token è salvato cifrato per il tuo account Windows e non viene più mostrato né scritto nei log. Lascia vuoti entrambi per usare il tunnel rapido gratuito."),
            });
            _tunnelToken = new PasswordBox { Header = T("Tunnel token", "Token del tunnel"), PlaceholderText = T("Paste to set or replace", "Incolla per impostare o sostituire") };
            AutomationProperties.SetAutomationId(_tunnelToken, TunnelIdPrefix + "_Token_PasswordBox");
            _tunnelNamedUrl = new TextBox { Header = T("Public address", "Indirizzo pubblico"), PlaceholderText = "https://parux.example.com" };
            AutomationProperties.SetAutomationId(_tunnelNamedUrl, TunnelIdPrefix + "_NamedUrl_TextBox");
            _tunnelListenerHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            var saveNamed = new Button { Content = T("Save named tunnel", "Salva tunnel con nome") };
            AutomationProperties.SetAutomationId(saveNamed, TunnelIdPrefix + "_SaveNamed_Button");
            saveNamed.Click += async (_, _) =>
            {
                var request = new RemoteTunnelConfigRequest { NamedPublicUrl = _tunnelNamedUrl.Text.Trim() };
                if (_tunnelToken.Password.Length > 0) request.NamedToken = _tunnelToken.Password;
                _tunnelToken.Password = string.Empty;
                await TunnelCallAsync(() => RemoteAccessClient.Current.ConfigureTunnelAsync(request, CancellationToken.None));
            };
            var removeToken = new Button { Content = T("Remove saved token", "Rimuovi il token salvato") };
            AutomationProperties.SetAutomationId(removeToken, TunnelIdPrefix + "_RemoveToken_Button");
            removeToken.Click += async (_, _) => await TunnelCallAsync(() => RemoteAccessClient.Current.ConfigureTunnelAsync(
                new RemoteTunnelConfigRequest { NamedToken = string.Empty, NamedPublicUrl = string.Empty }, CancellationToken.None));
            advancedBody.Children.Add(_tunnelToken);
            advancedBody.Children.Add(_tunnelNamedUrl);
            advancedBody.Children.Add(_tunnelListenerHint);
            advancedBody.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { saveNamed, removeToken } });
            advanced.Content = advancedBody;
            card.Children.Add(advanced);

            _tunnelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _tunnelTimer.Tick += (_, _) => { if (section.Visibility == Visibility.Visible && section.IsLoaded) _ = RefreshTunnelAsync(); };
            section.Loaded += (_, _) => { _tunnelTimer.Start(); _ = RefreshTunnelAsync(); };
            section.Unloaded += (_, _) => _tunnelTimer.Stop();
            return section;
        }

        private async Task ToggleTunnelAsync(bool on)
        {
            if (!on)
            {
                await TunnelCallAsync(() => RemoteAccessClient.Current.ConfigureTunnelAsync(new RemoteTunnelConfigRequest { Enabled = false }, CancellationToken.None));
                return;
            }
            // Explicit consent every time it is switched on for the first time, saying exactly what is exposed.
            RemoteTunnelStatus? current = null;
            try { current = await RemoteAccessClient.Current.GetTunnelStatusAsync(CancellationToken.None); } catch { }
            if (current is null || !current.ConsentGiven)
            {
                var dialog = new ContentDialog
                {
                    Title = T("Turn on remote access from anywhere?", "Attivare l'accesso remoto da ovunque?"),
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = T("PaRuX will open a Cloudflare tunnel to this PC.\n\nExposed: only the paired-device API used by the PaRuX phone app. Each phone needs its own token, and a new phone must be approved on this PC.\n\nNOT exposed: the PaRuX app, settings, files, API keys, the browser sign-in and every owner-only action.\n\nYour requests travel through Cloudflare's network over HTTPS; the AI still runs only on this PC. Anyone who learns the address can reach the sign-in checks, but cannot use PaRuX without a paired token. The tunnel closes when you turn this off, start Gaming Mode or quit PaRuX.",
                                 "PaRuX aprirà un tunnel Cloudflare verso questo PC.\n\nEsposto: solo l'API dei dispositivi associati usata dall'app PaRuX sul telefono. Ogni telefono richiede il proprio token e un nuovo telefono va approvato su questo PC.\n\nNON esposto: l'app PaRuX, le impostazioni, i file, le chiavi API, l'accesso dal browser e ogni azione riservata al proprietario.\n\nLe richieste passano dalla rete di Cloudflare via HTTPS; l'IA gira comunque solo su questo PC. Chi conosce l'indirizzo può raggiungere i controlli di accesso, ma non può usare PaRuX senza un token associato. Il tunnel si chiude quando lo spegni, avvii la modalità Gaming o chiudi PaRuX."),
                    },
                    PrimaryButtonText = T("I understand, turn it on", "Ho capito, attiva"),
                    CloseButtonText = T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    _tunnelLoading = true; _tunnelToggle!.IsOn = false; _tunnelLoading = false;
                    return;
                }
                await TunnelCallAsync(() => RemoteAccessClient.Current.ConfigureTunnelAsync(
                    new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None));
                return;
            }
            await TunnelCallAsync(() => RemoteAccessClient.Current.ConfigureTunnelAsync(new RemoteTunnelConfigRequest { Enabled = true }, CancellationToken.None));
        }

        private async Task InstallTunnelComponentAsync()
        {
            _tunnelDetail!.Text = T("Downloading cloudflared from the official Cloudflare release and checking its SHA-256…", "Scarico cloudflared dalla release ufficiale di Cloudflare e verifico lo SHA-256…");
            await TunnelCallAsync(() => RemoteAccessClient.Current.InstallTunnelComponentAsync(CancellationToken.None));
        }

        private async Task RunTunnelNextActionAsync()
        {
            switch (_tunnelNextAction)
            {
                case RemoteTunnelActions.InstallComponent: await InstallTunnelComponentAsync(); break;
                case RemoteTunnelActions.Consent:
                case RemoteTunnelActions.Retry:
                case RemoteTunnelActions.CheckNetwork:
                    _tunnelLoading = true; _tunnelToggle!.IsOn = true; _tunnelLoading = false;
                    await ToggleTunnelAsync(true);
                    break;
                case RemoteTunnelActions.CheckToken:
                    _tunnelToken?.Focus(FocusState.Programmatic);
                    break;
            }
        }

        private async Task TunnelCallAsync(Func<Task<RemoteTunnelStatus>> call)
        {
            try { ApplyTunnelStatus(await call()); }
            catch (Exception ex)
            {
                Log.Write("Remote tunnel request failed: " + ex.Message);
                if (_tunnelDetail != null) _tunnelDetail.Text = T("That did not work: ", "Non ha funzionato: ") + ex.Message;
                await RefreshTunnelAsync();
            }
        }

        private async Task RefreshTunnelAsync()
        {
            if (_tunnelState == null) return;
            try { ApplyTunnelStatus(await RemoteAccessClient.Current.GetTunnelStatusAsync(CancellationToken.None)); }
            catch (Exception ex) { _tunnelState.Text = T("Tunnel status unavailable: ", "Stato del tunnel non disponibile: ") + ex.Message; }
        }

        private void ApplyTunnelStatus(RemoteTunnelStatus s)
        {
            if (_tunnelState == null) return;
            _tunnelLoading = true;
            _tunnelToggle!.IsOn = s.Enabled && s.State != RemoteTunnelState.Off || s.State is RemoteTunnelState.Starting or RemoteTunnelState.Online;
            _tunnelToggle.IsEnabled = s.Allowed;
            _tunnelLoading = false;
            _tunnelOnline = s.State == RemoteTunnelState.Online;
            if (PairDeviceButton != null && _tunnelOnline) PairDeviceButton.IsEnabled = true;

            _tunnelState.Text = s.State switch
            {
                RemoteTunnelState.Online => T("Online · ", "Online · ") + s.PublicUrl,
                RemoteTunnelState.Starting => T("Starting… (attempt ", "Avvio… (tentativo ") + Math.Max(1, s.Attempt) + T(" of ", " di ") + s.MaxAttempts + ")",
                RemoteTunnelState.Error => T("Problem: ", "Problema: ") + TunnelReasonText(s),
                _ => s.ReasonCode is RemoteTunnelReasons.None ? T("Off", "Spento") : T("Off · ", "Spento · ") + TunnelReasonText(s),
            };
            _tunnelDetail!.Text = s.State == RemoteTunnelState.Online
                ? T("Create a pairing code below: the QR now includes this address, so your phone can connect from anywhere. The address changes every time the tunnel restarts (quick tunnels), so pair again from a fresh code after a restart.",
                    "Crea un codice di associazione qui sotto: il QR ora include questo indirizzo, così il telefono può collegarsi da ovunque. L'indirizzo cambia a ogni riavvio del tunnel (tunnel rapidi), quindi dopo un riavvio associa di nuovo con un codice nuovo.")
                : string.Empty;

            _tunnelNextAction = s.NextAction;
            string? actionLabel = s.NextAction switch
            {
                RemoteTunnelActions.InstallComponent => T("Install cloudflared with PaRuX", "Installa cloudflared con PaRuX"),
                RemoteTunnelActions.Consent => T("Review and turn on", "Leggi e attiva"),
                RemoteTunnelActions.Retry => T("Try again", "Riprova"),
                RemoteTunnelActions.CheckNetwork => T("Try again", "Riprova"),
                RemoteTunnelActions.CheckToken => T("Check the token", "Controlla il token"),
                _ => null,
            };
            _tunnelAction!.Content = actionLabel;
            _tunnelAction.Visibility = actionLabel != null && s.State != RemoteTunnelState.Starting ? Visibility.Visible : Visibility.Collapsed;

            _tunnelComponent!.Text = T("cloudflared: ", "cloudflared: ")
                + (s.ComponentDetected
                    ? (s.ComponentOwnership == "USER_MANAGED"
                        ? T("your own copy (", "la tua copia (") + s.ComponentPath + T("), PaRuX never changes it", "), PaRuX non la modifica mai")
                        : T("installed and managed by PaRuX, version ", "installato e gestito da PaRuX, versione ") + s.ComponentVersion + T(" (SHA-256 checked before every start)", " (SHA-256 verificato a ogni avvio)"))
                    : s.ComponentSkipped
                        ? T("skipped. Install it or point to your own copy to use the tunnel.", "saltato. Installalo o indica la tua copia per usare il tunnel.")
                        : T("not installed. PaRuX can download version ", "non installato. PaRuX può scaricare la versione ") + s.PinnedVersion + T(" from the official Cloudflare release.", " dalla release ufficiale di Cloudflare."));

            if (_tunnelNamedUrl != null && !_tunnelNamedUrl.FocusState.HasFlag(FocusState.Keyboard) && _tunnelNamedUrl.FocusState == FocusState.Unfocused)
                _tunnelNamedUrl.Text = s.NamedPublicUrl;
            if (_tunnelListenerHint != null)
                _tunnelListenerHint.Text = s.HasNamedToken
                    ? T("A token is saved (hidden). In the Cloudflare dashboard, point the tunnel's public hostname to http://127.0.0.1:", "Un token è salvato (nascosto). Nel pannello Cloudflare, punta l'hostname pubblico del tunnel a http://127.0.0.1:") + s.ListenerPort
                    : T("No token saved: the free quick tunnel is used.", "Nessun token salvato: si usa il tunnel rapido gratuito.");

            _tunnelPendingList!.Children.Clear();
            if (s.PendingPairings.Count == 0)
            {
                _tunnelPendingList.Children.Add(new TextBlock { Text = T("Nobody is waiting.", "Nessuno è in attesa."), Opacity = 0.7 });
            }
            foreach (RemoteTunnelPendingPairing pending in s.PendingPairings)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = pending.DeviceName + T(" wants to pair through the Internet", " vuole associarsi tramite Internet"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                string id = pending.Id;
                var approve = new Button { Content = T("Approve", "Approva") };
                AutomationProperties.SetAutomationId(approve, TunnelIdPrefix + "_Approve_Button");
                approve.Click += async (_, _) => { await DecideTunnelAsync(id, true); };
                var deny = new Button { Content = T("Deny", "Rifiuta") };
                AutomationProperties.SetAutomationId(deny, TunnelIdPrefix + "_Deny_Button");
                deny.Click += async (_, _) => { await DecideTunnelAsync(id, false); };
                row.Children.Add(approve);
                row.Children.Add(deny);
                _tunnelPendingList.Children.Add(row);
            }
        }

        private async Task DecideTunnelAsync(string id, bool approve)
        {
            try { await RemoteAccessClient.Current.DecideTunnelPairingAsync(id, approve, CancellationToken.None); }
            catch (Exception ex) { Log.Write("Tunnel pairing decision failed: " + ex.Message); }
            await RefreshTunnelAsync();
            await RefreshRemoteAccessAsync();
        }

        /// <summary>Friendly, localized text for a status code. The server's English message is the fallback.</summary>
        private static string TunnelReasonText(RemoteTunnelStatus s) => s.ReasonCode switch
        {
            RemoteTunnelReasons.ConsentRequired => T("Read what the tunnel exposes and accept it before turning it on.", "Leggi cosa espone il tunnel e accettalo prima di attivarlo."),
            RemoteTunnelReasons.CorporateBlocked => T("Not available in PaRuX Corporate Lite.", "Non disponibile in PaRuX Corporate Lite."),
            RemoteTunnelReasons.GamingActive => T("Gaming Mode is on, so the tunnel is off. Turn it on again after gaming.", "La modalità Gaming è attiva, quindi il tunnel è spento. Riattivalo dopo aver giocato."),
            RemoteTunnelReasons.ComponentMissing => T("cloudflared is needed. Install it with PaRuX or point to your own copy.", "serve cloudflared. Installalo con PaRuX o indica la tua copia."),
            RemoteTunnelReasons.ComponentSkipped => T("cloudflared was skipped. Install it or point to your own copy.", "cloudflared è stato saltato. Installalo o indica la tua copia."),
            RemoteTunnelReasons.InstallFailed => T("PaRuX could not download or verify cloudflared. Check your connection and try again.", "PaRuX non è riuscito a scaricare o verificare cloudflared. Controlla la connessione e riprova."),
            RemoteTunnelReasons.ListenerUnavailable => T("PaRuX could not open its private tunnel port. Restart PaRuX and try again.", "PaRuX non è riuscito ad aprire la porta privata del tunnel. Riavvia PaRuX e riprova."),
            RemoteTunnelReasons.StartFailed => T("Windows would not start cloudflared. Security software may be blocking it.", "Windows non ha avviato cloudflared. Potrebbe essere bloccato da un software di sicurezza."),
            RemoteTunnelReasons.UrlTimeout => T("Cloudflare did not give an address in time. Check your Internet connection and try again.", "Cloudflare non ha fornito un indirizzo in tempo. Controlla la connessione a Internet e riprova."),
            RemoteTunnelReasons.ProcessExited => T("The tunnel connection dropped and could not be restored. Check your Internet connection.", "La connessione del tunnel si è interrotta e non è stato possibile ripristinarla. Controlla la connessione a Internet."),
            RemoteTunnelReasons.NetworkUnreachable => T("PaRuX could not reach Cloudflare. A firewall or VPN may be blocking it.", "PaRuX non ha raggiunto Cloudflare. Un firewall o una VPN potrebbe bloccarlo."),
            RemoteTunnelReasons.TokenRejected => T("Cloudflare rejected the tunnel token. Check it in your Cloudflare dashboard and enter it again.", "Cloudflare ha rifiutato il token del tunnel. Controllalo nel pannello Cloudflare e inseriscilo di nuovo."),
            RemoteTunnelReasons.NamedUrlMissing => T("Enter the https address your named tunnel serves.", "Inserisci l'indirizzo https servito dal tuo tunnel con nome."),
            _ => s.Message,
        };
    }
}
