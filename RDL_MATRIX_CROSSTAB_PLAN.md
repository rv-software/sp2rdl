# Plan: Matrix / Crosstab Layout

## Summary

Dodati trecu nezavisnu granu za glavni tablix: `Matrix / Crosstab`. Postojeci `Band oriented` i `Tabular horizontal` layout ne smiju biti naruseni.

Matrix layout treba da podrzi prakticne analiticke reporte slicne FastReport/Stimulsoft crosstab komponenti: redne grupe lijevo, dinamicke kolone po vrijednostima iz dataseta i agregirane mjere u presjeku red/grupa.

## Cilj

Omoguciti da dataset moze biti u dugom obliku:

```text
LevelYear | DepartmentId | GradeName | StudentCount
1         | 10           | Excellent | 579
1         | 10           | VeryGood  | 479
1         | 10           | Good      | 499
```

a da RDL generise matrix:

```text
LevelYear | DepartmentId | Excellent | VeryGood | Good
1         | 10           | 579       | 479      | 499
```

## Novi Layout Mode

Na `Body / Tablix` ostaju tri moda:

- `Band oriented`
- `Tabular horizontal`
- `Matrix / Crosstab`

Default ostaje `Band oriented`.

## Dataset Column Roles

Na `Main dataset` gridu treba dodati matrix uloge za kolone:

- `None`: kolona se koristi kao obicna/detail kolona u drugim layoutima.
- `Row group`: vrijednosti idu lijevo kao redne grupe.
- `Column group`: vrijednosti postaju dinamicke kolone matrixa.
- `Measure`: vrijednost koja se agregira u celijama matrixa.

Za prvi korak podrzati:

- vise row group kolona,
- jednu column group kolonu,
- jednu measure kolonu.

Kasnije prosirenje:

- vise column group nivoa,
- vise measure kolona,
- subtotal/grand total po redovima i kolonama,
- posebni stilovi za matrix header/body/total.

## UI Plan

Na `Main dataset` tabu dodati kolone:

- `Matrix role`
- `Matrix level`

Pravila:

- `Row group` koristi `Matrix level` za redoslijed lijevih grupa.
- `Column group` za prvi korak smije biti samo jedna kolona.
- `Measure` mora imati agregaciju (`Sum`, `Count`, `Avg`, `Min`, `Max`, `CountDistinct`).
- Ako je `Grouping layout = Matrix / Crosstab`, validacija prije generate mora upozoriti ako nedostaje row group, column group ili measure.

## RDL Generation Plan

Napraviti posebnu metodu:

```text
BuildMatrixCrosstabTablix(...)
```

Prva verzija:

- static row group columns lijevo,
- dynamic column group u `TablixColumnHierarchy`,
- measure cell kao agregat u presjeku row/column scope-a,
- row total kolona za measure,
- column total red za measure,
- grand total u donjem desnom uglu,
- matrix header redovi,
- bez narusavanja `BuildBandTablix` i `BuildTabularHorizontalTablix`.

## Save / Load

Nove postavke moraju ici u state JSON:

- `tablixStyle.groupRenderMode = MatrixCrosstab`
- po koloni: `matrixRole`, `matrixLevel`

Stari state fajlovi bez tih polja ostaju validni i ponašaju se kao `Band oriented`.

## Validation

Prije generisanja matrix reporta provjeriti:

- postoji bar jedan `Row group`,
- postoji tacno jedan `Column group` u prvoj verziji,
- postoji tacno jedan `Measure` u prvoj verziji,
- measure ima dozvoljenu agregaciju za SQL tip,
- row/column group kolone postoje u datasetu i nisu praznog imena.

## Test Plan

- Build:

```powershell
dotnet build .\sp2rdlGenExtension.csproj -p:CreateVsixContainer=false
```

- Regression:
  - Band oriented report se generise isto kao ranije.
  - Tabular horizontal report zadrzava group kolone i agregate udesno.

- Matrix:
  - jedan row group + jedan column group + jedan measure.
  - vise row group kolona.
  - column group sa tekstualnim vrijednostima.
  - numeric measure sa `Sum`.
  - date/text measure samo sa dozvoljenim agregacijama.

## Risk Notes

RDL matrix je osjetljiv jer koristi i row hierarchy i column hierarchy. Zato implementaciju raditi u malim koracima:

1. Model/UI/state za matrix role.
2. Validacija matrix konfiguracije.
3. Minimalni RDL matrix: 1 row group, 1 column group, 1 measure.
4. Vise row groups.
5. Totali i stilovi.
