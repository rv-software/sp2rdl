# SP to RDL Generator

Visual Studio ekstenzija za generisanje `.rdl` ili `.rdlc` izvjestaja iz SQL Server stored procedure ili iz sacuvanog JSON state fajla.

Generator je napravljen kao praktican alat za band-oriented nacin razmisljanja: generalne postavke izvjestaja, report parametri, body/tablix, memorandum, page header/footer i report summary se podesavaju kroz dijalog, a rezultat se upisuje kao RDL/RDLC i prateci `.sp2rdl.json`.

## Osnovni tok rada

1. Pokreni komandu `SP to RDL Generator` iz Visual Studio `Extensions` menija.
2. Na tabu `General` izaberi konekciju, stored proceduru, output mode, naslov izvjestaja, format stranice, orijentaciju i margine.
3. Klikni `Inspect` za ucitavanje parametara procedure i osnovnih kolona.
4. Ako SQL Server ne moze automatski opisati rezultat procedure, koristi `Suggest columns` ili rucno dodaj kolone na `Main dataset`.
5. Na `Report params` uredi parametre koje korisnik vidi u izvjestaju.
6. Na `Report variables` definisi interne placeholder varijable za memorandum, footer i summary.
7. Na `Body / Tablix` podesi stil glavne tabele, grupe, podzbirove i ukupno.
8. Na `Memorandum`, `Page header / Footer` i `Report Summary` podesi zaglavlja, potpise, logo i tekstualne sablone.
9. Na `Output` izaberi putanju fajla.
10. Klikni `Generate`.

Svako generisanje snima i JSON state pored izvjestaja. State se moze rucno sacuvati i ucitati kroz dugmad `Save state...` i `Load state...`.

## Konekcija

Dugme za konekciju otvara dijalog koji cita connection stringove iz solution konfiguracije kada ih moze pronaci, ali dozvoljava i rucni unos. Windows autentikacija se posebno pazi pri generisanju data source dijela, jer Report Builder/Power BI Report Builder razlikuju connection string i credential type.

Ako se koristi Integrated Security, generator iz connection stringa uklanja credentials i u RDL upisuje `IntegratedSecurity=true`, uz odgovarajuci designer security type.

## Stored procedure i kolone

Primarni nacin citanja kolona je SQL Server metadata opis rezultata procedure. Kod kompleksnih procedura, temporary tabela ili dinamickog SQL-a, SQL Server nekad ne moze vratiti kolone. Tada postoje dvije alternative:

- `Suggest columns` cita tekst procedure i pokusava pronaci posljednji `SELECT`.
- Kolone se mogu rucno dodati ili urediti u gridu.

Kolone imaju:

- `Show`: da li ide u detalj izvjestaja.
- `SqlTypeName`: SQL tip.
- `Format`: RDL format prikaza, npr. `dd.MM.yyyy`, `#,##0.00`.
- `Align`: rucno poravnanje, ako se zeli pregaziti automatsko.
- `Group`: nivo grupe od 1 do 4.
- `Aggregate`: agregacija za subtotal i grand total.

Automatsko poravnanje je:

- numericke kolone desno,
- tekstualne lijevo,
- datumi centralno.

Sirine kolona se racunaju automatski po tipu i ocekivanoj duzini. `Tablix width %` na `Body / Tablix` odredjuje ukupnu sirinu tablixa u odnosu na korisnu sirinu stranice, a kolone se onda rasporedjuju unutar te sirine.

## Report params

`Report params` su parametri koje korisnik vidi u report parameter panelu. Oni nisu obavezno isti kao parametri stored procedure. Ovo omogucava scenarije tipa:

- `RegionId` filtrira `MunicipalityId`,
- `MunicipalityId` filtrira `OrganisationId`,
- `OrganisationId` se tek onda vezuje na stvarni SP parametar.

Polje `Bind to SP param` povezuje report parametar sa stvarnim SQL parametrom procedure.

Lookup moze biti:

- SQL lookup dataset,
- staticka lista vrijednosti,
- rucno unesen SQL.

Staticka lista se unosi kroz `Static` dugme na tabu `Report params`, jedan par po redu:

```text
1 | OS
2 | SS
```

Lijevo od `|` je vrijednost, desno je label koji korisnik vidi.

`Suggest SQL` za lookup pokusava pronaci tabelu ciji primary key ima isti naziv kao parametar i zatim kao label kolone predlaze kolone koje u nazivu imaju `Name`, npr. `Name`, `SchoolName`, `OrganisationName`.

`Depends on` sluzi za cascading parametre. U padajucem izboru se nude samo parametri sa manjim ordinalom da se izbjegnu forward dependency problemi.

Default vrijednost moze biti SQL, npr:

```sql
SELECT GETDATE()
```

ili konstanta kroz SQL:

```sql
SELECT 1
```

## Report variables i placeholderi

Report variables su interne vrijednosti koje se koriste u template tekstovima. Primjeri:

- `{CompanyName}`
- `{CompanyAddress}`
- `{CompanyCity}`
- `{DirectorName}`

Varijabla moze imati:

- `Source column`: kolona koju vraca zajednicki SQL za varijable.
- `Static value`: fiksna vrijednost.
- `Fallback value`: vrijednost koja se koristi ako SQL ne vrati podatak.

Preporuceni model je jedan SQL koji vraca vise kolona:

```sql
SELECT
    Name AS CompanyName,
    Address AS CompanyAddress,
    City AS CompanyCity
FROM dbo.Company
WHERE CompanyId = 1
```

Dugme `Generate variables` iz tog SQL-a moze dodati varijable na osnovu vracenih kolona. Placeholder mora postojati u listi report variables prije upotrebe u templateu. Desni klik na template polje nudi dostupne placeholder-e.

## Memorandum

Memorandum je analogan report title/header bandu iz band-oriented alata. Prikazuje se samo na prvoj stranici.

Podrzava dva nacina:

- `Inline`: generator direktno crta memorandum u RDL.
- `Subreport`: glavni izvjestaj ukljucuje poseban RDL kao subreport.

Inline memorandum podrzava:

- logo kao embedded image,
- logo lijevo/centar/desno,
- tekst pored loga ili ispod loga,
- vertikalnu liniju izmedju loga i teksta,
- liniju ispod loga,
- liniju ispod memoranduma,
- HTML-like template sa placeholderima.

Template podrzava osnovne tagove:

- `<b>`, `<i>`, `<u>`
- `<p style="text-align:right;">...</p>`
- `<ul><li>...</li></ul>`
- `<ol><li>...</li></ol>`

Za napredne memorandume preporucen je subreport. Tada se isti memorandum moze koristiti u vise glavnih izvjestaja, a promjena layouta subreporta vazi za sve izvjestaje koji ga ukljucuju.

## Page header / Footer

Page header i footer su odvojeni od memoranduma.

Page header moze imati lijevi i desni segment, bottom line, visinu i opciju `Show on first page`. U segmentima se mogu koristiti placeholderi kao `{ReportTitle}` ili `{CompanyName}`.

Page footer moze imati lijevi i desni segment, mini logo, page number izraz i opciju `Show on last page`. Za tekst tipa:

```text
Strana {PageNo} od {PageCount}
```

koriste se placeholderi koje generator prevodi u RDL globalne vrijednosti.

## Report Summary

Report Summary je zaseban segment koji se prikazuje na kraju body-ja, analogno summary bandu iz band-oriented alata. Koristi se za potpise, pecat, zavrsne napomene i slicne elemente.

Moze raditi inline ili kao subreport. Inline mode podrzava 1 do 3 kolone. Svaka kolona ima:

- template,
- sirinu u procentima,
- vertikalno poravnanje,
- padding,
- opciju `Line above`.

Za tipican potpis moze se koristiti template:

```html
<p style="text-align:center;"><b>Direktor</b></p>
{Line}
<p style="text-align:center;">{DirectorName}</p>
```

`{Line}` u report summary templateu crta horizontalnu liniju u okviru kolone.

## Body / Tablix

Tab `Body / Tablix` kontrolise osnovni izgled glavnog tablixa:

- ukupna sirina tablixa u procentu,
- osnovna boja sjenčenja,
- boja i debljina bordera,
- font, velicina fonta i boja teksta.

Grupisanje se definise na kolonama kroz `Group` level 1-4. Ako vise kolona ima isti level, grupa je kompozitna. Group header spaja celije tako da vizuelno izgleda kao jedna siroka celija. Group footer prikazuje podzbirove. Grand total prikazuje `Ukupno`.

Agregacije po tipu:

- tekst: `Count`, `CountDistinct`
- datum: `Count`, `CountDistinct`, `Min`, `Max`
- broj: `Sum`, `Count`, `CountDistinct`, `Min`, `Max`, `Avg`

Pozadine grupa se izvode iz osnovne boje. Visi nivo grupe je svjetliji, a grand total je tamniji i boldovan.

## Subreporti

Memorandum i Report Summary mogu biti subreporti. Ako je subreport izabran, inline verzija tog segmenta se ne generise, osim ako je ukljucen fallback.

Generator pokusava procitati lokalni subreport RDL i proslijediti sve parametre glavnog izvjestaja koji postoje u subreportu. Time se izbjegava rucno mapiranje u najcescem slucaju.

Kod objave na Reporting Services treba obratiti paznju na server path subreporta. Lokalna putanja je korisna za dizajn, ali server mora znati gdje je subreport objavljen.

## Lokalizacija

Generator moze ubaciti hidden parametre `ReportId` i `LanguageId`, dodati skriveni `dsReportLabels` dataset, i sve staticke labele u RDL-u (naslov, header/footer tekstovi, nazivi kolona, group header-i, „Ukupno", „Podzbir"...) preusmjeriti na ekspresije koje citaju vrijednosti iz konfigurisane tabele prevoda.

Konfiguracija je na tabu **Localization** dijaloga.

### Sta generator očekuje od baze

Tool je generican. Imena schema/tabela ne nudi kao default — popunis ih sam kako se zovu u tvojoj bazi. Bitne su **kolone i njihovi tipovi**.

#### Tabela za registar reporta („Report table")

Koristi je dugme **Register report**. Generator radi `MERGE` po `InternalName` i nakon toga procita `ReportId`.

Minimalna struktura:

| Kolona | Tip | Napomena |
|---|---|---|
| `ReportId` | `int IDENTITY` | primarni kljuc, generise ga baza |
| `InternalName` | `nvarchar(...)` | mora biti UNIQUE; koristi se kao match key |
| `DisplayName` | `nvarchar(...)` | čita se iz „Report Title" sa tab-a Report |
| `ReportFileName` | `nvarchar(...)` | naziv .rdl fajla (npr. `MyReport.rdl`) |
| `Public` | `bit` | dugme uvijek upisuje `1` |

Primjer DDL-a (prilagodi schema/imena svojoj bazi):

```sql
CREATE TABLE BasicCatalogs.Report
(
    ReportId        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Report PRIMARY KEY,
    InternalName    nvarchar(200)     NOT NULL CONSTRAINT UQ_Report_InternalName UNIQUE,
    DisplayName     nvarchar(400)     NOT NULL,
    ReportFileName  nvarchar(400)     NOT NULL,
    [Public]        bit               NOT NULL CONSTRAINT DF_Report_Public DEFAULT (1)
);
```

Tabela moze imati i druge kolone — generator dira samo ove gore.

#### Tabela prevoda („Translation table")

Koristi je: (1) seed SQL fajl, (2) dugme **Register report** za default jezik, (3) runtime `dsReportLabels` u generisanom RDL-u.

Minimalna struktura:

| Kolona | Tip | Napomena |
|---|---|---|
| `ReportId` | `int` | FK na Report; `0` (ili `GeneralReportId`) za zajednicke prevode |
| `LanguageId` | `int` | id jezika |
| `Key` | `nvarchar(...)` | logicki kljuc labele (npr. `Column.UkupnoDana`) |
| `Value` | `nvarchar(...)` | tekst prevoda |
| `Deleted` | `bit` | runtime filter; svuda se trazi `Deleted = 0` |

Primjer:

```sql
CREATE TABLE BasicCatalogs.ReportTranslation
(
    ReportTranslationId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportTranslation PRIMARY KEY,
    ReportId            int           NOT NULL,
    LanguageId          int           NOT NULL,
    [Key]               nvarchar(200) NOT NULL,
    [Value]             nvarchar(max) NOT NULL,
    Deleted             bit           NOT NULL CONSTRAINT DF_ReportTranslation_Deleted DEFAULT (0),
    CONSTRAINT UQ_ReportTranslation UNIQUE (ReportId, LanguageId, [Key])
);
```

Generator runtime SQL prvo trazi prevod za konkretni `ReportId`, pa fallback na `GeneralReportId` (ako je postavljen), pa fallback na default tekst hardkodovan u ekspresiji.

#### Tabela jezika

Generator je **ne dira** direktno, ali je obicno potrebna ako parametar `LanguageId` ima dropdown listu. Ti je definises sam u svojoj bazi.

### Dugme „Register report"

Posto su konekcija i tabele postavljene:

1. Kliknes **Register report** na tab-u Localization.
2. Generator otvori SqlConnection, izvrsi:
   ```sql
   MERGE INTO {ReportTable} AS T
   USING (VALUES (@InternalName, @DisplayName, @ReportFileName, 1)) AS S(...)
      ON T.InternalName = S.InternalName
   WHEN MATCHED THEN UPDATE SET DisplayName = S.DisplayName, ReportFileName = S.ReportFileName, [Public] = S.[Public]
   WHEN NOT MATCHED THEN INSERT (...) VALUES (...);
   SELECT ReportId FROM {ReportTable} WHERE InternalName = @InternalName;
   ```
   `InternalName` je vrijednost polja **Report name** (nije ReportTitle), `DisplayName` je **Report title**, `ReportFileName` je naziv .rdl fajla iz **Output path**.
3. Vraceni `ReportId` se upise u polje **ReportId** na formi.
4. Ako je **Enable generated label localization** ukljuceno, generator zatim radi `MERGE` u tabelu prevoda za svaki staticki label u default jeziku.

### „Skip keys already translated under General ReportId"

Cekiraj kada imas centralni („opsti") report sa zajednickim prevodima i ne zelis duplikate u svakom konkretnom report-u. Ako pod `GeneralReportId` postoji prevod za isti `Key/LanguageId`, generator ga preskoci za trenutni `ReportId`. To se odrazava i u Register report dugmetu i u generisanom seed SQL fajlu.

### Seed SQL fajl

Ako je **Generate seed SQL next to report** ukljuceno, generator pored .rdl fajla snimi `*.localization.sql` sa `MERGE` blokovima za default jezik i sve dodatne template jezike. Pregledaj prevode prije nego sto ga pustis u produkciju.



State fajl ima ekstenziju `.sp2rdl.json` i sadrzi kompletan `ReportModel`:

- konekciju,
- output mode,
- page setup,
- datasetove,
- SQL i report parametre,
- report variables,
- memorandum,
- page header/footer,
- report summary,
- tablix stil.

Ovo je glavni format za nastavak rada. Ako se prekine rad, ucitaj state i nastavi bez ponovnog podesavanja.

## Build

Build:

```powershell
dotnet build .\sp2rdlGenExtension.slnx
```

Build pravi VSIX i nakon toga patchuje VSIX tako da se ubace Windows SQL Client runtime DLL-ovi (`System.Data.SqlClient.dll` i `sni.dll`). Ovo je vazno zbog greske da SQL Client nije podrzan unutar VSIX okruzenja.

## Dokumentacija u ekstenziji

Dokumentacija je dio VSIX instalacionog paketa. Glavni dijalog ima dugmad:

- `README`: ovaj pregled sistema.
- `Quick guide`: najbrzi onboarding za junior/medior report developere.

Svi dokumenti se nalaze i u repozitoriju:

- `README.md`
- `docs/INSTALLATION.md`
- `docs/REPORT_DEVELOPER_QUICK_GUIDE.md`
- `docs/TECHNICAL_DOCUMENTATION.md`

## Odrzavanje

Najvazniji fajlovi:

- `Dialogs/ReportSetupDialog.xaml`: glavni UI.
- `Dialogs/ReportSetupDialog.xaml.cs`: logika UI-ja, state mapping i akcije dugmadi.
- `Generation/RdlBuilder.cs`: generisanje RDL XML-a.
- `Services/SqlIntrospector.cs`: citanje SQL Server metadata.
- `Services/ReportModelFactory.cs`: inicijalni model iz stored procedure.
- `Persistence/SpRdlJsonStore.cs`: save/load JSON state.
- `docs/TECHNICAL_DOCUMENTATION.md`: detaljniji opis metoda i toka.
