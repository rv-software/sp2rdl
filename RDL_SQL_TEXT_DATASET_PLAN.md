# Plan: Main Dataset From Stored Procedure Or T-SQL Text

## Summary

Extend the generator so the main dataset can come from either a stored procedure or raw T-SQL text. Stored procedure mode remains the default and must keep the current behavior.

SQL text mode uses the existing `DatasetConfig.CommandKind = Text` model support. The main changes are in the dialog, save/load state flow, and SQL introspection.

## Key Changes

- Add a Main dataset source selector: `Stored procedure` or `SQL text`.
- Keep stored procedure mode unchanged: refresh procedures, inspect, suggest columns, procedure parameters, and automatic parameter binding.
- In SQL text mode:
  - provide `Edit SQL...` for entering the main dataset command text,
  - inspect undeclared parameters and result columns through SQL Server metadata,
  - use a parser fallback over the final top-level result-producing `SELECT`,
  - ignore locally declared variables when creating dataset/report parameters.
- Make Main dataset grid titles mode-aware:
  - `Stored procedure params/columns` in stored procedure mode,
  - `SQL params/columns` in SQL text mode.

## Result Set Rule

- SQL mode `Inspect` first asks SQL Server:
  - parameters: `sys.sp_describe_undeclared_parameters`,
  - columns: `sys.sp_describe_first_result_set`.
- If SQL Server describe succeeds, its returned columns are used.
- If SQL Server describe fails, the parser tries to find one final top-level result-producing `SELECT`.
- The parser ignores selects that are not report results:
  - `SELECT INTO #temp`,
  - `INSERT INTO ... SELECT`,
  - `SELECT @var = ...`,
  - CTE, subquery, derived table, and `EXISTS` selects.
- If multiple real top-level result sets are found, warn the user and leave columns for manual entry or SQL cleanup.

## Implementation Changes

- Store SQL text in `datasets[dsMain].command`.
- Store SQL mode as `datasets[dsMain].commandKind = Text`.
- Do not introduce a separate source SQL text field.
- Load state must restore SQL mode and must not place SQL text into the stored procedure combo box.
- `BuildReportModelFromCurrentState`, `BuildReportModelWithoutMetadata`, and `ApplyReportModel` must branch by dataset source mode.
- SQL mode must not create fake `StoredProcedureMetadata`.
- `ReportGenerationRequest.StoredProcedureName` is only meaningful for stored procedure mode.
- Add SQL text introspection in `SqlIntrospector`.
- Add neutral SQL text parsing support for undeclared parameters and final top-level result selects.
- Every new or materially changed C# method must include a concise XML comment.

## No Regression Rule

- Stored procedure mode must remain functionally identical.
- Do not change stored procedure RDL output except where neutral model handling requires it.
- If a change affects both modes, validate stored procedure mode first.

## Test Plan

- Compile:
  - `dotnet build .\sp2rdlGenExtension.csproj -p:CreateVsixContainer=false`
- Stored procedure regression:
  - connection,
  - load procedures,
  - inspect,
  - suggest columns,
  - save/load state,
  - generate.
- SQL mode checks:
  - `@Param` without `DECLARE` becomes a SQL parameter,
  - `DECLARE @Local` is not added as a report/dataset parameter,
  - temp-table SQL with one final result `SELECT` can fall back to parser columns,
  - SQL with multiple real top-level result selects warns and does not auto-fill columns,
  - save/load keeps SQL mode,
  - generated RDL uses `CommandType=Text`, SQL in `CommandText`, and query parameters for bindings.

## Assumptions

- SQL mode inspect does not execute user SQL; it uses SQL Server metadata APIs.
- Temp-table result discovery is supported by parser fallback when the final result select is unambiguous.
- Multi-value parameters in raw SQL are the SQL author's responsibility.
- Documentation should be updated after implementation.
