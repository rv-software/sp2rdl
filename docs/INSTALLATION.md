# Installation Guide

Ovaj dokument opisuje kako instalirati, provjeriti i po potrebi rebuildovati SP to RDL Generator VSIX.

## Preduslovi

Na developerskoj masini treba imati:

- Visual Studio 2022 sa podrskom za ekstenzije.
- .NET SDK koji moze buildovati `net8.0-windows8.0`.
- SQL Server pristup bazi iz koje se citaju stored procedure.
- Power BI Report Builder ili Microsoft Report Builder za otvaranje i doradu `.rdl` fajlova.

## Instalacija iz gotovog VSIX fajla

1. Zatvori sve instance Visual Studija.
2. Pronadji fajl:

```text
bin\Debug\net8.0-windows8.0\sp2rdlGenExtension.vsix
```

3. Pokreni VSIX dvoklikom.
4. U VSIX installeru izaberi Visual Studio instancu u koju se instalira ekstenzija.
5. Klikni `Install`.
6. Nakon instalacije pokreni Visual Studio.
7. Otvori solution u kojem zelis kreirati report.
8. U meniju `Extensions` treba da postoji komanda `SP to RDL Generator`.

## Rebuild VSIX-a iz source koda

Iz root foldera repozitorija pokreni:

```powershell
dotnet build .\sp2rdlGenExtension.slnx
```

Build kreira:

```text
bin\Debug\net8.0-windows8.0\sp2rdlGenExtension.vsix
```

Tokom builda se dodatno patchuje VSIX paket da ukljuci SQL Client runtime fajlove:

- `System.Data.SqlClient.dll`
- `sni.dll`

Ovo je bitno jer bez tih fajlova Visual Studio ekstenzija moze prijaviti da SQL Client nije podrzan u runtime okruzenju.

## Da li treba zatvoriti EXP okruzenje

Ako testiras ekstenziju kroz Experimental Instance:

- Za cistu probu nakon rebuilda najbolje je zatvoriti EXP Visual Studio i pokrenuti ga ponovo.
- Ako je prethodna verzija ekstenzije vec ucitana u procesu, rebuild sam po sebi ne garantuje da ce nova DLL verzija biti ucitana.
- Ako se dijalog ponasa kao stara verzija, zatvori EXP okruzenje i pokreni novo.

## Provjera da dokumentacija ulazi u VSIX

VSIX treba da sadrzi:

- `README.md`
- `docs/INSTALLATION.md`
- `docs/REPORT_DEVELOPER_QUICK_GUIDE.md`
- `docs/TECHNICAL_DOCUMENTATION.md`

U instaliranoj ekstenziji ovi dokumenti su dostupni iz glavnog dijaloga kroz dugmad:

- `README`
- `Install`
- `Quick guide`

## Prva funkcionalna provjera

Nakon instalacije:

1. Otvori Visual Studio i solution.
2. Pokreni `Extensions > SP to RDL Generator`.
3. Klikni `README`, `Install` i `Quick guide` da provjeris da se dokumenti otvaraju.
4. Na `General` tabu podesi konekciju.
5. Ucitaj stored procedure.
6. Izaberi jednu jednostavnu proceduru.
7. Klikni `Inspect`.
8. Izaberi output putanju.
9. Klikni `Generate`.
10. Otvori generisani `.rdl` u Report Builderu.

## Najcesci problemi

### Komanda se ne vidi u Visual Studio meniju

- Provjeri da li je VSIX instaliran u pravu Visual Studio instancu.
- Restartuj Visual Studio.
- Provjeri `Extensions > Manage Extensions`.

### SQL Client runtime greska

- Uradi rebuild.
- Provjeri da build log sadrzi poruku `Replacing SQL Client runtime assemblies in VSIX.`
- Ponovo instaliraj generisani VSIX.

### README/Install/Quick guide se ne otvaraju

- Provjeri da dokumenti postoje u output folderu.
- Rebuilduj solution.
- Ako se koristi vec instaliran VSIX, ponovo instaliraj najnoviji VSIX.

### Dijalog ode iza Visual Studija

Dijalog koristi owner handle i aktivaciju. Ako se ipak desi, koristi `Alt+Tab` ili klik na taskbar. Ako se ponavlja, prvo zatvori sve stare EXP instance i probaj ponovo.

## Preporucena distribucija timu

Za timski rad najjednostavnije je:

1. Jedna osoba napravi provjeren build.
2. VSIX fajl se objavi na internu lokaciju.
3. Uz VSIX se navede verzija i datum.
4. Report developeri instaliraju isti VSIX.
5. Za svaki report se cuva i `.sp2rdl.json` state fajl u repozitoriju ili dogovorenom folderu.

