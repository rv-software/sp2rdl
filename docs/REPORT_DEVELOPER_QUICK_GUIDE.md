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
2. `Main dataset`: koja stored procedura i koje kolone cine glavni body.
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
3. Klikni `Load procedures`.
4. Izaberi stored proceduru.
5. Klikni `Inspect`.
6. Provjeri `Main dataset`:
   - da li se vide parametri procedure,
   - da li se vide kolone.
7. Ako kolone nisu prepoznate, klikni `Suggest columns`.
8. Na `Output` izaberi gdje ide `.rdl`.
9. Klikni `Generate`.
10. Otvori `.rdl` u Report Builderu.

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
3. Kolone koje su grupe se ne prikazuju u detail redu, vec idu u group header.
4. Na numerickim kolonama izaberi `Sum`, `Avg`, `Min`, `Max` ili `Count`.
5. Na tekstualnim kolonama koristi uglavnom `Count` ili `CountDistinct`.

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

- SQL params: stvarni parametri stored procedure.
- Report params: parametri koje korisnik vidi u reportu.

Jedan report parametar moze biti vezan na SQL parametar kroz `Bind to SP param`.

Primjer cascading parametara:

```text
RegionId        nije SP param, koristi se za filtriranje
MunicipalityId  zavisi od RegionId
OrganisationId  zavisi od MunicipalityId i binduje se na SP parametar @OrganisationId
```

Za `Depends on` biraj samo parametre koji su prije trenutnog parametra. Zato je `Ordinal` vazan.

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
