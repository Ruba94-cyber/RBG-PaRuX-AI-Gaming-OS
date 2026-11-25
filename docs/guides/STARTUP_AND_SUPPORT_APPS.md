# Start at sign-in, support apps and the pause list / Avvio all'accesso, app di supporto ed elenco pausa

Everything here is opt-in, off by default and reversible. / Tutto qui è facoltativo, spento di base e reversibile.

## English

### Start at sign-in (Settings > Gaming & AI Sharing > Start at sign-in)

- One switch adds ONE startup entry for your Windows user only (the current user's Run key, no administrator rights, no scheduled task). PaRuX shows what it will do and asks first. Turning the switch off puts the entry back exactly as it was; a different command already registered under PaRuX's name is never replaced silently.
- At sign-in PaRuX makes one decision:
  - **Console** when you asked for it (Settings > Gaming > Start in Console mode, or "start in TV mode when a chosen TV is on" and a chosen TV is connected) and, if you set the optional graphics-card rule, that card is present. Console opens Playnite Fullscreen when available, starts your support apps, and applies the optional power plan and Game Bar block you already turned on.
  - **Desktop** otherwise: the PaRuX window stays minimised and never takes the screen.
  - Ctrl at start opens the settings, Shift opens streaming. `--logon-force` skips the TV and card wait; `--no-start` keeps the desktop side.
  - A running game keeps everything off. No TV or monitor is ever required.
- **Preview what would start (dry run)** shows the exact decision and steps now, starting and changing nothing.
- **Start timings** are written to `StartupTiming.log` in PaRuXData: `date time +elapsed-ms phase`, measured with a monotonic stopwatch from the start of the sequence; the file keeps the newest 500 lines.

### Support apps

Helper programs you want running while Console is in use (for example a controller helper).

- You pick the program (**USER_MANAGED**): PaRuX only starts it (no shell), waits a bounded time, logs the result without paths, and, if you mark it, stops it when Console ends (only a process PaRuX started, matched by id and start time). It never installs, edits or updates your copy. Optional: replace an older running copy after you updated the program.
- **Known helpers** lists catalog entries. A download is offered only when the official https source, version, SHA-256 and size are pinned in PaRuX; you see them and confirm; PaRuX checks the SHA-256 before unpacking, keeps the files in its own folder (**PARUX_MANAGED**, no administrator rights) and can remove them again. Entries whose source cannot be pinned, or that need a driver or service, stay **Locate only**.
- Nothing starts while Gaming is on.

### Games never paused

Add game or program names the controller pause / resume button must never pause. PaRuX still pauses only a game it launched and never one with anti-cheat; a paused game can always be resumed.

## Italiano

### Avvio all'accesso (Impostazioni > Gaming e condivisione IA > Avvio all'accesso)

- Un interruttore aggiunge UNA sola voce di avvio solo per il tuo utente Windows (chiave Run dell'utente corrente, senza diritti di amministratore, senza attività pianificata). PaRuX mostra cosa farà e chiede prima. Spegnendo l'interruttore la voce torna esattamente com'era; un comando diverso già registrato con il nome di PaRuX non viene mai sostituito di nascosto.
- All'accesso PaRuX prende una sola decisione:
  - **Console** se l'hai chiesta (Impostazioni > Gaming > Avvia in modalità Console, oppure "avvia in modalità TV quando una TV scelta è accesa" e una TV scelta è collegata) e, se hai impostato la regola facoltativa della scheda grafica, quella scheda è presente. La Console apre Playnite Fullscreen se disponibile, avvia le tue app di supporto e applica il piano energetico e il blocco della Game Bar che hai già attivato.
  - **Desktop** negli altri casi: la finestra di PaRuX resta ridotta a icona e non prende mai lo schermo.
  - Ctrl all'avvio apre le impostazioni, Shift apre lo streaming. `--logon-force` salta l'attesa di TV e scheda; `--no-start` mantiene il lato desktop.
  - Un gioco in esecuzione tiene tutto spento. Non serve mai una TV o un monitor.
- **Anteprima di cosa partirebbe (prova a secco)** mostra subito la decisione e i passi esatti, senza avviare né cambiare nulla.
- **I tempi di avvio** sono scritti in `StartupTiming.log` dentro PaRuXData: `data ora +ms-trascorsi fase`, misurati con un cronometro monotono dall'inizio della sequenza; il file conserva le ultime 500 righe.

### App di supporto

Programmi di aiuto che vuoi attivi mentre la Console è in uso (per esempio un helper per il controller).

- Scegli tu il programma (**USER_MANAGED**): PaRuX lo avvia soltanto (senza shell), attende un tempo limitato, registra l'esito senza percorsi e, se lo indichi, lo chiude alla fine della Console (solo un processo avviato da PaRuX, riconosciuto da id e orario di avvio). Non installa, modifica né aggiorna mai la tua copia. Facoltativo: sostituire una copia più vecchia in esecuzione dopo che hai aggiornato il programma.
- **Helper noti** elenca le voci del catalogo. Il download è offerto solo quando fonte ufficiale https, versione, SHA-256 e dimensione sono fissati in PaRuX; li vedi e confermi; PaRuX controlla lo SHA-256 prima di estrarre, tiene i file nella propria cartella (**PARUX_MANAGED**, senza diritti di amministratore) e può rimuoverli. Le voci la cui fonte non è fissabile, o che richiedono un driver o un servizio, restano **solo da indicare**.
- Nulla parte mentre il Gaming è attivo.

### Giochi mai messi in pausa

Aggiungi i nomi dei giochi o dei programmi che il pulsante pausa / riprendi del controller non deve mai mettere in pausa. PaRuX mette comunque in pausa solo un gioco da lui avviato e mai uno con anti-cheat; un gioco in pausa si può sempre riprendere.
