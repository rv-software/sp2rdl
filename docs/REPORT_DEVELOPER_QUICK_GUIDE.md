# Quick Guide for Report Developers

Ovo uputstvo je pisano za junior i medior report developere koji vec znaju osnove Reporting Services / Report Buildera, ali prvi put koriste SP to RDL Generator.

Cilj nije da se sve nauci odjednom. Cilj je da developer za 30-60 minuta moze napraviti prvi upotrebljiv RDL, sacuvati state i znati gdje se najcesce doradjuje rezultat.

## Kako razmisljati o generatoru

Generator nije zamjena za Report Builder. Generator pravi dobru pocetnu verziju reporta i cuva sva podesavanja u JSON state fajlu. Report Builder se i dalje koristi za finalnu vizuelnu doradu kada je to potrebno.

Najvaznije pravilo:

```text
RDL je rezultat, .sp2rdl.json je radni fajl generatora.
```

Ako treba nastaviti rad kasnije, ucitava se JSON state, a ne rekonstruise sve iz RDL-a.

## Mentalni model

Generator radi kroz nekoliko slojeva:

1. `General`: sta pravimo i iz koje baze.
2. `Main dataset`: da li glavni body dolazi iz stored procedure ili SQL text-a i koje kolone se prikazuju.
3. `Report params`: koje parametre korisnik vidi.
4. `Report variables`: interne vrijednosti za memorandum, footer i summary.
5. `Body / Tablix`: izgled glavne tabele, grupe, subtotali i total.
6. `Memorandum`: prvi blok na prvoj stranici.
7. `Page header / Footer`: elementi koji idu na stranice.
8. `Report Summary`: zavrsni blok, cesto potpisi i pecat.
9. `Output`: gdje se snima RDL/RDLC.

## Prvi report, najbrzi put

1. Pokreni `Extensions > SP to RDL Generator`.
2. Na `General` tabu klikni connection dugme i podesi konekciju.
3. Na `Main dataset` ostavi `Source = Stored procedure`.
4. Klikni `Refresh`.
5. Izaberi stored proceduru.
6. Klikni `Inspect`.
7. Provjeri `Main dataset`:
   - da li se vide parametri procedure,
   - da li se vide kolone.
8. Ako kolone nisu prepoznate, klikni `Suggest columns`.
9. Na `Output` izaberi gdje ide `.rdl`.
10. Klikni `Generate`.
11. Otvori `.rdl` u Report Builderu.

Ako se report otvori bez greske, prvi cilj je zavrsen.

## Kako urediti kolone

Na `Main dataset` tabu najcesce mijenjas:

- `Show`: iskljuci kolonu iz detalja.
- `Format`: npr. `dd.MM.yyyy`, `#,##0.00`.
- `Align`: ostavi prazno za automatski izbor ili rucno postavi `Left`, `Center`, `Right`.
- `Group`: stavi 1-4 ako kolona treba grupisanje.
- `Aggregate`: izaberi `Sum`, `Count`, `Min`, `Max`, `Avg` gdje ima smisla.

Tipicna pravila:

- ID kolone obicno ne prikazuj ako korisniku nisu bitne.
- Tekstualne kolone ostavi lijevo.
- Datume ostavi centralno.
- Iznose poravnaj desno.
- Za novac koristi format `#,##0.00`.
- Za cijele brojeve koristi `#,##0`.

## Grupisanje i subtotali

Ako zelis grupisati report:

1. Na koloni po kojoj grupises postavi `Group = 1`.
2. Ako imas podgrupu, na drugoj koloni postavi `Group = 2`.
3. U `Band oriented` layoutu kolone koje su grupe se ne prikazuju u detail redu, vec idu u group header.
4. Na numerickim kolonama izaberi `Sum`, `Avg`, `Min`, `Max` ili `Count`.
5. Na tekstualnim kolonama koristi uglavnom `Count` ili `CountDistinct`.

Ako ti treba Excel-like analitika gdje group kolone ostaju lijevo u tabeli, na `Body / Tablix` izaberi `Grouping layout = Tabular horizontal`. Tada se group vrijednosti prikazuju samo na prvom detail redu grupe, dok se subtotal i grand total redovi i dalje generisu za agregirane kolone.

Ako ti treba pravi crosstab/matrix, izaberi `Grouping layout = Matrix / Crosstab`. Na `Main dataset` kolonama oznaci redne grupe kao `Matrix role = RowGroup`, jednu kolonu koja se siri horizontalno kao `ColumnGroup`, i jednu numericku ili brojivu kolonu kao `Measure` sa agregacijom. Prva verzija podrzava vise row grupa, jednu column grupu i jednu measure kolonu, uz total po redovima, total po kolonama i grand total.

Na `Body / Tablix` podesi `Matrix cols` na ocekivani broj dinamickih kolona. Taj broj se koristi za autosize da matrix stane u sirinu reporta zajedno sa total kolonom.

Primjer:

```text
RegionName       Group 1
MunicipalityName Group 2
SchoolName       detail
Amount           Sum
```

Rezultat:

- header za region,
- header za opstinu,
- detail redovi,
- subtotal po opstini,
- subtotal po regionu,
- ukupno.

## Parametri

Razlikuj dvije stvari:

- SQL params: stvarni parametri stored procedure ili SQL text dataset-a.
- Report params: parametri koje korisnik vidi u reportu.

Jedan report parametar moze biti vezan na SQL parametar kroz `Bind to SP param`.

Primjer cascading parametara:

```text
RegionId        nije SP param, koristi se za filtriranje
MunicipalityId  zavisi od RegionId
OrganisationId  zavisi od MunicipalityId i binduje se na SP parametar @OrganisationId
```

Za `Depends on` biraj samo parametre koji su prije trenutnog parametra. Zato je `Ordinal` vazan.

Ako u Reporting bazi vec postoje ceste definicije parametara, npr. `RegionId`, `MunicipalityId` ili `OrganisationId`, prvo dodaj potrebne redove u `Report params`, zatim na `Output` tabu podesi `Reporting connection`, pa klikni `Load definitions` ili `Apply definitions`. Kolona `Definition` bira globalni `ParameterDefinition.Name`, a kolona `Name` ostaje aktivno ime parametra za ovaj report. Ako definicija jos ne postoji, popuni prvi red do kraja i klikni `Save definition`; zatim dodaj drugi red, izaberi istu definiciju i promijeni samo `Name` i `Prompt`, npr. `MunicipalityFromId` / `Od opstine` i `MunicipalityToId` / `Do opstine`.

Ako novi report treba gotovo iste runtime parametre kao neki raniji report, koristi `Load from report...`. Lista nudi samo trenutno aktivne verzije aktivnih reporta. Izaberi report verziju, oznaci parametre checkboxovima i po potrebi ukljuci `Update existing parameters` da se istoimeni redovi kompletno osvjeze. Bez tog checkboxa postojeci parametri ostaju netaknuti, a dodaju se samo nedostajuci.

Za runtime FE formu mozes popuniti i metadata polja:

- `Visible`: prikazuje ili skriva parametar u runtime formi.
- `Entity key`: entitet u obliku `module.entity`.
- `Value template`: vrijednost koja se salje bekendu; moze biti i kompozitni template.
- `Display template`: tekst koji korisnik vidi u lookupu.
- `Filter path`: posredno filtriranje, npr. kada izbor regije treba u pozadini filtrirati opstine prije prikaza skola.
- `Compare template`: polje ili template za poredenje dropdown parametara, npr. `{PostalCode}` kada treba porediti opstine po postanskom broju umjesto po ID vrijednosti.

Za runtime validaciju i runtime metadata klikni `Runtime settings...` na redu parametra. Dialog prikazuje samo stavke dozvoljene za izabrani `Control`, prema `config/report-validators.json`. Izabrane postavke i vrijednosti se cuvaju u state fajlu i u Reporting metadata SQL-u kao JSON u `UiParameter.RuntimeSettings`.

Za `minDate` i `maxDate` mozes unijeti `yyyy-MM-dd` ili izraze kao `today`, `today-7d`, `startOfMonth`, `endOfMonth`, `startOfYear`, `endOfYear+1d`.

## SQL text kao main dataset

Ako jos nemas stored proceduru ili pravis prototip:

1. Na `Main dataset` izaberi `Source = SQL text`.
2. Klikni `Edit SQL...`.
3. Unesi T-SQL koji vraca glavni rezultat.
4. Klikni `Inspect`.
5. Ako kolone nisu prepoznate, klikni `Suggest columns` ili ih dodaj rucno.

`Inspect` ne izvrsava SQL, nego trazi metadata kroz SQL Server. Lokalne varijable iza `DECLARE` ne postaju report parametri. Parametri koje SQL ocekuje pisi kao `@NazivParametra`.

Primjer:

```sql
DECLARE @Local int = 1;

SELECT
    O.OrganisationId,
    O.Name
FROM BasicCatalogs.Organisation AS O
WHERE O.MunicipalityId = @MunicipalityId;
```

U ovom primjeru `@MunicipalityId` ulazi u SQL params, a `@Local` ne ulazi.

## Lookup SQL

Lookup SQL treba da vrati najmanje dvije kolone:

```sql
SELECT OrganisationId, Name
FROM BasicCatalogs.Organisation
ORDER BY Name
```

Prva kolona je value, druga je label. Ako je parametar zavisan od drugog parametra:

```sql
SELECT O.OrganisationId, O.Name
FROM BasicCatalogs.Organisation O
WHERE O.MunicipalityId = @MunicipalityId
ORDER BY O.Name
```

Za multiselect parametar koji se koristi u SQL-u najcesce se mora koristiti `IN`, `STRING_SPLIT` ili nacin koji podrzava dataset/report engine. Generator vizuelno moze prikazati multiselect, ali SQL mora biti napisan tako da engine zna kako da ga primijeni.

## Static vrijednosti

Ako parametar ima mali fiksni izbor, ne mora se praviti tabela niti SQL lookup. Koristi `Static` kolonu na tabu `Report params`.

Static editor ne ocekuje SQL. Unosi se jedan par po redu, a `Value` i `Label` se razdvajaju pipe znakom `|`.

Primjer:

```text
1 | OS
2 | SS
3 | Fakultet
```

Lijeva strana je vrijednost koja se prosljedjuje reportu/SP-u. Desna strana je tekst koji korisnik vidi u parameter panelu. Ako ne upises label, koristi se ista vrijednost kao label.

Static values su korisne za status, tip, nivo, pol i slicne male liste.

## Output i Reporting metadata

Na `Output` tabu biras gdje ide `.rdl`, ali tu mozes pripremiti i runtime metadata SQL:

1. Podesi `Reporting connection` prema bazi koja sadrzi `Reporting` semu.
2. Klikni `Test` da provjeris konekciju.
3. Izaberi `Version valid from`; to je datum od kog verzija reporta vazi.
4. Izaberi `Migration folder` za Flyway skripte.
5. Klikni `Preview SQL` da pregledas MERGE/INSERT skriptu za trenutni report.
6. U dev bazi mozes kliknuti `Execute SQL` da odmah upises metadata.
7. Za QA, staging i produkciju klikni `Save SQL` i pusti skriptu kroz Flyway.

Ako baza jos nema osnovnu `Reporting` semu, `Preview schema` prikazuje bootstrap skriptu, a `Install schema` je moze izvrsiti samo dok sema ne postoji. Za svaku kasniju izmjenu seme koristi redovnu migraciju, ne ponovni install.

## Report variables

Report variables su placeholderi za tekstove koji se ponavljaju:

```text
{CompanyName}
{CompanyAddress}
{CompanyCity}
{DirectorName}
```

Najbolja praksa je jedan SQL koji vrati vise kolona:

```sql
SELECT
    Name AS CompanyName,
    Address AS CompanyAddress,
    DirectorName AS DirectorName
FROM dbo.Company
WHERE CompanyId = 1
```

Zatim klikni `Generate variables`. Generator ce dodati varijable po kolonama.

Ako SQL ne vrati vrijednost, koristi se `Fallback value`.

## Memorandum

Memorandum ide samo na prvu stranicu.

Za jednostavne memorandume koristi inline mode:

- logo,
- tekst,
- linije,
- osnovni HTML-like template.

Primjer templatea:

```html
<p style="text-align:right;"><b>{CompanyName}</b></p>
<p style="text-align:right;">Adresa: {CompanyAddress}</p>
<p style="text-align:right;">Grad: {CompanyCity}</p>
```

Za napredan memorandum koristi subreport. To je najbolji izbor kada:

- memorandum mora biti isti za mnogo reporta,
- layout je kompleksan,
- treba precizna kontrola u Report Builderu.

## Report Summary

Report Summary se koristi za zavrsne blokove, najcesce potpise.

Tipican primjer sa tri kolone:

```text
Lijevo:  Direktor
Sredina: M.P.
Desno:   Komercijalista
```

U template kolone mozes staviti:

```html
<p style="text-align:center;"><b>Direktor</b></p>
{Line}
<p style="text-align:center;">{DirectorName}</p>
```

`{Line}` crta liniju za potpis.

## Page header i footer

Page header i footer su za elemente koji se ponavljaju po stranicama.

Primjer page footer teksta:

```text
Strana {PageNo} od {PageCount}
```

Generator prevodi `{PageNo}` i `{PageCount}` u RDL globals.

## Lokalizacija (visejezicne labele)

Ako tvoj report mora biti dostupan na vise jezika, koristi tab **Localization**.

### Sta ti treba u bazi

Generator je generican: ti unosis **schema** i **naziv** dvije tabele koje vec postoje (ili ih kreiras po nizem template-u). Tool ne pretpostavlja imena.

**1. Tabela registra reporta** (npr. `BasicCatalogs.Report`):

```sql
CREATE TABLE BasicCatalogs.Report
(
    ReportId       int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InternalName   nvarchar(200)  NOT NULL UNIQUE,
    DisplayName    nvarchar(400)  NOT NULL,
    ReportFileName nvarchar(400)  NOT NULL,
    [Public]       bit            NOT NULL DEFAULT (1)
);
```

**2. Tabela prevoda** (npr. `BasicCatalogs.ReportTranslation`):

```sql
CREATE TABLE BasicCatalogs.ReportTranslation
(
    ReportTranslationId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ReportId   int           NOT NULL,
    LanguageId int           NOT NULL,
    [Key]      nvarchar(200) NOT NULL,
    [Value]    nvarchar(max) NOT NULL,
    Deleted    bit           NOT NULL DEFAULT (0),
    CONSTRAINT UQ_ReportTranslation UNIQUE (ReportId, LanguageId, [Key])
);
```

Tabele mogu imati i druge kolone (Audit, CreatedBy, itd.) — generator dira samo navedene.

### Tipican workflow

1. Cekiraj **Enable generated label localization**.
2. Unesi **Default LanguageId** (jezik koji se koristi kad nema prevoda).
3. Po potrebi **General ReportId** (zajednicki report za prevode kao sto su `Ukupno`, `Podzbir` ili nazivi kolona koji se ponavljaju u vise reporta).
4. Popuni **Translation table schema/name** i **Report table schema/name** prema tvojoj bazi.
5. Klikni **Register report**:
   - generator radi MERGE u Report tabeli po `InternalName` (= **Report name** sa tab-a Report),
   - vrati `ReportId` u polje na formi,
   - ako je localization ukljuceno, automatski prebaci default-language prevode kroz MERGE u ReportTranslation.
6. Cekiraj **Generate seed SQL** ako zelis i `.translations.sql` fajl pored .rdl/.rdlc-a (sa MERGE blokovima koje mozes pregledati prije produkcije).
7. **Skip keys already translated under General ReportId** — kad zelis da prevodi koji vec postoje pod opstim report-om ne dupliraju za konkretni report.

### Sta se desi u generisanom RDL-u

- Dodaju se hidden parametri `ReportId` (= dobijeni id), `LanguageId` (= default).
- Dodaje se skriveni `dsReportLabels` koji povlaci sve labele jednim SELECT-om.
- Podrzane staticke labele (naslov, kolone, group header labeli, `Ukupno`, `Podzbir`) se zamijene ekspresijom koja prvo trazi prevod za `ReportId`, pa fallback na `GeneralReportId`, pa fallback na hardkodovan default.

### Prepravka prevoda nakon generisanja

Idi direktno u tabelu prevoda (UPDATE [Value]). Nije potrebno regenerisati RDL.

## Save state

Klikni `Save state...` cesto, posebno prije vece izmjene.

State cuva:

- konekciju,
- procedure,
- kolone,
- parametre,
- variables,
- memorandum,
- page header/footer,
- report summary,
- body/tablix stil.

Ako nesto krene pogresno, ucitas prethodni JSON i nastavis.

## Kada koristiti subreport

Koristi subreport kada:

- memorandum ili summary treba dijeliti vise reporta,
- layout je previse kompleksan za inline template,
- vise ljudi odrzava isti memorandum,
- zelis da promjena jednog subreporta azurira sve glavne izvjestaje koji ga koriste.

Kod objave na Reporting Services obavezno provjeri server path subreporta.

## Najcesce greske i brzo rjesenje

### Ne vide se kolone

Razlog: SQL Server ne moze opisati result set procedure.

Rjesenje:

- klikni `Suggest columns`,
- ili rucno dodaj kolone.

### Parametar zavisi od parametra koji je poslije njega

Razlog: forward dependency.

Rjesenje:

- promijeni `Ordinal`,
- zavisni parametar mora biti ispod parametra od kojeg zavisi.

### Multiselect prikazuje ID umjesto naziva

Razlog: lookup label nije dobro definisan.

Rjesenje:

- lookup SQL neka vrati ID kao prvu kolonu i naziv kao drugu kolonu.

### Report Builder javlja invalid RDL

Najcesci razlozi:

- prazan `DataSets`,
- prazan `ReportParameters`,
- pogresan dependency parametara,
- subreport path nije dobar.

Prvo probaj generisati minimalan report, pa postepeno vracaj kompleksne opcije.

## Preporuceni trening za nove developere

### Vjezba 1: Jednostavan list report

- Jedna procedura.
- Bez grupa.
- 5-8 kolona.
- Jedan datum format.
- Jedan numericki format.
- Generate i otvaranje u Report Builderu.

### Vjezba 2: Parametri i lookup

- Jedan lookup parametar.
- Jedan default SQL.
- Jedan static parameter.
- Save/load state.

### Vjezba 3: Grupisanje

- Group level 1.
- Group level 2.
- Sum na numerickoj koloni.
- Provjera subtotal i ukupno.

### Vjezba 4: Memorandum i variables

- SQL za CompanyName, CompanyAddress.
- Generate variables.
- Template sa placeholderima.
- Preview.

### Vjezba 5: Report Summary

- Tri kolone.
- Potpis lijevo/desno.
- M.P. u sredini.
- `{Line}` za potpis.

Nakon ovih pet vjezbi developer moze samostalno napraviti vecinu standardnih RDL reporta i znace kada treba preci na subreport ili rucnu doradu u Report Builderu.
