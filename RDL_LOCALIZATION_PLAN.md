# RDL Localization Plan for sp2rdlGenExtension

> **Status:** plan je u najvecoj mjeri implementiran. Razlike u odnosu na prvobitni plan:
> - Mod „procedure" je **uklonjen** (komplikovao je razvoj). Generator uvijek cita prevode iz tabele.
> - Polja `accessMode` i `translationProcedure` ostaju u JSON modelu radi kompatibilnosti, ali nisu izlozena u UI.
> - Dodato: dugme **Register report** (MERGE u Report tabelu + automatski MERGE default-language prevoda), opcija **Skip keys already translated under General ReportId**, i `MERGE` umjesto `INSERT` u seed SQL-u.
> - Default vrijednosti za schema/name su prazne — tool je generican, imena tabela se ne pretpostavljaju.
> - Trenutni opis ponasanja i minimalne strukture tabela: vidi **README.md** sekcija „Lokalizacija" i **docs/REPORT_DEVELOPER_QUICK_GUIDE.md** sekcija „Lokalizacija".

## Summary

Dodati podrsku za visejezicnost statickih labela u generisanim RDL izvjestajima: naslov izvjestaja, naslovi kolona, header/footer tekstovi, summary/potpisne sekcije i drugi fiksni tekstovi u layout-u.

Ne prevoditi podatke koji dolaze iz glavnog dataset-a.

Generator treba podrzati dva nacina citanja prevoda:

- direktno iz konfigurisane tabele prevoda
- preko konfigurisane procedure/funkcije za prevode

## Localization Config

U sp2rdl config dodati sekciju:

```json
"localization": {
  "enabled": true,
  "reportId": 2093,
  "defaultLanguageId": 3,
  "generalReportId": 0,

  "accessMode": "table",

  "translationTable": {
    "schema": "BasicCatalogs",
    "name": "ReportTranslation"
  },

  "translationProcedure": {
    "schema": "Report",
    "name": "Translation"
  },

  "generateDefaultLanguageSeed": true,
  "generateLanguageTemplatesFor": [1],
  "languageTemplateValueMode": "copyDefault"
}
```

Pravila:

- `accessMode = "table"` koristi `translationTable.schema/name`.
- `accessMode = "procedure"` koristi `translationProcedure.schema/name`.
- `defaultLanguageId` je jezik originalnih/default tekstova.
- `generalReportId` je fallback report za zajednicke labele, podrazumijevano `0`.
- `generateLanguageTemplatesFor` je lista dodatnih jezika za koje generator pravi template SQL skriptu.
- `languageTemplateValueMode` definise sta ide u `Value` za dodatne jezike.

Podrzane vrijednosti za `languageTemplateValueMode`:

```text
copyDefault
todoPrefix
empty
```

Preporuceni default:

```text
copyDefault
```

## Translation Table Contract

Bez obzira na naziv seme i tabele, struktura tabele prevoda mora biti poznata i kompatibilna:

```sql
CREATE TABLE [schema].[table]
(
    [ReportTranslation] int IDENTITY(1,1) NOT NULL,
    [LanguageId] smallint NOT NULL,
    [ReportId] int NOT NULL,
    [Key] varchar(100) NOT NULL,
    [Value] nvarchar(4000) NOT NULL,
    [ModifiedAt] datetime NULL,
    [ModifiedByUser] int NULL,
    [Deleted] bit NOT NULL
);
```

Minimalno potrebne kolone za generator/RDL:

```text
LanguageId
ReportId
Key
Value
Deleted
```

Preporucena jedinstvenost:

```sql
UNIQUE (ReportId, LanguageId, [Key])
```

`Deleted = 1` zapise generator/RDL ne smije koristiti.

## Label Model

Generator interno formira listu labela:

```json
{
  "key": "Column.Odjeljenje",
  "fieldName": "Column_Odjeljenje",
  "defaultValue": "Odjeljenje"
}
```

Pravila:

- `key` ide u tabelu/proceduru prevoda.
- `fieldName` ide kao kolona u RDL dataset `dsReportLabels`.
- `defaultValue` je fallback tekst u default jeziku.

Primjeri kljuceva:

```text
ReportTitle
Column.Odjeljenje
Column.Poslodavac
Column.DatumPocetkaPrakse
Footer.PageOf
Summary.Director
Summary.InvoicedBy
```

## RDL Parameters

Kada je `localization.enabled = true`, generator dodaje/obezbjedjuje:

```text
ReportId
- Hidden: true
- DataType: Integer
- DefaultValue: localization.reportId

LanguageId
- koristi postojeci ako vec postoji
- dodaje novi ako ne postoji
- moze biti hidden ako aplikacija salje jezik
- DefaultValue: localization.defaultLanguageId
```

Ne dodavati runtime checkbox `EnableLocalization` u RDL. Visejezicnost je generation-time opcija generatora.

## Dataset `dsReportLabels`

Generator dodaje dataset `dsReportLabels` koji vraca tacno jednu vrstu i jednu kolonu po labeli.

Za `accessMode = "procedure"`:

```sql
SELECT
    COALESCE(MAX(CASE WHEN T.[KEY] = 'ReportTitle' THEN T.Value END), N'Pregled ucenika na praksi') AS ReportTitle,
    COALESCE(MAX(CASE WHEN T.[KEY] = 'Column.Odjeljenje' THEN T.Value END), N'Odjeljenje') AS Column_Odjeljenje
FROM [Report].[Translation](@ReportId, @LanguageId) AS T;
```

Za `accessMode = "table"` generator pravi SQL direktno nad konfigurisanom tabelom:

```sql
WITH T AS
(
    SELECT RT.[Key], RT.[Value], 1 AS Priority
    FROM [BasicCatalogs].[ReportTranslation] AS RT
    WHERE RT.ReportId = @ReportId
      AND RT.LanguageId = @LanguageId
      AND RT.Deleted = 0

    UNION ALL

    SELECT RT.[Key], RT.[Value], 2 AS Priority
    FROM [BasicCatalogs].[ReportTranslation] AS RT
    WHERE RT.ReportId = @GeneralReportId
      AND RT.LanguageId = @LanguageId
      AND RT.Deleted = 0
)
SELECT
    COALESCE(MAX(CASE WHEN X.[KEY] = 'ReportTitle' THEN X.Value END), N'Pregled ucenika na praksi') AS ReportTitle,
    COALESCE(MAX(CASE WHEN X.[KEY] = 'Column.Odjeljenje' THEN X.Value END), N'Odjeljenje') AS Column_Odjeljenje
FROM
(
    SELECT [Key], [Value]
    FROM
    (
        SELECT
            T.[Key],
            T.[Value],
            ROW_NUMBER() OVER (PARTITION BY T.[Key] ORDER BY T.Priority) AS RowNo
        FROM T
    ) AS R
    WHERE R.RowNo = 1
) AS X;
```

Dataset parametri:

```text
@ReportId = Parameters!ReportId.Value
@LanguageId = Parameters!LanguageId.Value
@GeneralReportId = localization.generalReportId
```

## RDL Expressions

Textbox-i koriste kratke izraze:

```vb
=First(Fields!Column_Odjeljenje.Value, "dsReportLabels")
```

Ne koristiti dugacke `Lookup(...)` izraze po svakoj labeli.

Lokalizovani textbox-i treba da imaju citljive nazive:

```text
txtLbl_ReportTitle
txtLbl_Column_Odjeljenje
txtLbl_Column_Poslodavac
txtLbl_Footer_PageOf
txtLbl_Summary_Director
```

## Header/Footer

Za page header/footer provjeriti da li konkretan RDL moze direktno koristiti dataset expression.

Ako ne moze, generator treba da:

- napravi hidden textbox u body-ju
- u njemu izracuna vrijednost iz `dsReportLabels`
- u header/footer koristi `ReportItems!TextboxName.Value`

Primjer:

```vb
=ReportItems!txtHidden_Footer_PageOf.Value
```

## Seed SQL

Generator opciono generise SQL seed skriptu za default jezik.

Seed mora koristiti konfigurisani `translationTable.schema/name` i ne smije prepisivati postojece rucne prevode.

Primjer:

```sql
IF NOT EXISTS (
    SELECT 1
    FROM [BasicCatalogs].[ReportTranslation]
    WHERE ReportId = 2093
      AND LanguageId = 3
      AND [Key] = 'Column.Odjeljenje'
      AND Deleted = 0
)
BEGIN
    INSERT INTO [BasicCatalogs].[ReportTranslation]
        (ReportId, LanguageId, [Key], Value, Deleted)
    VALUES
        (2093, 3, 'Column.Odjeljenje', N'Odjeljenje', 0);
END
```

Za jezike iz `generateLanguageTemplatesFor`, generator pravi dodatne template seed zapise sa upisanim `LanguageId`.

Primjer za `LanguageId = 1` i `languageTemplateValueMode = "copyDefault"`:

```sql
IF NOT EXISTS (
    SELECT 1
    FROM [BasicCatalogs].[ReportTranslation]
    WHERE ReportId = 2093
      AND LanguageId = 1
      AND [Key] = 'Column.Odjeljenje'
      AND Deleted = 0
)
BEGIN
    INSERT INTO [BasicCatalogs].[ReportTranslation]
        (ReportId, LanguageId, [Key], Value, Deleted)
    VALUES
        (2093, 1, 'Column.Odjeljenje', N'Odjeljenje', 0);
END
```

Ponasalje `languageTemplateValueMode`:

```text
copyDefault
- u Value se upisuje default tekst

todoPrefix
- u Value se upisuje tekst oblika TODO: <default tekst>

empty
- u Value se upisuje prazan string
```

Stvarni unos i odrzavanje prevoda treba da ide kroz poseban admin/interfejs.

## Responsibility Split

Generator:

- zna koje staticke labele postoje
- generise `dsReportLabels`
- generise kratke RDL expression-e
- generise default-language seed SQL
- generise template seed za jezike iz `generateLanguageTemplatesFor`

Admin/interfejs za prevode:

- odrzava prevode po jeziku
- prikazuje nedostajuce prevode
- omogucava izmjenu vrijednosti
- omogucava kopiranje default jezika kao pocetne vrijednosti za drugi jezik

## Test Plan

Provjeriti:

- RDL ima hidden `ReportId`
- RDL ima `LanguageId`
- postoji dataset `dsReportLabels`
- `dsReportLabels` vraca jednu vrstu
- `accessMode = "table"` koristi konfigurisanu semu i tabelu
- `accessMode = "procedure"` koristi konfigurisanu proceduru/funkciju
- staticke labele koriste kratke `First(Fields!...` izraze
- default jezik prikazuje fallback vrijednosti
- drugi jezik prikazuje unesene prevode
- nedostajuci prevod pada na `defaultValue`
- report-specific prevod ima prednost nad `generalReportId`
- seed SQL ne prepisuje postojece prevode
- template SQL ima vec upisan ciljani `LanguageId`
- page header/footer rade u preview-u i PDF export-u

## Implementation Checkpoint - 2026-05-04

Implementiran je prvi stabilni korak lokalizacije:

- dodat je `localization` dio u report model i JSON state
- dodat je UI tab `Localization`
- generator dodaje `ReportId` kao hidden parametar kada je lokalizacija ukljucena
- generator dodaje `LanguageId` ako vec ne postoji u report parametrima
- generator dodaje dataset `dsReportLabels`
- lokalizovani su naslov izvjestaja, headeri kolona, labela `Ukupno`, labela `Podzbir` i nazivi polja u group header/subtotal tekstu
- pored RDL fajla generise se `*.translations.sql` seed skripta kada je ukljucen `Generate seed SQL`
- u `Stored procedure columns` dodata je kolona `Default label`; seed za default jezik i fallback vrijednosti labela koriste taj tekst kada je unesen, a tehnicki naziv kolone ostaje fallback

Napomena za naredni korak:

- `accessMode = procedure` trenutno generise SQL oblik za table-valued funkciju/proceduralni wrapper koji se moze koristiti u `FROM` dijelu upita. Ako bude potrebna prava stored procedure koja vraca result set, generator treba prosiriti posebnim mehanizmom jer SQL Server ne moze direktno pivotirati `EXEC proc` u istom SELECT-u.
- page header/footer prevodi nisu agresivno uvedeni u ovom koraku zbog RDL ogranicenja oko dataset izraza u page sekcijama. Za njih treba koristiti hidden body textbox + `ReportItems!` pristup iz plana.

## Assumptions

- Visejezicnost je opcija generatora, ne runtime opcija u RDL-u.
- `ReportId` dolazi iz config-a.
- `defaultLanguageId` je jezik originalnih tekstova.
- Naziv tabele prevoda moze varirati po bazi, ali struktura tabele je poznata.
- Poslovni podaci iz glavnog dataset-a nisu dio ovog scope-a.
- Prevodi za ostale jezike se odrzavaju kroz poseban interfejs.
