# SA-MP Account Manager

Aplicatie Windows (WPF, .NET 8) pentru conturi SA-MP multiple + client headless in Rust (fara GTA, fara grafica).
Server implicit: **B-Zone** `rpg.b-zone.ro:7777`.

## Structura
```
src/SampAccountManager.Models/    modele (cont, server, setari, stari, eveniment)
src/SampAccountManager.Core/      masina de stari a sesiunii + parser JSON Lines
src/SampAccountManager.Services/  stocare, DPAPI, backup, loguri, procese headless, sesiuni
src/SampAccountManager.WPF/       interfata (Dashboard, Accounts, Servers, Sessions, Settings, Logs)
src/SampAccountManager.Tests/     teste xUnit (regula "Online doar dupa spawn confirmat")
src/SampHeadlessClient/           client Rust, doar std (fara crate-uri externe)
data/ logs/ backups/              create langa aplicatie (sau in %LOCALAPPDATA%\SampAccountManager)
```

## Compilare
Local (Windows, cu .NET 8 SDK si Rust):
```
cd src\SampHeadlessClient && cargo test --release && cargo build --release
cd ..\.. 
dotnet publish src\SampAccountManager.WPF -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
`samp_headless_client.exe` este copiat automat langa `SampAccountManager.exe`.
Prin GitHub: `.github/workflows/build.yml` ruleaza testele, compileaza ambele componente si publica artefactul `SampAccountManager-win-x64`.

## Protocol WPF <-> Rust (JSON Lines)
Un proces Rust per sesiune, identificat prin `--session-id`. stdin: `{"cmd":"connect","host","port","nick","password"}`, `{"cmd":"disconnect"}`.
stdout, un eveniment pe linie: `{"sessionId","event","ts","data":{...}}`.
Evenimente: ClientStarted, Connecting, Connected, Joined, DialogShown, AuthenticationStarted, AuthenticationCompleted,
SpawnRequested, SpawnConfirmed, Online, PositionUpdate, ServerMessage, Log, ConnectionError, Disconnected.
Parola circula doar pe stdin si nu apare in niciun eveniment sau log (verificat in teste). Daca WPF se inchide, clientul vede EOF si iese.

## Cum se decide starea (fara simulari)
| Eveniment | Inseamna |
|---|---|
| Connected | serverul a acceptat handshake-ul RakNet |
| Joined | serverul a trimis RPC InitGame |
| AuthenticationStarted | am raspuns la un dialog cu camp de parola |
| AuthenticationCompleted | **dedus**: dupa raspuns, serverul a trimis SetSpawnInfo / TogglePlayerSpectating(0) |
| SpawnConfirmed | serverul a raspuns **explicit** la cererea noastra de spawn cu allow != 0 |
| Online | SpawnConfirmed + conexiune activa inca 5 s |

UI-ul accepta `Online` doar dupa `SpawnConfirmed` (testat). Pozitia X/Y/Z vine doar din pachetele serverului
(SetSpawnInfo / SetPlayerPos). Lumea virtuala nu este trimisa clientului de protocol -> afisata ca indisponibila.

## Limitari (importante)
1. **Netestat pe B-Zone.** Mediul de dezvoltare nu are acces UDP catre internet. Fluxul a fost verificat doar pe un server simulat scris de noi
   (2 teste de integrare) si pe unitati. Conectarea reala trebuie verificata de tine; eroarea exacta apare in log (ConnectionError cu cod).
2. **Prezenta permanenta pe harta nu este garantata.** Un client real trimite continuu pachete de sincronizare si raspunde la verificarile
   clientului impuse de server; unele servere deconecteaza clientii care nu o fac. Acest client **nu** falsifica GPCI, nu raspunde la
   verificari anti-cheat, nu emuleaza un client real si nu foloseste proxy. Campul GPCI din ClientJoin este trimis gol. Daca B-Zone
   respinge un client neoficial, vei vedea `ConnectionRejected`/deconectare in log; solutia compatibila este o autorizare explicita
   din partea administratorilor serverului pentru boti/clienti headless.
3. **Nu exista sincronizare de pozitie/miscare**: ceilalti jucatori pot vedea personajul doar daca serverul il pastreaza in lume fara pachete de sync.
4. Autentificarea functioneaza pentru servere care cer parola intr-un dialog (stil parola); alte fluxuri de login nu sunt acoperite.
5. Interfata WPF a fost scrisa fara a putea fi compilata local (nu exista .NET in mediul de dezvoltare); prima compilare se face in CI.
6. Parolele sunt protejate cu DPAPI (utilizator Windows curent): backup-ul restaurat pe alt utilizator nu isi poate decripta parolele.
7. Textele ServerMessage sunt afisate ca Latin-1 (diacriticele pot aparea gresit).
8. Regulamentul serverelor poate interzice conturi AFK/boti; foloseste doar cu acordul administratorilor.

## Credite
Transportul RakNet/SA-MP (cifru, strat de fiabilitate, tabel de autentificare) este adaptat din
[Newmcpe/RakClient](https://github.com/Newmcpe/RakClient) (WTFPL), fara modulul de emulare a clientului. Vezi `LICENSE-WTFPL-RakClient`.
