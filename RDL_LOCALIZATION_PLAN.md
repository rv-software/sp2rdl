# RDL Localization Plan for sp2rdlGenExtension

> **Status:** plan je u najvecoj mjeri implementiran. Razlike u odnosu na prvobitni plan:
> - Mod „procedure" je **uklonjen** (komplikovao je razvoj). Generator uvijek cita prevode iz tabele.
> - Polja `accessMode` i `translationProcedure` ostaju u JSON modelu radi kompatibilnosti, ali nisu izlozena u UI.
> - Dodato: dugme **Register report** (MERGE u Report tabelu + automatski MERGE default-language prevoda), opcija **Skip keys already translated under General ReportId**, i `MERGE` umjesto `INSERT` u seed SQL-u.
> - Default vrijednosti za schema/name su prazne — tool je generican, imena tabela se ne pretpostavljaju.
> - Trenutni opis ponasanja i minimalne strukture tabela: vidi **README.md** sekcija „Lokalizacija" i **docs/REPORT_DEVELOPER_QUICK_GUIDE.md** sekcija „Lokalizacija".

## Summary

Dodati podrsku za visejezicnost statickih labela u generisanim RDL izvjestajima. Trenutno implementirani opseg obuhvata naslov izvjestaja, naslove kolona, group header labele, `Ukupno` i `Podzbir`.

Ne prevoditi podatke koji dolaze iz glavnog dataset-a.

Generator trenutno cita prevode direktno iz konfigurisane tabele prevoda. Raniji mod preko procedure/funkcije je uklonjen iz UI-a i ne treba ga koristiti u novoj konfiguraciji.

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
    "schema": "",
    "name": ""
  },

  "reportTable": {
    "schema": "",
    "name": ""
  },

  "translationProcedure": {
    "schema": "",
    "name": ""
  },

  "generateDefaultLanguageSeed": true,
  "skipKeysFromGeneralReport": false,
  "generateLanguageTemplatesFor": [1],
  "languageTemplateValueMode": "copyDefault"
}
```

Pravila:

- `accessMode` ostaje u JSON modelu radi kompatibilnosti, ali UI podrzava samo table mode.
- `translationTable.schema/name` mora biti popunjeno za runtime prevode i seed SQL.
- `reportTable.schema/name` mora biti popunjeno za dugme **Register report**.
- `translationProcedure` ostaje u JSON modelu radi kompatibilnosti, ali se ne koristi u UI workflow-u.
- `defaultLanguageId` je jezik originalnih/default tekstova.
- `generalReportId` je fallback report za zajednicke labele, podrazumijevano `0`.
- `skipKeysFromGeneralReport` preskace seed/merge za kljuceve koji vec postoje pod `generalReportId`.
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

Napomena: `Footer.*` i `Summary.*` kljucevi ostaju kao planirani primjer za kasniju doradu. Trenutni generator ih jos ne sakuplja automatski u `LocalizationLabelCollector`.

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

Generator dodaje dataset `dsReportLabels` koji vraca tacno jednu vrstu i jednu kolonu po labeli. Generator pravi SQL direktno nad konfigurisanom tabelom prevoda:

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
GeneralReportId se ugradjuje kao vrijednost iz localization.generalReportId
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

Generator opciono generise SQL seed skriptu `*.translations.sql` pored RDL/RDLC fajla.

Seed koristi konfigurisani `translationTable.schema/name` i generise `MERGE` blokove. Postojeci zapis za isti `ReportId/LanguageId/Key` se azurira default/template vrijednoscu, a nedostajuci zapis se dodaje. Skriptu treba pregledati prije produkcije.

Primjer:

```sql
MERGE INTO [BasicCatalogs].[ReportTranslation] AS T
USING (VALUES (2093, 3, 'Column.Odjeljenje', N'Odjeljenje'))
    AS S(ReportId, LanguageId, [Key], [Value])
    ON T.ReportId = S.ReportId
   AND T.LanguageId = S.LanguageId
   AND T.[Key] = S.[Key]
   AND T.Deleted = 0
WHEN MATCHED THEN
    UPDATE SET [Value] = S.[Value]
WHEN NOT MATCHED THEN
    INSERT (ReportId, LanguageId, [Key], [Value], Deleted)
    VALUES (S.ReportId, S.LanguageId, S.[Key], S.[Value], 0);
```

Za jezike iz `generateLanguageTemplatesFor`, generator pravi dodatne template seed zapise sa upisanim `LanguageId`.

Primjer za `LanguageId = 1` i `languageTemplateValueMode = "copyDefault"`:

```sql
MERGE INTO [BasicCatalogs].[ReportTranslation] AS T
USING (VALUES (2093, 1, 'Column.Odjeljenje', N'Odjeljenje'))
    AS S(ReportId, LanguageId, [Key], [Value])
    ON T.ReportId = S.ReportId
   AND T.LanguageId = S.LanguageId
   AND T.[Key] = S.[Key]
   AND T.Deleted = 0
WHEN MATCHED THEN
    UPDATE SET [Value] = S.[Value]
WHEN NOT MATCHED THEN
    INSERT (ReportId, LanguageId, [Key], [Value], Deleted)
    VALUES (S.ReportId, S.LanguageId, S.[Key], S.[Value], 0);
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
- generise default-language seed SQL kao `MERGE`
- generise template seed za jezike iz `generateLanguageTemplatesFor` kao `MERGE`

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
- table mode koristi konfigurisanu semu i tabelu prevoda
- procedure mode nije dio UI workflow-a
- staticke labele koriste kratke `First(Fields!...` izraze
- default jezik prikazuje fallback vrijednosti
- drugi jezik prikazuje unesene prevode
- nedostajuci prevod pada na `defaultValue`
- report-specific prevod ima prednost nad `generalReportId`
- seed SQL koristi MERGE i azurira/dodaje zapise za isti `ReportId/LanguageId/Key`
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

Napomena:

- `accessMode` i `translationProcedure` ostaju u modelu samo radi kompatibilnosti sa ranije sacuvanim JSON state fajlovima.
- page header/footer prevodi nisu agresivno uvedeni zbog RDL ogranicenja oko dataset izraza u page sekcijama. Za njih treba koristiti hidden body textbox + `ReportItems!` pristup iz plana.

## Assumptions

- Visejezicnost je opcija generatora, ne runtime opcija u RDL-u.
- `ReportId` dolazi iz config-a.
- `defaultLanguageId` je jezik originalnih tekstova.
- Naziv tabele prevoda moze varirati po bazi, ali struktura tabele je poznata.
- Poslovni podaci iz glavnog dataset-a nisu dio ovog scope-a.
- Prevodi za ostale jezike se odrzavaju kroz poseban interfejs.
