# Tehnicka dokumentacija

Ovaj dokument opisuje glavne cjeline koda i metode koje kolege najcesce moraju razumjeti prije izmjena.

## Arhitektura

Tok sistema je:

1. `Command1.ExecuteCommandAsync` se poziva iz Visual Studio komande.
2. `ReportDialogService.ShowSetupDialog` otvara WPF dijalog u STA threadu i postavlja owner prozor.
3. `ReportSetupDialog` vodi korisnika kroz konfiguraciju, ucitava metadata, cuva state i poziva generisanje.
4. `ReportOutputWriter.Write` normalizuje izlaznu putanju, poziva `RdlBuilder`, eventualno konvertuje u RDLC i cuva JSON state.
5. `RdlBuilder.Build` sastavlja RDL XML iz `ReportModel`.

`ReportModel` je centralni DTO. Sve sto treba opstati kroz save/load mora biti dio tog modela ili njegovih child config klasa.

## Command1

### `ExecuteCommandAsync`

Ulazna tacka ekstenzije. Metoda pokusava pronaci folder aktivnog solutiona, uzima foreground Visual Studio prozor kao owner i otvara setup dialog. Sve greske se hvataju i prikazuju korisniku kroz Visual Studio prompt.

### `ResolveSolutionDirectoryAsync`

Primarni nacin koristi Visual Studio workspace query API. Ako to ne uspije, prelazi na fallback pretragu.

### `ResolveSolutionDirectoryFallback`

Krece od `Directory.GetCurrentDirectory()` i `AppContext.BaseDirectory`, ide prema parent folderima i trazi `.sln` ili `.slnx`.

## ReportDialogService

### `ShowSetupDialog`

Kreira `ReportSetupDialog` u zasebnom STA threadu. Ovo je bitno jer WPF dijalozi moraju biti na STA threadu, a Visual Studio extensibility komanda ne garantuje taj kontekst. Metoda primjenjuje VS temu i owner handle da dijalog ne ode iza Visual Studija.

### `ShowConnectionDialog`

Isti obrazac kao setup dialog, ali za connection dialog. Ostavljeno kao odvojena metoda zbog moguce ponovne upotrebe i jasnijeg ownership-a.

## ReportSetupDialog

Glavni UI i najveca klasa u projektu. Metode su grupisane po toku rada:

- connection i metadata,
- save/load state,
- SQL editori,
- template editori,
- report variables preview,
- build/apply `ReportModel`,
- pomocne metode za citanje UI vrijednosti.

### `SelectConnectionAsync`

Otvara `DatabaseConnectionDialog`, preuzima connection string i zatim po potrebi ucitava stored procedure.

### `LoadProceduresAsync`

Validira connection string, poziva `SqlIntrospector.ListStoredProceduresAsync` i puni combo listu procedura.

### `InspectProcedureAsync`

Cita parametre i kolone stored procedure kroz `SqlIntrospector.ReadStoredProcedureAsync`, pravi inicijalni `ReportModel` kroz `ReportModelFactory` i puni UI.

### `InspectSqlTextDatasetAsync`

Cita parametre i kolone SQL text dataset-a kroz `SqlIntrospector.ReadSqlTextDatasetAsync`. Metoda ne kreira fake `StoredProcedureMetadata`; SQL mode se cuva kroz `DatasetConfig.CommandKind = Text`, a SQL tekst kroz `DatasetConfig.Command`.

### `SuggestColumnsAsync`

Grananje zavisi od `Main dataset` source-a. U stored procedure mode-u koristi tekst procedure i `SqlProcedureColumnSuggester` kao fallback kada SQL Server ne moze opisati prvi result set. U SQL text mode-u koristi `SqlTextAnalyzer` i trazi jedan finalni top-level result-producing `SELECT`.

### `SaveStateButton_Click` i `LoadStateAsync`

Serijalizuju/deserijalizuju kompletan `ReportModel` preko `SpRdlJsonStore`. Ovo je osnovni checkpoint mehanizam za nastavak rada.

### `BuildReportModelFromCurrentState`

Najvaznija metoda u dialogu. Cita sve UI kontrole i gradi kompletan `ReportModel`. Ako neka nova opcija treba da se cuva u JSON i koristi pri generisanju, mora biti dodata ovdje.

### `ApplyReportModel`

Suprotan smjer od `BuildReportModelFromCurrentState`. Prima model iz JSON-a ili introspekcije procedure i popunjava UI. Za svaku novu opciju dodatu u model treba dodati i apply logiku. Main dataset source se obnavlja iz `mainDataset.CommandKind`; `CommandKind.Text` se ne smije prikazati kao naziv stored procedure.

### `BuildLookupAndDefaultDatasets`

Na osnovu report parametara dodaje dodatne datasetove za lookup i default vrijednosti. Dataset names dobijaju prefix `ds`, a datasource ostaje `dsr`.

### `PreviewReportVariablesSqlAsync`

Izvrsava SQL za report variables i vraca prvi red kao preview vrijednosti. Te vrijednosti se koriste za template preview i za provjeru placeholdera.

### `ResolveTemplatePlaceholders`

Zamjenjuje `{Placeholder}` u memorandum i summary templateima vrijednostima iz report variables preview-a, static/fallback vrijednostima ili poznatim sistemskim vrijednostima.

### `ReadmeButton_Click`

Otvara `ReadmeDialog` i prikazuje sadrzaj `README.md`. README se pri buildu kopira u output folder, a metoda ima fallback pretragu prema parent folderima za razvojni scenario.

## SqlIntrospector

### `ListStoredProceduresAsync`

Cita `sys.objects` i `sys.schemas` i vraca listu procedura sortiranu po shemi i nazivu.

### `ReadStoredProcedureAsync`

Parsirani naziv procedure koristi za citanje parametara i result kolona. Ako result kolone ne mogu biti procitane, metoda vraca warning i praznu listu polja, da korisnik moze nastaviti rucno.

### `ReadSqlTextDatasetAsync`

Cita metadata za raw SQL text bez izvrsavanja korisnickog SQL-a. Parametri se prvo citaju kroz `sys.sp_describe_undeclared_parameters`, a fallback je ScriptDom parser koji izbacuje lokalne `DECLARE` varijable. Kolone se prvo citaju kroz `sys.sp_describe_first_result_set`, a fallback je finalni top-level result `SELECT`.

### `SuggestFieldsFromProcedureTextAsync`

Cita `OBJECT_DEFINITION` i salje tekst procedure u parser koji trazi najvjerovatniji zadnji `SELECT`.

### `SuggestFieldsFromSqlText`

Poziva neutralni SQL text parser. Parser automatski vraca kolone samo kada postoji jedan stvarni top-level result `SELECT`; ako ih ima vise, vraca warning i ne puni kolone automatski.

### `SuggestLookupSqlAsync`

Traži tabelu ciji single-column primary key ima isti naziv kao parametar. Kao label kolone bira sve kolone koje u nazivu imaju `Name`. Ako postoji `DependsOn`, metoda pokusava dodati WHERE uslov na osnovu zavisnog parametra i njegovog tipa.

## ReportModelFactory

### `FromStoredProcedure`

Pravi pocetni `ReportModel` iz SQL metadata:

- `dsMain` dataset,
- SP command,
- bindings iz SP parametara u report parametre,
- report parametre sa promptom, tipom kontrole, formatom i ordinalom.

### `FromSqlText`

Pravi pocetni `ReportModel` za SQL text mode:

- `dsMain` dataset,
- `CommandKind.Text`,
- SQL text kao `Command`,
- bindings iz SQL parametara u report parametre,
- report parametre sa promptom, tipom kontrole, formatom i ordinalom.

## ReportOutputWriter

### `Write`

Normalizuje ekstenziju po output mode-u, kreira folder, generise RDL, po potrebi konvertuje u RDLC i cuva XML. Nakon toga cuva `.sp2rdl.json` pored izvjestaja.

### `GetModelPath`

Vraca putanju state fajla za dati RDL/RDLC.

## Reporting metadata SQL

### `ReportingMetadataReader`

Cita `Reporting.ParameterDefinition` i povezani `Reporting.ComponentType` iz baze izabrane na `Output` tabu. `Apply definitions` na `Report params` tabu koristi ovaj reader da po imenu parametra primijeni globalne default vrijednosti na postojece redove, bez automatskog dodavanja novih report parametara.

Isti reader cita `ReportVersion` listu i pripadajuce `UiParameter` redove za `Load from report...` tok. Import dijalog prikazuje checkbox listu parametara, a glavna forma dodaje nedostajuce redove ili, ako korisnik ukljuci update opciju, osvjezava postojece redove kompletnim runtime podesavanjima.

### `ReportingMigrationSqlBuilder`

Gradi Flyway SQL za runtime `Reporting` metadata tabele na osnovu trenutnog `ReportModel`. Koristi `MERGE` za report, definicije parametara, UI parametre i dependency zapise. `ReportVersion` se bira po paru `ReportId/ValidFrom`: ako verzija za taj datum postoji, koristi se ona; ako ne postoji, kreira se nova verzija sa narednim brojem.

`Preview SQL`, `Save SQL` i `Execute SQL` koriste isti builder. `Execute SQL` dodatno provjerava da ciljna baza vec ima `Reporting` semu i zatim izvrsava generisane batch-eve direktno nad izabranom Reporting konekcijom.

Kod `UiParameterDependency` redova `CompareOperatorId` se popunjava samo kada je `CompareParams = 1`. Za obicne filter/cascading dependency redove (`CompareParams = 0`) ostaje `NULL`; isto pravilo je zasticeno CHECK constraintom u Reporting modelu.

`ReportValidatorCatalog` ucitava `config/report-validators.json` iz solution foldera, output foldera ili embedded defaulta. Config ima top-level `kinds` katalog dozvoljenih pripadnosti i listu runtime settings stavki. `ParameterValidatorsDialog` koristi katalog da za trenutni `ControlType` prikaze samo dozvoljene runtime settings. Stavke imaju `kind` (`validation`, `behavior`, `metadata`), pa se i metadata poput `defaultValue` cuva u istom toku. Izabrane vrijednosti se cuvaju u `ReportParameter.RuntimeSettings`, a builder ih serializuje u `UiParameter.RuntimeSettings`.

Za `valueType = dateExpression` dialog prihvata `yyyy-MM-dd` ili relativne izraze: `today`, `startOfWeek`, `endOfWeek`, `startOfMonth`, `endOfMonth`, `startOfQuarter`, `endOfQuarter`, `startOfYear`, `endOfYear`, uz opcionalni offset `+/-N` i jedinicu `d/w/m/q/y`. Generator validira samo sintaksu; runtime aplikacija evaluira stvarni datum.

### `Reporting_Core_Model.sql`

Bootstrap skripta za pocetno kreiranje `Reporting` seme i osnovnih tabela. Ukljucena je u VSIX kao fajl i kao embedded fallback. `Install schema` na `Output` tabu smije je izvrsiti samo kada ciljna baza jos nema `Reporting` semu.

Skripta dodatno obezbjedjuje `Localization.Language`: kreira `Localization` semu i `Language` tabelu ako nedostaju, seeduje osnovne jezike i dodaje `FK_ReportVersion_Language` sa `Reporting.ReportVersion.LanguageId` na `Localization.Language.Id`.

`TR_ReportVersion_ClosePreviousIntervals` se izvrsava nakon inserta ili promjene `ReportId`/`LanguageId`/`ValidFrom` na verziji reporta. Za isti `ReportId` i `LanguageId` zatvara svaku postojecu verziju u ciji interval upada novi `ValidFrom`, tako sto `ValidTo` postavlja na dan prije novog `ValidFrom`.

## RdlBuilder

`RdlBuilder` je centralna klasa za RDL XML. Ona ne cita UI i ne ide na bazu; ocekuje kompletan `ReportModel`.

### `Build`

Ucita embedded `SkeletonTemplate.rdl`, validira dependencies parametara, zamjenjuje top-level RDL sekcije i na kraju postavlja novi `ReportID`.

### `BuildDataSources`

Generise `DataSources`. Ako postoji connection string, upisuje connection properties. Ako ne postoji, koristi shared data source reference.

### `BuildConnectionInfo`

Normalizuje connection string za integrated security. Cilj je da RDL credential type bude uskladjen sa connection stringom.

### `BuildDataSets`

Generise sve datasetove iz modela i po potrebi dodaje dataset za report variables.

### `BuildReportVariablesDataset`

Ako je ukljucen dynamic source za variables, pravi dataset `dsReportVariables` iz SQL-a i iz kolona koje su vezane za report variables.

### `BuildEmbeddedImages`

Dodaje embedded slike za footer logo i memorandum logo. Slike se u RDL upisuju kao base64.

### `BuildReportParameters`

Generise RDL `ReportParameters`, ukljucujuci lookup vrijednosti, default vrijednosti, nullable/multivalue pravila i zavisnosti.

### `BuildReportParametersLayout`

Generise lijevi parameter panel. Broj redova i kolona se racuna tako da panel ne bude invalidan kada ima vise parametara.

### `ApplyBody`

Slaze body segment redom: memorandum/subreport, report title, parameter summary, validation warnings, tablix, report summary.

### `BuildTablix`

Generise glavni tablix. Ulaz su dataset kolone, grupe, agregacije i `TablixStyleConfig`. Sirina tablixa je procenat korisne sirine stranice, a kolone se automatski rasporedjuju unutar te sirine.

### `CalculateTablixColumnWidths`

Racuna relativne sirine kolona po tipu:

- datumi i numerika dobijaju kompaktnije sirine,
- kraci tekst dobija srednju sirinu,
- duzi tekst dobija fleksibilan prostor,
- tablix se na kraju popunjava do zadate sirine.

### `BuildGroupHeaderRow`

Generise group header sa `ColSpan`, tako da se celije vizuelno spoje u jednu siroku celiju. Tekst je lijevo poravnat.

### `BuildAggregateRow`

Generise subtotal i grand total redove. Label se spaja preko kolona prije prve agregirane kolone, da prva agregirana kolona ne izgubi vrijednost.

### `GetGroupVisualStyle`

Vraca boju i font style po nivou grupe. Visi nivo je svjetliji, level 2 i 4 su italic, grand total se posebno tamni i bolda.

### `BuildMemorandumBand`

Generise inline memorandum: logo, template tekst, separator linije i bottom line. Ako je izabran subreport i fallback nije potreban, inline memorandum se ne crta.

### `BuildReportSummaryBand`

Generise inline report summary sa 1-3 kolone ili subreport. Kolone imaju template, sirinu u procentima, vertical align i padding.

### `BuildTemplateTextboxes`

Pretvara jednostavni HTML-like template u jedan ili vise RDL textboxova. Podrzava osnovne tagove i style `text-align`.

### `ApplyHeaderFooter`

Generise page header i page footer. Placeholderi kao `{PageNo}` i `{PageCount}` se prevode u RDL globals.

### `ValidateParameterDependencies`

Sprjecava forward dependency u parametrima. Parametar smije zavisiti samo od ranijih parametara po ordinalu.

## Localization

Lokalizacija staticnih labela u generisanom RDL-u je rijesena preko:

- `Model/LocalizationConfig.cs` — UI/state model. Polja `TranslationTable`, `ReportTable`, `DefaultLanguageId`, `GeneralReportId`, `SkipKeysFromGeneralReport`, `GenerateDefaultLanguageSeed`, `GenerateLanguageTemplatesFor`, `LanguageTemplateValueMode`. Defaulti za schema/name su prazni — alat je generican i imena tabela ne pretpostavlja.
- `Generation/LocalizationLabelCollector.cs` — sakuplja labele iz modela: `ReportTitle`, `GrandTotal`, `Subtotal`, `Column.{name}`, `Group.{name}`.
- `Generation/LocalizationSeedSqlBuilder.cs` — emituje seed SQL kao `MERGE` blokove. Kada je `SkipKeysFromGeneralReport` ukljuceno i `GeneralReportId > 0`, source u MERGE-u dobije `WHERE NOT EXISTS` filter da se preskoce vec postojeci kljucevi pod opstim report-om.
- `Generation/RdlBuilder.cs` (`BuildLocalizationLabelsSql`, `ApplyLocalization*`) — dodaje hidden parametre `ReportId`/`LanguageId`, `dsReportLabels` dataset i `Placeholder` ekspresije koje koriste prevode sa fallback-om na `GeneralReportId` pa na hardkodovan default.
- `Generation/ReportOutputWriter.cs` — uz report i `.sp2rdl.json` moze snimiti i `*.translations.sql` kada je ukljucen `GenerateDefaultLanguageSeed`.

### Dugme „Register report"

Handler `LocalizationRegisterReportButton_Click` u `ReportSetupDialog.xaml.cs` (`RegisterReportInDatabaseAsync`):

1. Otvara `SqlConnection` na trenutnu konekciju.
2. Radi `MERGE` na `Report` tabeli (par `InternalName` = TxtReportName, `DisplayName` = TxtReportTitle, `ReportFileName` = `Path.GetFileName(OutputPath)`).
3. Cita `ReportId` kroz `SELECT` nakon MERGE-a i upise u `TxtLocalizationReportId`.
4. Ako je localization ukljuceno, poziva `MergeDefaultLanguageTranslationsAsync` koji za svaki sakupljeni `LocalizationLabel` izvrsi MERGE u tabelu prevoda. Postojeci zapis za isti `ReportId/LanguageId/Key` se azurira default vrijednoscu, a nedostajuci zapis se dodaje. Ako je `skipIfExistsForGeneralReportId` postavljen, source koristi `WHERE NOT EXISTS` filter prema `GeneralReportId`.

### Minimalne strukture tabela

Tool ne kreira tabele sam. Korisnik mora obezbijediti:

- **Report tabelu**: `ReportId int IDENTITY` PK, `InternalName nvarchar` UNIQUE, `DisplayName nvarchar`, `ReportFileName nvarchar`, `[Public] bit`.
- **Translation tabelu**: `ReportId int`, `LanguageId int`, `[Key] nvarchar`, `[Value] nvarchar(max)`, `Deleted bit` (filter `Deleted = 0`). Preporucen UNIQUE constraint nad `(ReportId, LanguageId, [Key])` da MERGE bude deterministican.

Vidi README sekciju „Lokalizacija" za primjer DDL-a.

## Persistence

### `SpRdlJsonStore.Save`

Serijalizuje `ReportModel` u camelCase JSON sa enum vrijednostima kao stringovima.

### `SpRdlJsonStore.Load`

Ucita JSON i vrati `ReportModel`. Ako deserijalizacija vrati null, baca jasnu gresku.

## Pravila za buduce izmjene

1. Svaka nova UI opcija mora imati:
   - property u modelu,
   - mapping u `BuildReportModelFromCurrentState`,
   - mapping u `ApplyReportModel`,
   - upotrebu u `RdlBuilder`, ako utice na RDL,
   - opis u README-u ako je korisnicka opcija.
2. Ne dodavati RDL XML stringove rucno ako se moze koristiti `XElement`.
3. Ne generisati prazne top-level RDL elemente kao `DataSets` ili `ReportParameters`.
4. Kod lookup i default datasetova drzati prefix `ds`.
5. Data source naziv drzati sa prefixom `dsr`.
6. Ako SQL Server introspekcija ne uspije, korisnik mora imati rucni fallback.
