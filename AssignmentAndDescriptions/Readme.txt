================================================================================
MARS ROVER PATHFINDING PROJEKT
Vadász Dénes Informatika Verseny 2026
================================================================================

1) CSAPATADATOK

Csapat neve: RelativO
Csapattagok: Katona Gergő, Gál Péter, Tasi Dóra
Iskola: DSZC Mechwart András Gépipari és Informatikai Technikum
Felkészítő tanár: Hagymási Gyula Levente
Kapcsolati e-mail: geri.katona.mechwartsuli23@gmail.com


2) A PROJEKT LEÍRÁSA

Ez a projekt egy marsjáró-szimuláció és útvonaltervező/tanuló rendszer.
A rover egy 50x50-es térképen gyűjt ásványokat, közben figyeli az energiaállapotát,
és a küldetés végére vissza kell térnie a bázisra.

A program C# nyelven, .NET 8 platformon készült.
A fejlesztéshez használt fő szoftverek és technológiák:
- Visual Studio 2022
- C# / .NET 8
- Avalonia UI (grafikus felület)
- LiveCharts (grafikonok)
- LibVLCSharp (opcionális menüháttér videó)

A projekt fő részei:
- MarsRover.Core: szimulációs és algoritmikus logika (UI-független)
- MarsRover.Console: konzolos futtatás, tesztelés, logolás
- MarsRover.UI: Avalonia alapú grafikus felület


3) PROJEKTMAPPA FELÉPÍTÉSE (RÖVIDEN)

- MarsRover.sln
- MarsRover.Core/
- MarsRover.Console/
- MarsRover.UI/
- Map/ (példatérképek)
- Saved_Models/ (mentett modellek)
- results/ (futási naplók)


4) ELŐFELTÉTELEK

A futtatáshoz szükséges:
- .NET 8 SDK telepítve

Ellenőrzés:
  dotnet --version
A projekt gyökerében:
  dotnet restore
  dotnet build MarsRover.sln


6) FUTTATÁS – KONZOLOS ALKALMAZÁS

Alap futtatás:
  dotnet run --project MarsRover.Console

Példák paraméterezett futtatásra:
  dotnet run --project MarsRover.Console -- --map Map/mars_map_50x50.csv
  dotnet run --project MarsRover.Console -- --episodes 1000
  dotnet run --project MarsRover.Console -- --hours 24 --model q_table

Súgó:
  dotnet run --project MarsRover.Console -- --help

Mentett modell adatai:
  dotnet run --project MarsRover.Console -- --info


7) FUTTATÁS – GRAFIKUS FELÜLET (AVALONIA)

Indítás:
  dotnet run --project MarsRover.UI

Használati lépések:
1. Kattints a "LOAD MAP" gombra, és válassz térképet (.txt vagy .csv).
2. Állítsd be a küldetés időtartamát (Duration) és az epizódok számát.
3. Indítsd a tanítást a "TRAIN & RUN" gombbal.
4. A felső vezérlőkkel kezeld a szimulációt/visszajátszást:
   - PLAY
   - PAUSE
   - STEP
   - RESET
5. Nézetváltás:
   - MAP
   - REPLAY
   - CHART


8) TÉRKÉPFORMÁTUM

A térkép 50x50-es rács, ahol a jelölések például:
- .  szabad mező
- #  akadály
- B/Y/G  különböző ásványtípusok
- S  kezdő/bázis pozíció

(A tényleges parser a projektkódban definiált karaktereket kezeli.)


9) KIMENETEK, MENTÉSEK

A futás során/után létrejövő fontos fájlok:
- <model>.qtable.json   (tanult Q-tábla)
- <model>.meta.json     (modell metaadatok)
- results/run_*.txt     (futási napló)


10) RÖVID FELHASZNÁLÓI KÉZIKÖNYV

Tipikus munkamenet:
1. Térkép betöltése.
2. Tanítás elindítása a kívánt epizódszámmal.
3. Eredmények ellenőrzése MAP/REPLAY/CHART nézetekben.
4. Modell mentése és későbbi visszatöltése.
5. Több futás összehasonlítása a results mappa naplói alapján.

Hibakeresési tipp:
- Ha futtatási hiba van, először futtasd:
  dotnet restore
  dotnet build MarsRover.sln
