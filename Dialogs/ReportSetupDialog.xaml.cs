using System.Collections.ObjectModel;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using sp2rdlGenExtension.Generation;
using sp2rdlGenExtension.Model;
using sp2rdlGenExtension.Persistence;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

internal sealed record Choice<T>(T Value, string Label);

internal enum MainDatasetSourceMode
{
    StoredProcedure,
    SqlText
}

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
public partial class ReportSetupDialog : Window
{
    private static readonly Regex TemplatePlaceholderRegex = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex HtmlParagraphRegex = new(
        @"<p\b(?<attributes>[^>]*)>(?<content>.*?)</p\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CssTextAlignRegex = new(
        @"text-align\s*:\s*(?<align>left|center|right)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlGoBatchRegex = new(
        @"^\s*GO\s*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private const string ReportingSchemaSqlResourceName = "sp2rdlGenExtension.Database.Reporting_Core_Model.sql";

    private static readonly IReadOnlyList<string> SqlTypeNames =
    [
        "bit",
        "tinyint",
        "smallint",
        "int",
        "bigint",
        "decimal(18,2)",
        "numeric(18,2)",
        "money",
        "float",
        "real",
        "date",
        "datetime",
        "datetime2",
        "time",
        "uniqueidentifier",
        "char",
        "varchar",
        "nvarchar",
        "text",
        "binary",
        "varbinary"
    ];
    private static readonly IReadOnlyList<string> CompareOperators = [">=", "<=", ">", "<", "="];
    private static readonly IReadOnlyList<string> TextAlignOptions = [string.Empty, "Left", "Center", "Right"];
    private static readonly IReadOnlyList<Choice<int>> GroupLevels =
    [
        new(0, string.Empty),
        new(1, "1"),
        new(2, "2"),
        new(3, "3"),
        new(4, "4")
    ];

    private readonly string solutionDirectory;
    private readonly SqlIntrospector sqlIntrospector;
    private readonly ReportOutputWriter outputWriter;
    private readonly ReportValidatorCatalog validatorCatalog;
    private readonly ReportingMetadataReader reportingMetadataReader = new();
    private readonly ReportingMetadataWriter reportingMetadataWriter = new();
    private readonly ObservableCollection<DatasetFieldDraft> fieldDrafts = new();
    private readonly ObservableCollection<ReportParameter> reportParameters = new();
    private readonly ObservableCollection<ReportVariableConfig> reportVariables = new();
    private readonly ObservableCollection<Choice<string>> storedProcedureParameterChoices = new();
    private readonly ObservableCollection<Choice<string>> parameterDefinitionChoices = new();
    private readonly Dictionary<string, ReportingParameterDefinition> parameterDefinitionsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> reportVariablePreviewValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource cts = new();
    private StoredProcedureMetadata? currentMetadata;
    private TextBox? activeReportSummaryTemplateBox;
    private string mainDatasetSqlText = string.Empty;

    internal ReportGenerationRequest Request { get; private set; } = new();

    internal ReportSetupDialog(string solutionDirectory, SqlIntrospector sqlIntrospector, ReportOutputWriter outputWriter)
    {
        this.solutionDirectory = solutionDirectory;
        this.sqlIntrospector = sqlIntrospector;
        this.outputWriter = outputWriter;
        this.validatorCatalog = ReportValidatorCatalog.Load(solutionDirectory);
        InitializeComponent();
        LoadInstalledFonts();
        ColFieldSqlType.ItemsSource = SqlTypeNames;
        ColFieldGroupLevel.ItemsSource = GroupLevels;
        ColFieldTextAlign.ItemsSource = TextAlignOptions;
        ColFieldMatrixRole.ItemsSource = Enum.GetValues(typeof(MatrixFieldRole));
        ColParameterSqlType.ItemsSource = SqlTypeNames;
        ColParameterControlType.ItemsSource = Enum.GetValues(typeof(ControlType));
        ColParameterCompareOperator.ItemsSource = CompareOperators;
        ColParameterBindToSpParam.ItemsSource = this.storedProcedureParameterChoices;
        GridFields.ItemsSource = this.fieldDrafts;
        GridReportParameters.ItemsSource = this.reportParameters;
        GridReportVariables.ItemsSource = this.reportVariables;
        TxtMemorandumTemplate.Text = "<b>{CompanyName}</b>";
        DpReportingVersionValidFrom.SelectedDate = DateTime.Today;
        ApplyTablixStyle(new TablixStyleConfig());
        InitializeTemplateContextMenus();
        UpdateReportSummaryColumnVisibility();
        SetMainDatasetSourceMode(MainDatasetSourceMode.StoredProcedure);
        GridReportParameters.RowEditEnding += GridReportParameters_RowEditEnding;
        GridReportVariables.CurrentCellChanged += GridReportVariables_CurrentCellChanged;
        this.Closed += OnClosed;
    }

    private void GridReportParameters_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.Row.Item is ReportParameter parameter)
        {
            EnsureReportParameterOrdinal(parameter);
        }

        SortReportParametersByOrdinal();
    }

    private void GridReportVariables_CurrentCellChanged(object? sender, EventArgs e)
    {
        RefreshVisibleTemplatePreviews();
    }

    private void LoadInstalledFonts()
    {
        var fontFamilies = Fonts.SystemFontFamilies
            .Select(fontFamily => fontFamily.Source)
            .Where(fontFamily => !string.IsNullOrWhiteSpace(fontFamily))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(fontFamily => fontFamily, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        CmbBaseFont.ItemsSource = fontFamilies;
        CmbTablixFontFamily.ItemsSource = fontFamilies;
        CmbBaseFont.SelectedItem = fontFamilies.FirstOrDefault(fontFamily =>
            string.Equals(fontFamily, "Arial", StringComparison.OrdinalIgnoreCase));
        CmbBaseFont.Text = CmbBaseFont.SelectedItem?.ToString() ?? fontFamilies.FirstOrDefault() ?? "Arial";
        CmbTablixFontFamily.SelectedItem = fontFamilies.FirstOrDefault(fontFamily =>
            string.Equals(fontFamily, "Arial Narrow", StringComparison.OrdinalIgnoreCase));
        CmbTablixFontFamily.Text = CmbTablixFontFamily.SelectedItem?.ToString() ?? "Arial Narrow";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!this.cts.IsCancellationRequested)
        {
            this.cts.Cancel();
        }
        this.cts.Dispose();
    }

    private void ConnectionButton_Click(object sender, RoutedEventArgs e)
        => _ = SelectConnectionAsync();

    private void ReportingConnectionButton_Click(object sender, RoutedEventArgs e)
        => _ = SelectReportingConnectionAsync();

    /// <summary>
    /// Lets the user select a connection and refreshes source metadata when stored procedure mode is active.
    /// </summary>
    private async Task SelectConnectionAsync()
    {
        try
        {
            var dialog = new DatabaseConnectionDialog(this.solutionDirectory, TxtConnectionString.Text.Trim())
            {
                Owner = this,
                ShowActivated = true
            };
            DialogThemeService.ApplyFromOwner(dialog, this);

            dialog.SourceInitialized += (_, _) =>
            {
                dialog.Activate();
                dialog.Topmost = true;
                dialog.Topmost = false;
                dialog.Focus();
            };

            if (dialog.ShowDialog() == true)
            {
                TxtConnectionString.Text = dialog.ConnectionString;
                CmbStoredProcedure.ItemsSource = null;
                GridParameters.ItemsSource = null;
                this.fieldDrafts.Clear();
                this.currentMetadata = null;
                UpdateStoredProcedureParameterChoices([]);
                LblMetadataStatus.Text = string.Empty;

                if (ReadMainDatasetSourceMode() == MainDatasetSourceMode.StoredProcedure)
                {
                    await LoadProceduresAsync();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open connection dialog:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Lets the user select the Reporting database connection used by migration script generation.
    /// </summary>
    private async Task SelectReportingConnectionAsync()
    {
        try
        {
            var dialog = new DatabaseConnectionDialog(this.solutionDirectory, TxtReportingConnectionString.Text.Trim())
            {
                Owner = this,
                ShowActivated = true
            };
            DialogThemeService.ApplyFromOwner(dialog, this);

            dialog.SourceInitialized += (_, _) =>
            {
                dialog.Activate();
                dialog.Topmost = true;
                dialog.Topmost = false;
                dialog.Focus();
            };

            if (dialog.ShowDialog() == true)
            {
                TxtReportingConnectionString.Text = dialog.ConnectionString;
                await TestReportingConnectionAsync(showSuccess: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open reporting connection dialog:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadProceduresButton_Click(object sender, RoutedEventArgs e)
        => _ = LoadProceduresAsync();

    /// <summary>
    /// Updates the Main dataset controls when the source mode changes.
    /// </summary>
    private void MainDatasetSource_Changed(object sender, SelectionChangedEventArgs e)
        => UpdateMainDatasetSourceModeUi();

    /// <summary>
    /// Opens the SQL editor used for the main SQL text dataset command.
    /// </summary>
    private void EditMainSqlButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SqlEditorDialog(
            "Main dataset SQL",
            this.mainDatasetSqlText,
            "Enter the T-SQL command for dsMain. Use @ParameterName for report parameters; locally DECLARE-d variables are ignored during Inspect.")
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            this.mainDatasetSqlText = dialog.SqlText;
            this.currentMetadata = null;
            LblMetadataStatus.Text = string.IsNullOrWhiteSpace(this.mainDatasetSqlText)
                ? "SQL text is empty."
                : "SQL text updated. Run Inspect to refresh params and columns.";
        }
    }

    private async Task LoadProceduresAsync()
    {
        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            LblMetadataStatus.Text = "Loading stored procedures...";
            var connectionString = TxtConnectionString.Text.Trim();
            CmbStoredProcedure.ItemsSource = await this.sqlIntrospector
                .ListStoredProceduresAsync(connectionString, this.cts.Token);
            LblMetadataStatus.Text = "Stored procedures loaded.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load stored procedures:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void InspectProcedureButton_Click(object sender, RoutedEventArgs e)
        => _ = InspectMainDatasetAsync();

    private void SuggestColumnsButton_Click(object sender, RoutedEventArgs e)
        => _ = SuggestColumnsAsync();

    private void SaveStateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save sp2rdl state",
                Filter = "sp2rdl state (*.sp2rdl.json)|*.sp2rdl.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".sp2rdl.json",
                AddExtension = true,
                FileName = BuildDefaultStateFileName()
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            SpRdlJsonStore.Save(dialog.FileName, BuildReportModelFromCurrentState());
            MessageBox.Show(this, "State saved.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save state:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadStateButton_Click(object sender, RoutedEventArgs e)
        => _ = LoadStateAsync();

    private void ReadmeButton_Click(object sender, RoutedEventArgs e)
        => ShowHelpDocument("README.md", "SP to RDL Generator README");

    private void QuickGuideButton_Click(object sender, RoutedEventArgs e)
        => ShowHelpDocument(Path.Combine("docs", "REPORT_DEVELOPER_QUICK_GUIDE.md"), "SP to RDL Generator Quick Guide");

    private void ShowHelpDocument(string relativePath, string title)
    {
        try
        {
            var dialog = new ReadmeDialog(title, LoadHelpDocumentText(relativePath))
            {
                Owner = this
            };
            DialogThemeService.ApplyFromOwner(dialog, this);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open help document:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string LoadHelpDocumentText(string relativePath)
    {
        foreach (var path in GetHelpDocumentCandidatePaths(relativePath))
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        return LoadEmbeddedHelpDocumentText(relativePath)
            ?? $"{relativePath} was not found next to the extension binaries or embedded resources.";
    }

    private static IEnumerable<string> GetHelpDocumentCandidatePaths(string relativePath)
    {
        yield return Path.Combine(AppContext.BaseDirectory, relativePath);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, relativePath);
            directory = directory.Parent;
        }
    }

    private static string? LoadEmbeddedHelpDocumentText(string relativePath)
    {
        var resourceName = relativePath.Replace('\\', '.').Replace('/', '.');
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".Help." + resourceName, StringComparison.OrdinalIgnoreCase));
        if (fullResourceName is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task LoadStateAsync()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load sp2rdl state",
                Filter = "sp2rdl state (*.sp2rdl.json)|*.sp2rdl.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            ApplyReportModel(SpRdlJsonStore.Load(dialog.FileName));
            await RefreshReportVariablePreviewAsync(force: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load state:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var outputMode = ReadOutputMode();
            var extension = outputMode == OutputMode.Rdlc ? ".rdlc" : ".rdl";
            var filter = outputMode == OutputMode.Rdlc
                ? "RDLC report (*.rdlc)|*.rdlc|All files (*.*)|*.*"
                : "RDL report (*.rdl)|*.rdl|All files (*.*)|*.*";

            var dialog = new SaveFileDialog
            {
                Title = "Save generated report",
                Filter = filter,
                DefaultExt = extension,
                AddExtension = true,
                InitialDirectory = Directory.Exists(this.solutionDirectory) ? this.solutionDirectory : null,
                FileName = BuildDefaultReportFileName(extension)
            };

            if (!string.IsNullOrWhiteSpace(TxtOutputPath.Text))
            {
                dialog.FileName = TxtOutputPath.Text.Trim();
            }

            if (dialog.ShowDialog(this) == true)
            {
                TxtOutputPath.Text = dialog.FileName;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not choose output path:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TestReportingConnectionButton_Click(object sender, RoutedEventArgs e)
        => _ = TestReportingConnectionAsync(showSuccess: true);

    private void PreviewReportingMigrationSqlButton_Click(object sender, RoutedEventArgs e)
        => PreviewReportingMigrationSql();

    private void SaveReportingMigrationSqlButton_Click(object sender, RoutedEventArgs e)
        => SaveReportingMigrationSql();

    private void ExecuteReportingMigrationSqlButton_Click(object sender, RoutedEventArgs e)
        => _ = ExecuteReportingMigrationSqlAsync();

    private void PreviewReportingSchemaSqlButton_Click(object sender, RoutedEventArgs e)
        => PreviewReportingSchemaSql();

    private void InstallReportingSchemaButton_Click(object sender, RoutedEventArgs e)
        => _ = InstallReportingSchemaAsync();

    /// <summary>
    /// Opens a folder picker for the Flyway migration folder used by Reporting metadata scripts.
    /// </summary>
    private void BrowseReportingMigrationFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Flyway migration folder",
                Multiselect = false
            };

            if (Directory.Exists(TxtReportingMigrationFolder.Text?.Trim()))
            {
                dialog.FolderName = TxtReportingMigrationFolder.Text.Trim();
            }
            else if (Directory.Exists(this.solutionDirectory))
            {
                dialog.FolderName = this.solutionDirectory;
            }

            if (dialog.ShowDialog(this) == true)
            {
                TxtReportingMigrationFolder.Text = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not choose migration folder:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Verifies that the configured Reporting metadata connection can be opened.
    /// </summary>
    private async Task TestReportingConnectionAsync(bool showSuccess)
    {
        var connectionString = TxtReportingConnectionString.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Reporting connection string is required.", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(this.cts.Token);

            if (showSuccess)
            {
                MessageBox.Show(this, "Reporting connection is valid.", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not connect to Reporting database:\n\n{ex.Message}", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Opens a read/write preview window with the generated Reporting metadata migration SQL.
    /// </summary>
    private void PreviewReportingMigrationSql()
    {
        try
        {
            var sql = BuildReportingMigrationSqlFromCurrentState();
            var dialog = new SqlEditorDialog(
                "Reporting metadata migration SQL",
                sql,
                "Review the generated idempotent SQL before saving it as a Flyway migration. This preview does not execute the script.")
            {
                Owner = this
            };
            DialogThemeService.ApplyFromOwner(dialog, this);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not build reporting migration SQL:\n\n{ex.Message}", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Writes the generated Reporting metadata migration SQL to the selected Flyway folder.
    /// </summary>
    private void SaveReportingMigrationSql()
    {
        try
        {
            var folder = TxtReportingMigrationFolder.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show(this, "Migration folder is required.", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, "Migration folder does not exist.", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var model = BuildReportModelFromCurrentState();
            var sql = ReportingMigrationSqlBuilder.Build(model);
            var fileName = ReportingMigrationSqlBuilder.BuildFileName(model, DateTime.Now);
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, sql, Encoding.UTF8);

            MessageBox.Show(this, $"Reporting migration SQL saved:\n\n{path}", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save reporting migration SQL:\n\n{ex.Message}", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Builds Reporting metadata migration SQL from the current dialog state.
    /// </summary>
    private string BuildReportingMigrationSqlFromCurrentState()
        => ReportingMigrationSqlBuilder.Build(BuildReportModelFromCurrentState());

    /// <summary>
    /// Executes the generated Reporting metadata SQL against the selected development database.
    /// </summary>
    private async Task ExecuteReportingMigrationSqlAsync()
    {
        var connectionString = TxtReportingConnectionString.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Reporting connection string is required.", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(this.cts.Token);

            if (!await ReportingSchemaExistsAsync(connection))
            {
                MessageBox.Show(this, "Reporting schema does not exist in the selected database. Install the schema first or choose another Reporting connection.", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                "This will execute the generated report metadata SQL directly on the selected database. Use this only for development databases. Continue?",
                "Execute Reporting metadata SQL",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var sql = BuildReportingMigrationSqlFromCurrentState();
            var executedBatches = await ExecuteSqlBatchesAsync(connection, sql);
            MessageBox.Show(this, $"Reporting metadata SQL executed successfully.\n\nBatches executed: {executedBatches}", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not execute reporting metadata SQL:\n\n{ex.Message}", "Reporting metadata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Opens the bundled Reporting schema bootstrap script for review without executing it.
    /// </summary>
    private void PreviewReportingSchemaSql()
    {
        try
        {
            var sql = LoadReportingSchemaSql();
            var dialog = new SqlEditorDialog(
                "Reporting schema bootstrap SQL",
                sql,
                "Review the bundled Reporting schema script. The install action can run it only when the Reporting schema does not already exist.")
            {
                Owner = this
            };
            DialogThemeService.ApplyFromOwner(dialog, this);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load Reporting schema SQL:\n\n{ex.Message}", "Reporting schema", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Installs the bundled Reporting schema script only when the target database has no Reporting schema.
    /// </summary>
    private async Task InstallReportingSchemaAsync()
    {
        var connectionString = TxtReportingConnectionString.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Reporting connection string is required.", "Reporting schema", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(this.cts.Token);

            if (await ReportingSchemaExistsAsync(connection))
            {
                MessageBox.Show(this, "Reporting schema already exists. Bootstrap install is allowed only on an empty target database without the Reporting schema.", "Reporting schema", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                "This will create the Reporting schema and its base tables in the selected database. Continue?",
                "Install Reporting schema",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var sql = LoadReportingSchemaSql();
            await ExecuteSqlBatchesAsync(connection, sql);

            MessageBox.Show(this, "Reporting schema installed successfully.", "Reporting schema", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not install Reporting schema:\n\n{ex.Message}", "Reporting schema", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Checks whether the target database already contains the Reporting schema.
    /// </summary>
    private static async Task<bool> ReportingSchemaExistsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN SCHEMA_ID(N'Reporting') IS NULL THEN 0 ELSE 1 END";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Executes a SQL script batch-by-batch using the dialog cancellation token.
    /// </summary>
    private async Task<int> ExecuteSqlBatchesAsync(SqlConnection connection, string sql)
    {
        var executedBatches = 0;
        foreach (var batch in SplitSqlBatches(sql))
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(this.cts.Token);
            executedBatches++;
        }

        return executedBatches;
    }

    /// <summary>
    /// Loads the Reporting schema bootstrap script from copied output files or embedded resources.
    /// </summary>
    private static string LoadReportingSchemaSql()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Database", "Reporting_Core_Model.sql");
        if (File.Exists(outputPath))
        {
            return File.ReadAllText(outputPath, Encoding.UTF8);
        }

        using var stream = typeof(ReportSetupDialog).Assembly.GetManifestResourceStream(ReportingSchemaSqlResourceName);
        if (stream is null)
        {
            throw new FileNotFoundException("Reporting_Core_Model.sql was not found next to the extension binaries or embedded in the extension assembly.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Splits a SQL script into executable batches separated by standalone GO lines.
    /// </summary>
    private static IReadOnlyList<string> SplitSqlBatches(string sql)
    {
        var batches = new List<string>();
        var start = 0;
        foreach (Match match in SqlGoBatchRegex.Matches(sql))
        {
            var batch = sql[start..match.Index].Trim();
            if (!string.IsNullOrWhiteSpace(batch))
            {
                batches.Add(batch);
            }

            start = match.Index + match.Length;
        }

        var lastBatch = sql[start..].Trim();
        if (!string.IsNullOrWhiteSpace(lastBatch))
        {
            batches.Add(lastBatch);
        }

        return batches;
    }

    private void BrowseFooterLogoButton_Click(object sender, RoutedEventArgs e)
        => BrowseImagePath(TxtFooterLogo, "Select footer logo", "Could not choose footer logo");

    private void BrowseMemorandumLogoButton_Click(object sender, RoutedEventArgs e)
        => BrowseImagePath(TxtMemorandumLogo, "Select memorandum logo", "Could not choose memorandum logo");

    private void BrowseMemorandumSubreportButton_Click(object sender, RoutedEventArgs e)
        => BrowseReportDefinitionPath(
            TxtMemorandumSubreport,
            TxtMemorandumSubreportServerPath,
            "Select memorandum subreport",
            "Could not choose memorandum subreport");

    private void BrowseReportSummarySubreportButton_Click(object sender, RoutedEventArgs e)
        => BrowseReportDefinitionPath(
            TxtReportSummarySubreport,
            TxtReportSummarySubreportServerPath,
            "Select report summary subreport",
            "Could not choose report summary subreport");

    private void PickTablixShadeBaseColorButton_Click(object sender, RoutedEventArgs e)
        => PickColorInto(TxtTablixShadeBaseColor);

    private void PickTablixBorderColorButton_Click(object sender, RoutedEventArgs e)
        => PickColorInto(TxtTablixBorderColor);

    private void PickTablixFontColorButton_Click(object sender, RoutedEventArgs e)
        => PickColorInto(TxtTablixFontColor);

    private void PickColorInto(TextBox target)
    {
        var dialog = new ColorPickerDialog(NormalizeHexColor(target.Text, "#000000"))
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);
        if (dialog.ShowDialog() == true)
        {
            target.Text = dialog.SelectedHexColor;
        }
    }

    private void BrowseImagePath(TextBox targetTextBox, string title, string errorMessage)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                targetTextBox.Text = dialog.FileName;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{errorMessage}:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseReportDefinitionPath(TextBox targetTextBox, TextBox? serverPathTextBox, string title, string errorMessage)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "Report definition (*.rdl;*.rdlc)|*.rdl;*.rdlc|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                targetTextBox.Text = dialog.FileName;
                if (serverPathTextBox is not null && string.IsNullOrWhiteSpace(serverPathTextBox.Text))
                {
                    serverPathTextBox.Text = BuildSubreportName(dialog.FileName) ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{errorMessage}:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditDefaultSqlButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if ((sender as FrameworkElement)?.DataContext is not ReportParameter parameter)
        {
            return;
        }

        var dialog = new SqlEditorDialog(
            $"Default SQL - {parameter.Name}",
            parameter.DefaultValueSql,
            "SQL treba vratiti jednu vrijednost. Za konstantu mozes koristiti npr. SELECT 1 ili SELECT GETDATE().",
            previewSqlAsync: sql => PreviewSqlAsync(sql))
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            parameter.DefaultValueSql = NormalizeOptional(dialog.SqlText);
            ResetGeneratedDefaultDatasetReference(parameter);
        }
    }

    private void EditLookupSqlButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if ((sender as FrameworkElement)?.DataContext is not ReportParameter parameter)
        {
            return;
        }

        var dialog = new SqlEditorDialog(
            $"Lookup SQL - {parameter.Name}",
            parameter.LookupSql,
            "SQL treba vratiti value/label kolone za valid values. Primjer: SELECT Id AS Value, Name AS Label FROM dbo.Table ORDER BY Name.",
            suggestSqlAsync: () => SuggestLookupSqlForParameterAsync(parameter),
            previewSqlAsync: sql => PreviewSqlAsync(sql))
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            parameter.LookupSql = NormalizeOptional(dialog.SqlText);
            if (!string.IsNullOrWhiteSpace(parameter.LookupSql))
            {
                parameter.Lookup ??= new LookupConfig
                {
                    DatasetName = BuildLookupDatasetName(parameter.Name)
                };
                parameter.Lookup.ValueField = "Value";
                parameter.Lookup.LabelField = "Label";
            }
        }
    }

    /// <summary>
    /// Applies reusable parameter definitions from the Reporting schema to matching grid rows.
    /// </summary>
    private void ApplyParameterDefinitionsButton_Click(object sender, RoutedEventArgs e)
        => _ = ApplyParameterDefinitionsAsync();

    private void LoadParameterDefinitionsButton_Click(object sender, RoutedEventArgs e)
        => _ = LoadParameterDefinitionsAsync();

    private void SaveParameterDefinitionButton_Click(object sender, RoutedEventArgs e)
        => _ = SaveSelectedParameterDefinitionAsync();

    private void SelectParameterDefinitionButton_Click(object sender, RoutedEventArgs e)
        => _ = SelectParameterDefinitionAsync((sender as FrameworkElement)?.DataContext as ReportParameter);

    /// <summary>
    /// Adds a new report parameter row, assigns the next ordinal, and selects it for immediate editing.
    /// </summary>
    private void AddReportParameterButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        var parameter = new ReportParameter
        {
            OrdinalNumber = GetNextReportParameterOrdinal()
        };
        EnsureRuntimeParameterDefaults(parameter);

        this.reportParameters.Add(parameter);
        GridReportParameters.SelectedItem = parameter;
        GridReportParameters.CurrentItem = parameter;
        GridReportParameters.ScrollIntoView(parameter);
        GridReportParameters.Items.Refresh();
    }

    /// <summary>
    /// Moves the selected report parameter one position up and rewrites ordinals to match the grid order.
    /// </summary>
    private void MoveReportParameterUpButton_Click(object sender, RoutedEventArgs e)
        => MoveSelectedReportParameter(-1);

    /// <summary>
    /// Moves the selected report parameter one position down and rewrites ordinals to match the grid order.
    /// </summary>
    private void MoveReportParameterDownButton_Click(object sender, RoutedEventArgs e)
        => MoveSelectedReportParameter(1);

    /// <summary>
    /// Opens a picker for copying parameters from another report version.
    /// </summary>
    private void LoadReportParametersButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        var connectionString = TxtReportingConnectionString.Text?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Set the Reporting connection on the Output tab first.", "Load params from report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ImportReportParametersDialog(connectionString, this.reportingMetadataReader)
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            ImportReportParameters(dialog.SelectedParameters, dialog.UpdateExistingParameters);
        }
    }

    /// <summary>
    /// Adds selected imported parameters and optionally refreshes matching existing rows.
    /// </summary>
    private void ImportReportParameters(IEnumerable<ReportingParameterImportCandidate> importedParameters, bool updateExisting)
    {
        var selectedParameters = importedParameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .OrderBy(parameter => parameter.CreationOrder <= 0 ? int.MaxValue : parameter.CreationOrder)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedParameters.Count == 0)
        {
            return;
        }

        var existingByName = this.reportParameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .GroupBy(parameter => NormalizeParameterNameForLookup(parameter.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var availableNames = existingByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in selectedParameters)
        {
            availableNames.Add(NormalizeParameterNameForLookup(parameter.Name));
        }

        var addedCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;
        var nextOrdinal = this.reportParameters.Count == 0
            ? 1
            : this.reportParameters.Max(parameter => parameter.OrdinalNumber) + 1;

        foreach (var importedParameter in selectedParameters)
        {
            var key = NormalizeParameterNameForLookup(importedParameter.Name);
            if (existingByName.TryGetValue(key, out var existingParameter))
            {
                if (!updateExisting)
                {
                    skippedCount++;
                    continue;
                }

                ApplyImportedReportParameter(existingParameter, importedParameter, availableNames, keepOrdinal: true);
                updatedCount++;
                continue;
            }

            var newParameter = new ReportParameter
            {
                OrdinalNumber = importedParameter.CreationOrder > 0 ? importedParameter.CreationOrder : nextOrdinal++
            };
            ApplyImportedReportParameter(newParameter, importedParameter, availableNames, keepOrdinal: false);
            this.reportParameters.Add(newParameter);
            existingByName[key] = newParameter;
            addedCount++;
        }

        SortReportParametersByOrdinal();
        UpdateParameterDefinitionChoices([]);
        GridReportParameters.Items.Refresh();
        MessageBox.Show(
            this,
            $"Added {addedCount}, updated {updatedCount}, skipped {skippedCount} existing parameter(s).",
            "Load params from report",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Copies one imported runtime parameter onto a grid parameter.
    /// </summary>
    private static void ApplyImportedReportParameter(
        ReportParameter target,
        ReportingParameterImportCandidate source,
        ISet<string> availableParameterNames,
        bool keepOrdinal)
    {
        if (!keepOrdinal)
        {
            target.OrdinalNumber = source.CreationOrder > 0 ? source.CreationOrder : target.OrdinalNumber;
        }

        target.Name = NormalizeParameterNameForLookup(source.Name);
        target.DefinitionName = NormalizeParameterNameForLookup(source.DefinitionName);
        target.Prompt = string.IsNullOrWhiteSpace(source.Label) ? target.Name : source.Label.Trim();
        target.ControlType = MapReportingComponentType(source.ComponentTypeName);
        target.MultiValue = target.ControlType == ControlType.MultiSelect;
        target.Nullable = target.MultiValue ? false : !source.IsRequired;
        target.IsVisible = source.IsVisible;
        target.EntityKey = NormalizeOptional(source.EntityKey ?? string.Empty);
        target.ValueFieldTemplate = NormalizeOptional(source.ValueFieldTemplate ?? string.Empty);
        target.DisplayFieldTemplate = NormalizeOptional(source.DisplayFieldTemplate ?? string.Empty);
        target.DefaultValueExpression = NormalizeOptional(source.InitialValue ?? string.Empty);
        target.StaticValidValues = CloneStaticValidValues(source.StaticValidValues);
        target.RuntimeSettings = CloneRuntimeSettings(source.RuntimeSettings);

        ApplyImportedDependencies(target, source.Dependencies, availableParameterNames);

        if (string.IsNullOrWhiteSpace(target.SqlTypeName))
        {
            target.SqlTypeName = "nvarchar";
        }
    }

    /// <summary>
    /// Copies import dependency rows into the current grid fields when referenced parameters are available.
    /// </summary>
    private static void ApplyImportedDependencies(
        ReportParameter target,
        IEnumerable<ReportingParameterImportDependency> dependencies,
        ISet<string> availableParameterNames)
    {
        var filterDependencies = new List<string>();
        foreach (var dependency in dependencies)
        {
            var dependsOnName = NormalizeParameterNameForLookup(dependency.DependsOnParameterName);
            if (string.IsNullOrWhiteSpace(dependsOnName) || !availableParameterNames.Contains(dependsOnName))
            {
                continue;
            }

            if (dependency.CompareParams)
            {
                target.CompareToParameterName = dependsOnName;
                target.CompareOperator = NormalizeOptional(dependency.CompareOperator ?? string.Empty);
                target.ComparisonValueTemplate = NormalizeOptional(dependency.ComparisonValueTemplate ?? string.Empty);
                continue;
            }

            filterDependencies.Add(dependsOnName);
            if (string.IsNullOrWhiteSpace(target.DependencyFilterPath))
            {
                target.DependencyFilterPath = NormalizeOptional(dependency.DependencyFilterPath ?? string.Empty);
            }
        }

        target.DependsOnParameterName = filterDependencies.Count == 0 ? null : string.Join(", ", filterDependencies.Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(target.CompareToParameterName))
        {
            target.CompareOperator = null;
        }
    }

    /// <summary>
    /// Clones static values so imported rows do not share mutable list items with the dialog.
    /// </summary>
    private static List<StaticValidValue> CloneStaticValidValues(IEnumerable<StaticValidValue> values)
        => values.Select(value => new StaticValidValue
        {
            Value = value.Value,
            Label = value.Label
        }).ToList();

    /// <summary>
    /// Clones runtime settings so imported rows do not share mutable list items with the dialog.
    /// </summary>
    private static List<ParameterValidatorValue> CloneRuntimeSettings(IEnumerable<ParameterValidatorValue> values)
        => values.Select(value => new ParameterValidatorValue
        {
            Code = value.Code,
            Value = value.Value,
            ValueType = value.ValueType,
            Kind = value.Kind,
            SortOrder = value.SortOrder,
            IsEnabled = value.IsEnabled
        }).ToList();

    /// <summary>
    /// Loads Reporting.ParameterDefinition rows and applies matches to the current report parameters.
    /// </summary>
    private async Task ApplyParameterDefinitionsAsync()
    {
        CommitPendingGridEdits();

        var connectionString = TxtReportingConnectionString.Text?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Set the Reporting connection on the Output tab first.", "Apply definitions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var currentParameters = this.reportParameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .ToList();
        if (currentParameters.Count == 0)
        {
            MessageBox.Show(this, "There are no report parameters to update.", "Apply definitions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var definitions = await ReadReportingParameterDefinitionsAsync();
            UpdateParameterDefinitionChoices(definitions.Values);
            var appliedCount = 0;
            var missingCount = 0;

            foreach (var parameter in currentParameters)
            {
                var key = NormalizeParameterNameForLookup(string.IsNullOrWhiteSpace(parameter.DefinitionName) ? parameter.Name : parameter.DefinitionName);
                if (!definitions.TryGetValue(key, out var definition))
                {
                    missingCount++;
                    continue;
                }

                ApplyReportingParameterDefinition(parameter, definition, initializeName: false);
                appliedCount++;
            }

            GridReportParameters.Items.Refresh();
            MessageBox.Show(
                this,
                $"Applied {appliedCount} parameter definition(s). {missingCount} current parameter(s) were not found in Reporting.ParameterDefinition.",
                "Apply definitions",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Could not read Reporting.ParameterDefinition rows.{Environment.NewLine}{ex.Message}", "Apply definitions", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Loads reusable parameter definitions into the Report params Definition dropdown.
    /// </summary>
    private async Task LoadParameterDefinitionsAsync()
    {
        CommitPendingGridEdits();

        try
        {
            var definitions = await ReadReportingParameterDefinitionsAsync();
            UpdateParameterDefinitionChoices(definitions.Values);
            GridReportParameters.Items.Refresh();
            MessageBox.Show(this, $"Loaded {definitions.Count} parameter definition(s).", "Load definitions", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Could not read Reporting.ParameterDefinition rows.{Environment.NewLine}{ex.Message}", "Load definitions", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Saves the selected report parameter row as a reusable Reporting.ParameterDefinition and refreshes the definition list.
    /// </summary>
    private async Task SaveSelectedParameterDefinitionAsync()
    {
        CommitPendingGridEdits();

        if (GridReportParameters.SelectedItem is not ReportParameter parameter)
        {
            MessageBox.Show(this, "Select one report parameter row first.", "Save definition", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var definitionName = NormalizeParameterNameForLookup(string.IsNullOrWhiteSpace(parameter.DefinitionName) ? parameter.Name : parameter.DefinitionName);
        if (string.IsNullOrWhiteSpace(definitionName))
        {
            MessageBox.Show(this, "Parameter Name or Definition is required.", "Save definition", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var connectionString = TxtReportingConnectionString.Text?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Set the Reporting connection on the Output tab first.", "Save definition", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            parameter.DefinitionName = definitionName;
            await this.reportingMetadataWriter.SaveParameterDefinitionAsync(connectionString, parameter, this.cts.Token);

            var definitions = await ReadReportingParameterDefinitionsAsync();
            UpdateParameterDefinitionChoices(definitions.Values);
            GridReportParameters.Items.Refresh();

            MessageBox.Show(this, $"Saved definition '{definitionName}'. Definitions were reloaded.", "Save definition", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Could not save Reporting.ParameterDefinition.{Environment.NewLine}{ex.Message}", "Save definition", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Opens a modal picker and applies the selected reusable parameter definition to the target row.
    /// </summary>
    private async Task SelectParameterDefinitionAsync(ReportParameter? parameter)
    {
        CommitPendingGridEdits();

        if (parameter is null)
        {
            MessageBox.Show(this, "Select one report parameter row first.", "Select definition", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (this.parameterDefinitionsByName.Count == 0)
        {
            try
            {
                var definitions = await ReadReportingParameterDefinitionsAsync();
                UpdateParameterDefinitionChoices(definitions.Values);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                MessageBox.Show(this, $"Could not read Reporting.ParameterDefinition rows.{Environment.NewLine}{ex.Message}", "Select definition", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        EnsureReportParameterOrdinal(parameter);

        var dialog = new SelectParameterDefinitionDialog(this.parameterDefinitionsByName.Values, parameter.DefinitionName, Resources)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedDefinition is null)
        {
            return;
        }

        ApplyReportingParameterDefinition(parameter, dialog.SelectedDefinition, initializeName: true);
        GridReportParameters.Items.Refresh();
    }

    /// <summary>
    /// Gives a newly added report parameter the next visible ordinal before sorting or applying definitions.
    /// </summary>
    private void EnsureReportParameterOrdinal(ReportParameter parameter)
    {
        if (parameter.OrdinalNumber > 0)
        {
            return;
        }

        parameter.OrdinalNumber = GetNextReportParameterOrdinal(parameter);
    }

    /// <summary>
    /// Calculates the next report-parameter ordinal while ignoring the row currently being initialized.
    /// </summary>
    private int GetNextReportParameterOrdinal(ReportParameter? excludedParameter = null)
    {
        var maxOrdinal = this.reportParameters
            .Where(parameter => !ReferenceEquals(parameter, excludedParameter))
            .Select(parameter => parameter.OrdinalNumber)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(1, maxOrdinal + 1);
    }

    /// <summary>
    /// Reorders the selected report parameter and keeps Ordinal values synchronized with the displayed order.
    /// </summary>
    private void MoveSelectedReportParameter(int direction)
    {
        CommitPendingGridEdits();
        if (GridReportParameters.SelectedItem is not ReportParameter parameter)
        {
            MessageBox.Show(this, "Select one report parameter row first.", "Move parameter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SortReportParametersByOrdinal();
        var currentIndex = this.reportParameters.IndexOf(parameter);
        var targetIndex = currentIndex + Math.Sign(direction);
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= this.reportParameters.Count)
        {
            return;
        }

        if (!CanMoveReportParameter(currentIndex, targetIndex, out var validationMessage))
        {
            MessageBox.Show(this, validationMessage, "Move parameter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        this.reportParameters.Move(currentIndex, targetIndex);
        RenumberReportParameterOrdinals();
        GridReportParameters.SelectedItem = parameter;
        GridReportParameters.CurrentItem = parameter;
        GridReportParameters.ScrollIntoView(parameter);
        GridReportParameters.Items.Refresh();
    }

    /// <summary>
    /// Checks whether moving a parameter would keep dependency and comparison parameters before their consumers.
    /// </summary>
    private bool CanMoveReportParameter(int currentIndex, int targetIndex, out string validationMessage)
    {
        var proposedOrder = this.reportParameters.ToList();
        var parameter = proposedOrder[currentIndex];
        proposedOrder.RemoveAt(currentIndex);
        proposedOrder.Insert(targetIndex, parameter);

        if (TryFindParameterOrderViolation(proposedOrder, out var dependentName, out var dependencyName))
        {
            validationMessage = $"Cannot move '{dependentName}' before '{dependencyName}'. Parameters must stay below the parameters they depend on.";
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// Finds the first dependency ordering violation in a proposed report-parameter order.
    /// </summary>
    private static bool TryFindParameterOrderViolation(
        IReadOnlyList<ReportParameter> proposedOrder,
        out string dependentName,
        out string dependencyName)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < proposedOrder.Count; index++)
        {
            var name = NormalizeParameterNameForLookup(proposedOrder[index].Name);
            if (!string.IsNullOrWhiteSpace(name) && !indexByName.ContainsKey(name))
            {
                indexByName[name] = index;
            }
        }

        for (var index = 0; index < proposedOrder.Count; index++)
        {
            var parameter = proposedOrder[index];
            foreach (var dependency in GetReportParameterDependencyNames(parameter))
            {
                if (indexByName.TryGetValue(dependency, out var dependencyIndex) && dependencyIndex >= index)
                {
                    dependentName = GetDisplayParameterName(parameter);
                    dependencyName = dependency;
                    return true;
                }
            }
        }

        dependentName = string.Empty;
        dependencyName = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns all report parameters that must appear before the supplied parameter.
    /// </summary>
    private static IEnumerable<string> GetReportParameterDependencyNames(ReportParameter parameter)
    {
        foreach (var dependency in ParseDependencyNames(parameter.DependsOnParameterName))
        {
            yield return dependency;
        }

        var compareTo = NormalizeParameterNameForLookup(parameter.CompareToParameterName);
        if (!string.IsNullOrWhiteSpace(compareTo))
        {
            yield return compareTo;
        }
    }

    /// <summary>
    /// Provides a readable parameter name for validation messages.
    /// </summary>
    private static string GetDisplayParameterName(ReportParameter parameter)
        => NormalizeParameterNameForLookup(parameter.Name)
            ?? NormalizeParameterNameForLookup(parameter.DefinitionName)
            ?? "(unnamed parameter)";

    /// <summary>
    /// Rewrites report-parameter ordinals sequentially so the persisted order matches the grid order.
    /// </summary>
    private void RenumberReportParameterOrdinals()
    {
        for (var index = 0; index < this.reportParameters.Count; index++)
        {
            this.reportParameters[index].OrdinalNumber = index + 1;
        }
    }

    /// <summary>
    /// Reads Reporting.ParameterDefinition rows using the Output tab Reporting connection.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ReportingParameterDefinition>> ReadReportingParameterDefinitionsAsync()
    {
        var connectionString = TxtReportingConnectionString.Text?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set the Reporting connection on the Output tab first.");
        }

        return await this.reportingMetadataReader.ReadParameterDefinitionsAsync(connectionString, this.cts.Token);
    }

    /// <summary>
    /// Copies reusable definition metadata onto one report parameter while preserving report-specific settings.
    /// </summary>
    private static void ApplyReportingParameterDefinition(ReportParameter parameter, ReportingParameterDefinition definition, bool initializeName)
    {
        parameter.DefinitionName = NormalizeParameterNameForLookup(definition.Name);
        if (initializeName && string.IsNullOrWhiteSpace(parameter.Name))
        {
            parameter.Name = parameter.DefinitionName;
        }

        parameter.Prompt = string.IsNullOrWhiteSpace(definition.Label) ? parameter.Prompt : definition.Label.Trim();
        parameter.ControlType = MapReportingComponentType(definition.ComponentTypeName);
        parameter.EntityKey = NormalizeOptional(definition.EntityKey ?? string.Empty);
        parameter.ValueFieldTemplate = NormalizeOptional(definition.ValueFieldTemplate ?? string.Empty);
        parameter.DisplayFieldTemplate = NormalizeOptional(definition.DisplayFieldTemplate ?? string.Empty);
        parameter.DefaultValueExpression = NormalizeOptional(definition.InitialValue ?? string.Empty);
    }

    /// <summary>
    /// Refreshes the dropdown choices used to bind report parameters to reusable Reporting definitions.
    /// </summary>
    private void UpdateParameterDefinitionChoices(IEnumerable<ReportingParameterDefinition> definitions)
    {
        this.parameterDefinitionsByName.Clear();
        var selectedValues = this.reportParameters
            .Select(parameter => NormalizeParameterNameForLookup(parameter.DefinitionName))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        this.parameterDefinitionChoices.Clear();
        this.parameterDefinitionChoices.Add(new Choice<string>(string.Empty, string.Empty));

        foreach (var definition in definitions.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase))
        {
            var value = NormalizeParameterNameForLookup(definition.Name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(definition.EntityKey)
                ? value
                : $"{value} | {definition.EntityKey}";
            this.parameterDefinitionChoices.Add(new Choice<string>(value, label));
            this.parameterDefinitionsByName[value] = definition;
            selectedValues.Remove(value);
        }

        foreach (var selectedValue in selectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            this.parameterDefinitionChoices.Add(new Choice<string>(selectedValue, selectedValue));
        }
    }

    /// <summary>
    /// Maps Reporting.ComponentType names back to generator control types.
    /// </summary>
    private static ControlType MapReportingComponentType(string componentTypeName)
        => componentTypeName.Trim() switch
        {
            "Checkbox" => ControlType.Boolean,
            var value when Enum.TryParse<ControlType>(value, ignoreCase: true, out var controlType) => controlType,
            _ => ControlType.Text
        };

    /// <summary>
    /// Normalizes report parameter names before lookup against Reporting.ParameterDefinition.
    /// </summary>
    private static string NormalizeParameterNameForLookup(string? parameterName)
        => string.IsNullOrWhiteSpace(parameterName) ? string.Empty : parameterName.Trim().TrimStart('@');

    private void EditStaticValuesButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if ((sender as FrameworkElement)?.DataContext is not ReportParameter parameter)
        {
            return;
        }

        var dialog = new SqlEditorDialog(
            $"Static values - {parameter.Name}",
            FormatStaticValidValues(parameter.StaticValidValues),
            "Unesi jedan par po redu. Format: Value | Label. Primjer: 1 | OŠ",
            previewSqlAsync: null)
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            parameter.StaticValidValues = ParseStaticValidValues(dialog.SqlText);
            if (parameter.StaticValidValues.Count > 0)
            {
                parameter.Lookup = null;
                parameter.LookupSql = null;
            }

            GridReportParameters.Items.Refresh();
        }
    }

    /// <summary>
    /// Opens the runtime settings editor for the current report parameter.
    /// </summary>
    private void EditValidatorsButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if ((sender as FrameworkElement)?.DataContext is not ReportParameter parameter)
        {
            return;
        }

        var allowedValidators = this.validatorCatalog.GetAllowedValidators(parameter.ControlType);
        if (allowedValidators.Count == 0)
        {
            MessageBox.Show(this, $"No runtime settings are configured for control type '{parameter.ControlType}'.", "Runtime settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ParameterValidatorsDialog(
            string.IsNullOrWhiteSpace(parameter.Name) ? "(new parameter)" : parameter.Name,
            parameter.ControlType,
            allowedValidators,
            parameter.RuntimeSettings)
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            parameter.RuntimeSettings = dialog.RuntimeSettings;
            GridReportParameters.Items.Refresh();
        }
    }

    private void EditReportVariablesSqlButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        var dialog = new SqlEditorDialog(
            "Report variables SQL",
            TxtReportVariablesSql.Text,
            "SQL treba vratiti jednu vrstu. Kolone iz prve vrste mogu se koristiti kao placeholderi, npr. {CompanyName}.",
            previewSqlAsync: sql => PreviewSqlAsync(sql))
        {
            Owner = this
        };
        DialogThemeService.ApplyFromOwner(dialog, this);

        if (dialog.ShowDialog() == true)
        {
            TxtReportVariablesSql.Text = dialog.SqlText.Trim();
            this.reportVariablePreviewValues.Clear();
        }
    }

    private void GenerateReportVariablesButton_Click(object sender, RoutedEventArgs e)
        => _ = GenerateReportVariablesFromSqlAsync();

    private async Task GenerateReportVariablesFromSqlAsync()
    {
        CommitPendingGridEdits();

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            var preview = await PreviewReportVariablesSqlAsync(showValidationMessages: true);
            if (preview is null)
            {
                return;
            }

            var added = MergeReportVariablesFromColumns(preview.Columns);
            UpdateReportVariablePreviewValues(preview);
            RefreshVisibleTemplatePreviews();

            MessageBox.Show(
                this,
                added == 0
                    ? "No new report variables were added. Existing variables were preserved."
                    : $"Report variables generated. Added: {added}.",
                "sp2rdlGenExtension",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not generate report variables from SQL:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private async Task RefreshReportVariablePreviewAsync(bool force = false)
    {
        if (!force && ChkMemorandumPreview.IsChecked != true)
        {
            return;
        }

        try
        {
            var preview = await PreviewReportVariablesSqlAsync(showValidationMessages: false);
            if (preview is not null)
            {
                UpdateReportVariablePreviewValues(preview);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Preview refresh is opportunistic; explicit Generate variables still reports errors.
        }

        RefreshVisibleTemplatePreviews();
    }

    private async Task<DataTable?> PreviewReportVariablesSqlAsync(bool showValidationMessages)
    {
        if (string.IsNullOrWhiteSpace(TxtReportVariablesSql.Text))
        {
            if (showValidationMessages)
            {
                MessageBox.Show(this, "Report variables SQL is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            if (showValidationMessages)
            {
                MessageBox.Show(this, "Connection string is required to inspect report variables SQL.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return null;
        }

        return await this.sqlIntrospector.PreviewSqlAsync(
            TxtConnectionString.Text.Trim(),
            TxtReportVariablesSql.Text.Trim(),
            this.cts.Token);
    }

    private int MergeReportVariablesFromColumns(DataColumnCollection columns)
    {
        var added = 0;
        foreach (DataColumn column in columns)
        {
            var columnName = column.ColumnName?.Trim();
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            var existing = this.reportVariables.FirstOrDefault(variable =>
                string.Equals(variable.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                this.reportVariables.Add(new ReportVariableConfig
                {
                    Enabled = true,
                    Name = columnName,
                    SourceColumnName = columnName
                });
                added++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(existing.SourceColumnName))
            {
                existing.SourceColumnName = columnName;
            }
        }

        return added;
    }

    private void UpdateReportVariablePreviewValues(DataTable preview)
    {
        this.reportVariablePreviewValues.Clear();
        if (preview.Rows.Count == 0)
        {
            return;
        }

        var row = preview.Rows[0];
        foreach (DataColumn column in preview.Columns)
        {
            var columnName = column.ColumnName?.Trim();
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            var value = row[column] is DBNull ? null : Convert.ToString(row[column], CultureInfo.CurrentCulture);
            this.reportVariablePreviewValues[columnName] = value;
        }
    }

    private void RefreshVisibleTemplatePreviews()
    {
        if (ChkMemorandumPreview.IsChecked == true)
        {
            UpdateTemplatePreview(TxtMemorandumTemplate, BrowserMemorandumPreview);
        }

    }

    private void InitializeTemplateContextMenus()
    {
        TxtMemorandumTemplate.ContextMenu = BuildTemplateContextMenu(TxtMemorandumTemplate);
        TxtReportSummaryColumn1Template.ContextMenu = BuildTemplateContextMenu(TxtReportSummaryColumn1Template);
        TxtReportSummaryColumn2Template.ContextMenu = BuildTemplateContextMenu(TxtReportSummaryColumn2Template);
        TxtReportSummaryColumn3Template.ContextMenu = BuildTemplateContextMenu(TxtReportSummaryColumn3Template);
    }

    private void MemorandumBoldButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<b>", "</b>");

    private void MemorandumItalicButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<i>", "</i>");

    private void MemorandumUnderlineButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<u>", "</u>");

    private void MemorandumAlignLeftButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<p style=\"text-align:left;\">", "</p>");

    private void MemorandumAlignCenterButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<p style=\"text-align:center;\">", "</p>");

    private void MemorandumAlignRightButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<p style=\"text-align:right;\">", "</p>");

    private void MemorandumBulletButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<ul><li>", "</li></ul>");

    private void MemorandumNumberButton_Click(object sender, RoutedEventArgs e)
        => WrapMemorandumSelection("<ol><li>", "</li></ol>");

    private void WrapMemorandumSelection(string before, string after)
    {
        ChkMemorandumPreview.IsChecked = false;
        var selected = TxtMemorandumTemplate.SelectedText;
        TxtMemorandumTemplate.SelectedText = before + selected + after;
        TxtMemorandumTemplate.Focus();
    }

    private void InsertMemorandumText(string text)
    {
        ChkMemorandumPreview.IsChecked = false;
        TxtMemorandumTemplate.SelectedText = text;
        TxtMemorandumTemplate.Focus();
    }

    private void MemorandumPreviewCheckBox_Changed(object sender, RoutedEventArgs e)
        => _ = SetTemplatePreviewModeAsync(TxtMemorandumTemplate, BrowserMemorandumPreview, ChkMemorandumPreview.IsChecked == true);

    private void ReportSummaryBoldButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<b>", "</b>");

    private void ReportSummaryItalicButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<i>", "</i>");

    private void ReportSummaryUnderlineButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<u>", "</u>");

    private void ReportSummaryAlignLeftButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<p style=\"text-align:left;\">", "</p>");

    private void ReportSummaryAlignCenterButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<p style=\"text-align:center;\">", "</p>");

    private void ReportSummaryAlignRightButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<p style=\"text-align:right;\">", "</p>");

    private void ReportSummaryBulletButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<ul><li>", "</li></ul>");

    private void ReportSummaryNumberButton_Click(object sender, RoutedEventArgs e)
        => WrapActiveReportSummarySelection("<ol><li>", "</li></ol>");

    private void ReportSummaryLineButton_Click(object sender, RoutedEventArgs e)
        => InsertActiveReportSummaryText("{Line}");

    private void ReportSummaryTemplate_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            this.activeReportSummaryTemplateBox = textBox;
        }
    }

    private TextBox GetActiveReportSummaryTemplateBox()
        => this.activeReportSummaryTemplateBox
            ?? TxtReportSummaryColumn1Template;

    private void WrapActiveReportSummarySelection(string before, string after)
    {
        var editor = GetActiveReportSummaryTemplateBox();
        var selected = editor.SelectedText;
        editor.SelectedText = before + selected + after;
        editor.Focus();
    }

    private void InsertActiveReportSummaryText(string text)
    {
        var editor = GetActiveReportSummaryTemplateBox();
        editor.SelectedText = text;
        editor.Focus();
    }

    private void TemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (ReferenceEquals(sender, TxtMemorandumTemplate) && ChkMemorandumPreview.IsChecked == true)
        {
            UpdateTemplatePreview(TxtMemorandumTemplate, BrowserMemorandumPreview);
        }
    }

    private async Task SetTemplatePreviewModeAsync(TextBox editor, WebBrowser preview, bool enabled)
    {
        if (enabled)
        {
            editor.Visibility = Visibility.Collapsed;
            preview.Visibility = Visibility.Visible;

            // Force a layout pass so the WebBrowser HWND is realized before NavigateToString;
            // without this the very first NavigateToString after visibility flip is a no-op
            // and users had to toggle the preview checkbox twice to see content.
            preview.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            await RefreshReportVariablePreviewAsync(force: true);
            UpdateTemplatePreview(editor, preview);
            return;
        }

        preview.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.Focus();
    }

    private void UpdateTemplatePreview(TextBox editor, WebBrowser preview)
    {
        CommitPendingGridEdits();

        var compactTemplate = PrepareHtmlTemplateForPreview(editor.Text);
        var body = ResolveTemplatePreviewValues(compactTemplate.Html);
        body = body.Replace("\r\n", "<br/>", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(body))
        {
            body = "&nbsp;";
        }

        var html = "<!doctype html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\" />"
            + "<style>"
            + "body{margin:8px;font-family:'Segoe UI',Arial,sans-serif;font-size:12px;line-height:1.15;color:#111;background:#fff;}"
            + "p{margin:0;line-height:1.15;}ul,ol{margin-top:0;margin-bottom:4px;padding-left:22px;}"
            + "</style></head><body>"
            + "<div style=\"text-align:" + compactTemplate.CssTextAlign + ";\">"
            + body
            + "</div>"
            + "</body></html>";

        preview.NavigateToString(html);
    }

    private static HtmlTemplateForPreview PrepareHtmlTemplateForPreview(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return new HtmlTemplateForPreview(string.Empty, "left");
        }

        var matches = HtmlParagraphRegex.Matches(template);
        if (matches.Count == 0)
        {
            return new HtmlTemplateForPreview(template, "left");
        }

        var paragraphAlignments = matches
            .Select(match => ExtractParagraphTextAlign(match.Groups["attributes"].Value))
            .Where(align => !string.IsNullOrWhiteSpace(align))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var textAlign = paragraphAlignments.Count == 1 ? paragraphAlignments[0]!.ToLowerInvariant() : "left";

        var compactHtml = new StringBuilder();
        var currentIndex = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            compactHtml.Append(template, currentIndex, match.Index - currentIndex);
            compactHtml.Append(match.Groups["content"].Value.Trim());
            if (i < matches.Count - 1)
            {
                compactHtml.Append("<br/>");
            }

            currentIndex = match.Index + match.Length;
        }

        compactHtml.Append(template, currentIndex, template.Length - currentIndex);
        return new HtmlTemplateForPreview(compactHtml.ToString(), textAlign);
    }

    private static string? ExtractParagraphTextAlign(string attributes)
    {
        var match = CssTextAlignRegex.Match(attributes);
        return match.Success ? match.Groups["align"].Value : null;
    }

    private sealed record HtmlTemplateForPreview(string Html, string CssTextAlign);

    private string ResolveTemplatePreviewValues(string template)
    {
        var values = BuildTemplatePreviewValues();
        return TemplatePlaceholderRegex.Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (string.Equals(name, "Line", StringComparison.OrdinalIgnoreCase))
            {
                return "<hr style=\"border:0; border-top:1px solid #A6A6A6; margin:4px 0;\"/>";
            }

            return values.TryGetValue(name, out var value)
                ? WebUtility.HtmlEncode(value ?? string.Empty)
                : match.Value;
        });
    }

    private Dictionary<string, string?> BuildTemplatePreviewValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in this.reportVariables.Where(variable => variable.Enabled && !string.IsNullOrWhiteSpace(variable.Name)))
        {
            var sourceValue = !string.IsNullOrWhiteSpace(variable.SourceColumnName)
                && this.reportVariablePreviewValues.TryGetValue(variable.SourceColumnName.Trim(), out var previewValue)
                    ? previewValue
                    : null;
            values[variable.Name.Trim()] = FirstNonBlank(variable.StaticValue, sourceValue, variable.FallbackValue);
        }

        values["CompanyName"] = FirstNonBlank(values.TryGetValue("CompanyName", out var companyValue) ? companyValue : null, TxtCompanyName.Text.Trim());
        values["ReportTitle"] = string.IsNullOrWhiteSpace(TxtReportTitle.Text) ? TxtReportName.Text.Trim() : TxtReportTitle.Text.Trim();

        return values;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private void TemplateTextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        CommitPendingGridEdits();

        if (sender is not TextBox textBox)
        {
            return;
        }

        textBox.ContextMenu = BuildTemplateContextMenu(textBox);
    }

    private ContextMenu BuildTemplateContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();
        var systemPlaceholders = new[] { "CompanyName", "ReportTitle", "Line" };
        var reportVariables = this.reportVariables
            .Where(variable => variable.Enabled && !string.IsNullOrWhiteSpace(variable.Name))
            .Select(variable => variable.Name.Trim())
            .Where(variable => !systemPlaceholders.Contains(variable, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var systemMenu = new MenuItem { Header = "System placeholders" };
        foreach (var placeholder in systemPlaceholders)
        {
            systemMenu.Items.Add(BuildTemplatePlaceholderMenuItem(textBox, placeholder));
        }

        menu.Items.Add(systemMenu);

        var variablesMenu = new MenuItem { Header = "Report variables" };
        if (reportVariables.Count == 0)
        {
            variablesMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        else
        {
            foreach (var variable in reportVariables)
            {
                variablesMenu.Items.Add(BuildTemplatePlaceholderMenuItem(textBox, variable));
            }
        }

        menu.Items.Add(variablesMenu);
        return menu;
    }

    private static MenuItem BuildTemplatePlaceholderMenuItem(TextBox textBox, string placeholder)
    {
        var menuItem = new MenuItem { Header = "{" + placeholder + "}" };
        menuItem.Click += (_, _) =>
        {
            textBox.SelectedText = "{" + placeholder + "}";
            textBox.Focus();
        };
        return menuItem;
    }

    private void DependsOnDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if (sender is not Button button || button.DataContext is not ReportParameter parameter)
        {
            return;
        }

        var availableParameters = GetEarlierParameterNames(parameter);
        var selectedValues = ParseDependencyNames(parameter.DependsOnParameterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextMenu = new ContextMenu();

        foreach (var parameterName in availableParameters.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            var menuItem = new MenuItem
            {
                Header = parameterName,
                IsCheckable = true,
                IsChecked = selectedValues.Contains(parameterName),
                StaysOpenOnClick = true
            };
            menuItem.Click += (_, _) =>
            {
                if (menuItem.IsChecked)
                {
                    selectedValues.Add(parameterName);
                }
                else
                {
                    selectedValues.Remove(parameterName);
                }

                parameter.DependsOnParameterName = selectedValues.Count == 0
                    ? null
                    : string.Join(", ", selectedValues.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));
                GridReportParameters.Items.Refresh();
            };
            contextMenu.Items.Add(menuItem);
        }

        if (contextMenu.Items.Count == 0)
        {
            contextMenu.Items.Add(new MenuItem
            {
                Header = "No other parameters",
                IsEnabled = false
            });
        }

        button.ContextMenu = contextMenu;
        contextMenu.PlacementTarget = button;
        contextMenu.IsOpen = true;
    }

    private void CompareToDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if (sender is not Button button || button.DataContext is not ReportParameter parameter)
        {
            return;
        }

        var contextMenu = new ContextMenu();
        var clearItem = new MenuItem
        {
            Header = "(none)",
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(parameter.CompareToParameterName)
        };
        clearItem.Click += (_, _) =>
        {
            parameter.CompareToParameterName = null;
            parameter.CompareOperator = null;
            GridReportParameters.Items.Refresh();
        };
        contextMenu.Items.Add(clearItem);
        contextMenu.Items.Add(new Separator());

        foreach (var parameterName in GetEarlierParameterNames(parameter))
        {
            var menuItem = new MenuItem
            {
                Header = parameterName,
                IsCheckable = true,
                IsChecked = string.Equals(parameter.CompareToParameterName, parameterName, StringComparison.OrdinalIgnoreCase)
            };
            menuItem.Click += (_, _) =>
            {
                parameter.CompareToParameterName = parameterName;
                parameter.CompareOperator = string.IsNullOrWhiteSpace(parameter.CompareOperator) ? ">=" : parameter.CompareOperator;
                GridReportParameters.Items.Refresh();
            };
            contextMenu.Items.Add(menuItem);
        }

        button.ContextMenu = contextMenu;
        contextMenu.PlacementTarget = button;
        contextMenu.IsOpen = true;
    }

    private async Task<string?> SuggestLookupSqlForParameterAsync(ReportParameter parameter)
    {
        CommitPendingGridEdits();

        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(parameter.Name))
        {
            MessageBox.Show(this, "Parameter name is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var suggestion = await this.sqlIntrospector.SuggestLookupSqlAsync(
            TxtConnectionString.Text.Trim(),
            parameter.Name,
            GetPrimaryDependencyName(parameter.DependsOnParameterName),
            IsMultiValueReportParameter(GetPrimaryDependencyName(parameter.DependsOnParameterName)),
            parameter.Nullable,
            this.cts.Token);

        if (suggestion is null)
        {
            return null;
        }

        parameter.Lookup = new LookupConfig
        {
            DatasetName = suggestion.DatasetName,
            ValueField = suggestion.ValueField,
            LabelField = suggestion.LabelField
        };

        return suggestion.Sql;
    }

    private async Task<System.Collections.IEnumerable?> PreviewSqlAsync(string sql)
        => (await this.sqlIntrospector.PreviewSqlAsync(TxtConnectionString.Text.Trim(), sql, this.cts.Token)).DefaultView;


    /// <summary>
    /// Inspects the selected main dataset source and refreshes dataset params and columns.
    /// </summary>
    private async Task InspectMainDatasetAsync()
    {
        if (ReadMainDatasetSourceMode() == MainDatasetSourceMode.SqlText)
        {
            await InspectSqlTextDatasetAsync();
            return;
        }

        await InspectProcedureAsync();
    }

    /// <summary>
    /// Inspects the selected stored procedure using the existing stored procedure metadata flow.
    /// </summary>
    private async Task InspectProcedureAsync()
    {
        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var storedProcedureName = ReadStoredProcedureName();
        if (string.IsNullOrWhiteSpace(storedProcedureName))
        {
            MessageBox.Show(this, "Select a stored procedure first.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            LblMetadataStatus.Text = "Inspecting stored procedure...";

            var connectionString = TxtConnectionString.Text.Trim();
            this.currentMetadata = await this.sqlIntrospector
                .ReadStoredProcedureAsync(connectionString, storedProcedureName, this.cts.Token);

            GridParameters.ItemsSource = this.currentMetadata.Parameters;
            UpdateStoredProcedureParameterChoices(this.currentMetadata.Parameters);
            this.fieldDrafts.Clear();
            foreach (var field in this.currentMetadata.Fields.Select(DatasetFieldDraft.FromDatasetField))
            {
                this.fieldDrafts.Add(field);
            }

            LblMetadataStatus.Text = this.currentMetadata.ResultSetWarning
                ?? $"Loaded {this.currentMetadata.Parameters.Count} parameter(s) and {this.currentMetadata.Fields.Count} field(s).";

            if (string.IsNullOrWhiteSpace(TxtReportName.Text))
            {
                TxtReportName.Text = this.currentMetadata.ProcedureName;
            }

            ApplyReportParameters(ReportModelFactory.FromStoredProcedure(this.currentMetadata).Parameters);
            AutoBindReportParametersToStoredProcedureParameters(this.currentMetadata.Parameters);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not inspect stored procedure:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    /// <summary>
    /// Inspects raw SQL text and uses metadata/parser results to refresh report parameters and fields.
    /// </summary>
    private async Task InspectSqlTextDatasetAsync()
    {
        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sqlText = ReadMainDatasetSqlText();
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            MessageBox.Show(this, "Enter SQL text first.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            LblMetadataStatus.Text = "Inspecting SQL text...";

            var metadata = await this.sqlIntrospector
                .ReadSqlTextDatasetAsync(TxtConnectionString.Text.Trim(), sqlText, this.cts.Token);
            var sqlParameters = MergeSqlTextParameters(
                metadata.Parameters,
                this.sqlIntrospector.SuggestParametersFromSqlText(sqlText));

            this.currentMetadata = null;
            GridParameters.ItemsSource = sqlParameters;
            UpdateStoredProcedureParameterChoices(sqlParameters);
            this.fieldDrafts.Clear();
            foreach (var field in metadata.Fields.Select(DatasetFieldDraft.FromDatasetField))
            {
                this.fieldDrafts.Add(field);
            }

            LblMetadataStatus.Text = metadata.ResultSetWarning
                ?? $"Loaded {sqlParameters.Count} SQL parameter(s) and {metadata.Fields.Count} field(s).";

            var displayMetadata = metadata with { Parameters = sqlParameters };
            ApplyReportParameters(ReportModelFactory.FromSqlText(TxtReportName.Text, sqlText, displayMetadata).Parameters);
            AutoBindReportParametersToStoredProcedureParameters(sqlParameters);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not inspect SQL text:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    /// <summary>
    /// Merges SQL Server and parser-discovered SQL text parameters for display and binding.
    /// </summary>
    private static IReadOnlyList<SpParameter> MergeSqlTextParameters(
        IReadOnlyList<SpParameter> metadataParameters,
        IReadOnlyList<SpParameter> parserParameters)
    {
        var merged = metadataParameters.ToList();
        var knownNames = merged
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var parserParameter in parserParameters)
        {
            var normalizedName = parserParameter.Name.TrimStart('@');
            if (string.IsNullOrWhiteSpace(normalizedName) || knownNames.Contains(normalizedName))
            {
                continue;
            }

            merged.Add(parserParameter with { OrdinalPosition = merged.Count + 1 });
            knownNames.Add(normalizedName);
        }

        return merged;
    }

    /// <summary>
    /// Suggests main dataset columns using the selected source mode fallback strategy.
    /// </summary>
    private async Task SuggestColumnsAsync()
    {
        if (ReadMainDatasetSourceMode() == MainDatasetSourceMode.SqlText)
        {
            SuggestColumnsFromSqlText();
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var storedProcedureName = ReadStoredProcedureName();
        if (string.IsNullOrWhiteSpace(storedProcedureName))
        {
            MessageBox.Show(this, "Select or enter a stored procedure first.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            LblMetadataStatus.Text = "Suggesting columns from procedure text...";
            var connectionString = TxtConnectionString.Text.Trim();
            var suggestedFields = await this.sqlIntrospector
                .SuggestFieldsFromProcedureTextAsync(connectionString, storedProcedureName, this.cts.Token);

            if (suggestedFields.Count == 0)
            {
                LblMetadataStatus.Text = "No columns could be suggested from the procedure text.";
                return;
            }

            this.fieldDrafts.Clear();
            foreach (var field in suggestedFields.Select(DatasetFieldDraft.FromDatasetField))
            {
                this.fieldDrafts.Add(field);
            }

            LblMetadataStatus.Text = $"Suggested {suggestedFields.Count} column(s) from the last SELECT. Review SQL types before saving.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not suggest columns from procedure text:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    /// <summary>
    /// Suggests columns for SQL text mode without executing the SQL.
    /// </summary>
    private void SuggestColumnsFromSqlText()
    {
        var sqlText = ReadMainDatasetSqlText();
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            MessageBox.Show(this, "Enter SQL text first.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = this.sqlIntrospector.SuggestFieldsFromSqlText(sqlText);
        if (result.Warning is not null)
        {
            LblMetadataStatus.Text = result.Warning;
            return;
        }

        if (result.Fields.Count == 0)
        {
            LblMetadataStatus.Text = "No columns could be suggested from SQL text.";
            return;
        }

        this.fieldDrafts.Clear();
        foreach (var field in result.Fields.Select(DatasetFieldDraft.FromDatasetField))
        {
            this.fieldDrafts.Add(field);
        }

        LblMetadataStatus.Text = $"Suggested {result.Fields.Count} column(s) from SQL text. Review SQL types before saving.";
    }

    /// <summary>
    /// Generates the report from the current dialog state and records the request snapshot.
    /// </summary>
    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateOutputPathBeforeGenerate())
        {
            return;
        }

        var reportModel = BuildReportModelFromCurrentState();
        var outputPath = TxtOutputPath.Text.Trim();
        reportModel.OutputPath = outputPath;

        Request = new ReportGenerationRequest
        {
            ConnectionString = TxtConnectionString.Text.Trim(),
            StoredProcedureName = ReadMainDatasetSourceMode() == MainDatasetSourceMode.StoredProcedure ? ReadStoredProcedureName() : string.Empty,
            OutputPath = outputPath,
            ReportModel = reportModel
        };

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            var result = this.outputWriter.Write(outputPath, reportModel);
            MessageBox.Show(
                this,
                BuildGenerateSuccessMessage(reportModel, result),
                "sp2rdlGenExtension",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not generate report:\n\n{ex.Message}",
                "sp2rdlGenExtension",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private static string BuildGenerateSuccessMessage(ReportModel model, ReportOutputResult result)
    {
        var message = new StringBuilder()
            .AppendLine("Report generated:")
            .AppendLine()
            .AppendLine(result.ReportPath)
            .AppendLine()
            .AppendLine("State JSON:")
            .AppendLine(result.ModelPath);

        if (result.LocalizationSeedPath is not null)
        {
            message
                .AppendLine()
                .AppendLine("Localization seed SQL:")
                .AppendLine(result.LocalizationSeedPath);
        }
        else if (model.Localization.Enabled)
        {
            message
                .AppendLine()
                .AppendLine("Localization seed SQL was not generated. Check that 'Generate seed SQL next to report' is enabled and that the report has labels.");
        }

        return message.ToString();
    }

    private bool ValidateOutputPathBeforeGenerate()
    {
        var outputPath = TxtOutputPath.Text?.Trim() ?? string.Empty;

        string? error = null;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "Output file path is required. Choose the output .rdl/.rdlc location and name on the Output tab.";
        }
        else
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Path.GetFileName(outputPath)))
                {
                    error = "Output file path must include a file name, not only a folder.";
                }
                else if (!Path.IsPathRooted(outputPath))
                {
                    error = "Output file path must be absolute (rooted) so the file lands in a known location.";
                }
            }
            catch (ArgumentException)
            {
                error = "Output file path contains invalid characters.";
            }
        }

        if (error is null)
        {
            return true;
        }

        MainTabs.SelectedItem = OutputTab;
        TxtOutputPath.Focus();
        MessageBox.Show(
            this,
            error,
            "sp2rdlGenExtension",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!this.cts.IsCancellationRequested)
        {
            this.cts.Cancel();
        }
        DialogResult = false;
        Close();
        DebugExpShutdown.TryCloseExpInstance();
    }

    private OutputMode ReadOutputMode()
    {
        var tag = (CmbOutputMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, nameof(OutputMode.Rdlc), StringComparison.OrdinalIgnoreCase)
            ? OutputMode.Rdlc
            : OutputMode.Rdl;
    }

    private ReportPurpose ReadReportPurpose()
    {
        var tag = (CmbReportPurpose.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<ReportPurpose>(tag, ignoreCase: true, out var purpose)
            ? purpose
            : ReportPurpose.MainReport;
    }

    /// <summary>
    /// Reads the selected main dataset source mode from the dialog.
    /// </summary>
    private MainDatasetSourceMode ReadMainDatasetSourceMode()
    {
        var tag = (CmbMainDatasetSource.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, nameof(CommandKind.Text), StringComparison.OrdinalIgnoreCase)
            ? MainDatasetSourceMode.SqlText
            : MainDatasetSourceMode.StoredProcedure;
    }

    /// <summary>
    /// Selects the requested main dataset source mode in the dialog.
    /// </summary>
    private void SetMainDatasetSourceMode(MainDatasetSourceMode mode)
    {
        var tag = mode == MainDatasetSourceMode.SqlText
            ? nameof(CommandKind.Text)
            : nameof(CommandKind.StoredProcedure);
        SetComboBoxByTag(CmbMainDatasetSource, tag);
        UpdateMainDatasetSourceModeUi();
    }

    /// <summary>
    /// Shows only the controls that belong to the selected main dataset source mode.
    /// </summary>
    private void UpdateMainDatasetSourceModeUi()
    {
        if (CmbMainDatasetSource is null || BtnEditMainSql is null)
        {
            return;
        }

        var sqlMode = ReadMainDatasetSourceMode() == MainDatasetSourceMode.SqlText;
        LblStoredProcedure.Text = sqlMode ? "SQL text:" : "Procedure:";
        CmbStoredProcedure.Visibility = sqlMode ? Visibility.Collapsed : Visibility.Visible;
        BtnLoadProcedures.Visibility = sqlMode ? Visibility.Collapsed : Visibility.Visible;
        BtnEditMainSql.Visibility = sqlMode ? Visibility.Visible : Visibility.Collapsed;
        LblDatasetParametersHeader.Text = sqlMode ? "SQL params" : "Stored procedure params";
        LblDatasetColumnsHeader.Text = sqlMode ? "SQL columns" : "Stored procedure columns";
    }

    /// <summary>
    /// Returns the SQL text currently configured for the main dataset.
    /// </summary>
    private string ReadMainDatasetSqlText()
        => this.mainDatasetSqlText.Trim();

    /// <summary>
    /// Builds stored procedure metadata from edited fields when the dialog is in stored procedure mode.
    /// </summary>
    private StoredProcedureMetadata? BuildCurrentMetadata()
    {
        if (ReadMainDatasetSourceMode() == MainDatasetSourceMode.SqlText || this.currentMetadata is null)
        {
            return null;
        }

        var fields = this.fieldDrafts
            .Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .Select((field, index) =>
            {
                field.OrdinalPosition = field.OrdinalPosition <= 0 ? index + 1 : field.OrdinalPosition;
                return field.ToDatasetField();
            })
            .ToList();

        return this.currentMetadata with { Fields = fields };
    }

    /// <summary>
    /// Builds the persisted report model from the current UI, branching by stored procedure or SQL text mode.
    /// </summary>
    private ReportModel BuildReportModelFromCurrentState()
    {
        // This method is the single UI -> model boundary. Keeping persistence
        // mapping here makes Save state, Generate, and future automation use the
        // same configuration snapshot.
        CommitPendingGridEdits();

        var metadata = BuildCurrentMetadata();
        var reportModel = metadata is null
            ? BuildReportModelWithoutMetadata()
            : ReportModelFactory.FromStoredProcedure(metadata);

        reportModel.Name = TxtReportName.Text.Trim();
        reportModel.Author = NormalizeOptional(TxtAuthor.Text);
        reportModel.Description = NormalizeOptional(TxtDescription.Text);
        reportModel.OutputMode = ReadOutputMode();
        reportModel.Purpose = ReadReportPurpose();
        reportModel.SourceConnectionString = NormalizeOptional(TxtConnectionString.Text);
        reportModel.SourceStoredProcedureName = ReadMainDatasetSourceMode() == MainDatasetSourceMode.StoredProcedure
            ? ReadStoredProcedureName()
            : null;
        reportModel.OutputPath = NormalizeOptional(TxtOutputPath.Text);
        reportModel.ReportingConnectionString = NormalizeOptional(TxtReportingConnectionString.Text);
        reportModel.ReportingVersionValidFrom = DpReportingVersionValidFrom.SelectedDate?.Date;
        reportModel.ReportingMigrationFolder = NormalizeOptional(TxtReportingMigrationFolder.Text);
        reportModel.BaseFontFamily = ReadBaseFontFamily();
        reportModel.ReportTitle.Enabled = ChkReportTitleEnabled.IsChecked == true;
        reportModel.ReportTitle.Text = string.IsNullOrWhiteSpace(TxtReportTitle.Text)
            ? reportModel.Name
            : TxtReportTitle.Text.Trim();
        reportModel.ReportTitle.ShowInPageHeaderAfterFirstPage = false;
        reportModel.CompanyInfo.Text = TxtCompanyName.Text.Trim();
        reportModel.CompanyInfo.SqlExpression = NormalizeOptional(TxtCompanySql.Text);
        reportModel.CompanyInfo.BackendEndpoint = NormalizeOptional(TxtCompanyEndpoint.Text);
        reportModel.ReportVariables.DynamicSource.Enabled = !string.IsNullOrWhiteSpace(TxtReportVariablesSql.Text);
        reportModel.ReportVariables.DynamicSource.SqlExpression = TxtReportVariablesSql.Text.Trim();
        reportModel.ReportVariables.Items = BuildReportVariablesFromGrid();
        reportModel.Localization = BuildLocalizationConfig();
        reportModel.TablixStyle = BuildTablixStyle();
        UpsertReportVariable(reportModel.ReportVariables, "CompanyName", "CompanyName", reportModel.CompanyInfo.Text);
        reportModel.Memorandum.Enabled = ChkMemorandumEnabled.IsChecked == true;
        reportModel.Memorandum.LayoutMode = ReadReportBandLayoutMode(CmbMemorandumLayoutMode);
        reportModel.Memorandum.SubreportPath = NormalizeOptional(TxtMemorandumSubreport.Text);
        reportModel.Memorandum.SubreportName = BuildSubreportName(reportModel.Memorandum.SubreportPath);
        reportModel.Memorandum.SubreportServerPath = NormalizeOptional(TxtMemorandumSubreportServerPath.Text);
        reportModel.Memorandum.FallbackToInline = ChkMemorandumFallbackInline.IsChecked == true;
        reportModel.Memorandum.RichTextParagraphs.Clear();
        reportModel.Memorandum.TextTemplate = TxtMemorandumTemplate.Text.Trim();
        reportModel.Memorandum.LogoImagePath = NormalizeOptional(TxtMemorandumLogo.Text);
        reportModel.Memorandum.ShowVerticalSeparator = ChkMemorandumVerticalLine.IsChecked == true;
        reportModel.Memorandum.ShowLogoBottomLine = ChkMemorandumLogoBottomLine.IsChecked == true;
        reportModel.Memorandum.ShowBottomLine = ChkMemorandumBottomLine.IsChecked == true;
        reportModel.Memorandum.HeightInCentimeters = ReadPositiveDouble(TxtMemorandumHeight.Text, reportModel.Memorandum.HeightInCentimeters);
        reportModel.Memorandum.LogoAlignment = ReadEnumComboBox(CmbMemorandumLogoAlignment, MemorandumLogoAlignment.Left);
        reportModel.Memorandum.TextPlacement = ReadEnumComboBox(CmbMemorandumTextPlacement, MemorandumTextPlacement.BesideLogo);
        reportModel.ReportSummary.Enabled = ChkReportSummaryEnabled.IsChecked == true;
        reportModel.ReportSummary.LayoutMode = ReadReportBandLayoutMode(CmbReportSummaryLayoutMode);
        reportModel.ReportSummary.SubreportPath = NormalizeOptional(TxtReportSummarySubreport.Text);
        reportModel.ReportSummary.SubreportName = BuildSubreportName(reportModel.ReportSummary.SubreportPath);
        reportModel.ReportSummary.SubreportServerPath = NormalizeOptional(TxtReportSummarySubreportServerPath.Text);
        reportModel.ReportSummary.FallbackToInline = ChkReportSummaryFallbackInline.IsChecked == true;
        reportModel.ReportSummary.ShowTopLine = ChkReportSummaryTopLine.IsChecked == true;
        reportModel.ReportSummary.ColumnCount = ReadReportSummaryColumnCount();
        reportModel.ReportSummary.Columns = BuildReportSummaryColumnsFromUi(reportModel.ReportSummary.ColumnCount);
        reportModel.ReportSummary.HeightInCentimeters = ReadPositiveDouble(TxtReportSummaryHeight.Text, reportModel.ReportSummary.HeightInCentimeters);
        reportModel.PageSetup = BuildPageSetup();
        reportModel.PageHeader.Enabled = ChkPageHeaderEnabled.IsChecked == true;
        reportModel.PageHeader.LeftText = TxtHeaderLeft.Text.Trim();
        reportModel.PageHeader.RightText = TxtHeaderRight.Text.Trim();
        reportModel.PageHeader.HeightInCentimeters = ReadPositiveDouble(TxtPageHeaderHeight.Text, reportModel.PageHeader.HeightInCentimeters);
        reportModel.PageHeader.FontSizeInPoints = ReadPositiveDouble(TxtPageHeaderFontSize.Text, reportModel.PageHeader.FontSizeInPoints);
        reportModel.PageHeader.PrintOnFirstPage = ChkPageHeaderFirstPage.IsChecked == true;
        reportModel.PageFooter.Enabled = ChkPageFooterEnabled.IsChecked == true;
        reportModel.PageFooter.LeftText = TxtFooterLeft.Text.Trim();
        reportModel.PageFooter.RightText = TxtFooterRight.Text.Trim();
        reportModel.PageFooter.LogoImagePath = NormalizeOptional(TxtFooterLogo.Text);
        reportModel.PageFooter.ShowPageNumber = ChkPageFooterNumber.IsChecked == true;
        reportModel.PageFooter.ShowTopLine = ChkPageFooterTopLine.IsChecked == true;
        reportModel.PageFooter.DisplayMode = ReadPageFooterDisplayMode();
        reportModel.PageFooter.HeightInCentimeters = ReadPositiveDouble(TxtPageFooterHeight.Text, reportModel.PageFooter.HeightInCentimeters);
        ApplyPageFooterDisplayModeFlags(reportModel.PageFooter);
        reportModel.PageFooter.PrintOnLastPage = ChkPageFooterLastPage.IsChecked == true;
        if (reportModel.Purpose != ReportPurpose.MainReport)
        {
            reportModel.PageHeader.Enabled = false;
            reportModel.PageFooter.Enabled = false;
        }
        reportModel.Parameters = BuildReportParametersFromGrid();
        NormalizeReportParameters(reportModel.Parameters);
        if (metadata is not null)
        {
            AutoBindReportParametersToStoredProcedureParameters(reportModel.Parameters, metadata.Parameters);
        }

        ApplyAuxiliaryParameterDatasets(reportModel);
        ApplyDatasetParameterBindings(reportModel);

        return reportModel;
    }

    /// <summary>
    /// Builds a minimal report model when no fresh dataset metadata is available.
    /// </summary>
    private ReportModel BuildReportModelWithoutMetadata()
    {
        var sourceMode = ReadMainDatasetSourceMode();
        var command = sourceMode == MainDatasetSourceMode.SqlText
            ? ReadMainDatasetSqlText()
            : ReadStoredProcedureName();
        var fields = this.fieldDrafts
            .Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .Select((field, index) =>
            {
                field.OrdinalPosition = field.OrdinalPosition <= 0 ? index + 1 : field.OrdinalPosition;
                return field.ToDatasetField();
            })
            .ToList();

        var dataset = new DatasetConfig
        {
            Name = "dsMain",
            Command = command,
            CommandKind = sourceMode == MainDatasetSourceMode.SqlText ? CommandKind.Text : CommandKind.StoredProcedure,
            Fields = fields
        };

        return new ReportModel
        {
            Name = TxtReportName.Text.Trim(),
            MainDatasetName = dataset.Name,
            Datasets = string.IsNullOrWhiteSpace(command) && fields.Count == 0 ? [] : [dataset],
            Parameters = BuildReportParametersFromGrid()
        };
    }

    /// <summary>
    /// Applies a saved report model to the dialog and restores the correct main dataset source mode.
    /// </summary>
    private void ApplyReportModel(ReportModel model)
    {
        // This is the inverse model -> UI boundary used by Load state and by the
        // initial stored procedure inspection result.
        TxtReportName.Text = model.Name;
        TxtAuthor.Text = model.Author ?? string.Empty;
        TxtDescription.Text = model.Description ?? string.Empty;
        TxtConnectionString.Text = model.SourceConnectionString ?? string.Empty;
        TxtOutputPath.Text = model.OutputPath ?? string.Empty;
        TxtReportingConnectionString.Text = model.ReportingConnectionString ?? string.Empty;
        DpReportingVersionValidFrom.SelectedDate = model.ReportingVersionValidFrom?.Date ?? DateTime.Today;
        TxtReportingMigrationFolder.Text = model.ReportingMigrationFolder ?? string.Empty;
        TxtReportTitle.Text = model.ReportTitle.Text;
        ChkReportTitleEnabled.IsChecked = model.ReportTitle.Enabled;
        TxtCompanyName.Text = model.CompanyInfo.Text;
        TxtCompanySql.Text = model.CompanyInfo.SqlExpression ?? string.Empty;
        TxtCompanyEndpoint.Text = model.CompanyInfo.BackendEndpoint ?? string.Empty;
        TxtReportVariablesSql.Text = model.ReportVariables.DynamicSource.SqlExpression;
        ApplyReportVariables(model.ReportVariables.Items);
        ApplyLocalizationConfig(model.Localization ?? new LocalizationConfig());
        ApplyTablixStyle(model.TablixStyle ?? new TablixStyleConfig());
        ChkMemorandumEnabled.IsChecked = model.Memorandum.Enabled;
        SetReportBandLayoutMode(CmbMemorandumLayoutMode, model.Memorandum.LayoutMode);
        TxtMemorandumSubreport.Text = model.Memorandum.SubreportPath ?? model.Memorandum.SubreportName ?? string.Empty;
        TxtMemorandumSubreportServerPath.Text = model.Memorandum.SubreportServerPath ?? model.Memorandum.SubreportName ?? string.Empty;
        ChkMemorandumFallbackInline.IsChecked = model.Memorandum.FallbackToInline;
        TxtMemorandumTemplate.Text = string.IsNullOrWhiteSpace(model.Memorandum.TextTemplate)
            ? "<b>{CompanyName}</b>"
            : model.Memorandum.TextTemplate;
        TxtMemorandumLogo.Text = model.Memorandum.LogoImagePath ?? string.Empty;
        ChkMemorandumVerticalLine.IsChecked = model.Memorandum.ShowVerticalSeparator;
        ChkMemorandumLogoBottomLine.IsChecked = model.Memorandum.ShowLogoBottomLine;
        ChkMemorandumBottomLine.IsChecked = model.Memorandum.ShowBottomLine;
        TxtMemorandumHeight.Text = ToUiNumber(model.Memorandum.HeightInCentimeters);
        SetEnumComboBox(CmbMemorandumLogoAlignment, model.Memorandum.LogoAlignment);
        SetEnumComboBox(CmbMemorandumTextPlacement, model.Memorandum.TextPlacement);
        UpdateMemorandumLogoAlignmentAvailability();
        ChkReportSummaryEnabled.IsChecked = model.ReportSummary.Enabled;
        SetReportBandLayoutMode(CmbReportSummaryLayoutMode, model.ReportSummary.LayoutMode);
        TxtReportSummarySubreport.Text = model.ReportSummary.SubreportPath ?? model.ReportSummary.SubreportName ?? string.Empty;
        TxtReportSummarySubreportServerPath.Text = model.ReportSummary.SubreportServerPath ?? model.ReportSummary.SubreportName ?? string.Empty;
        ChkReportSummaryFallbackInline.IsChecked = model.ReportSummary.FallbackToInline;
        ChkReportSummaryTopLine.IsChecked = model.ReportSummary.ShowTopLine;
        SetReportSummaryColumns(model.ReportSummary);
        TxtReportSummaryHeight.Text = ToUiNumber(model.ReportSummary.HeightInCentimeters);
        ChkPageHeaderEnabled.IsChecked = model.PageHeader.Enabled;
        TxtHeaderLeft.Text = model.PageHeader.LeftText;
        TxtHeaderRight.Text = model.PageHeader.RightText;
        TxtPageHeaderHeight.Text = ToUiNumber(model.PageHeader.HeightInCentimeters);
        TxtPageHeaderFontSize.Text = ToUiNumber(model.PageHeader.FontSizeInPoints);
        ChkPageHeaderFirstPage.IsChecked = model.PageHeader.PrintOnFirstPage;
        ChkPageFooterEnabled.IsChecked = model.PageFooter.Enabled;
        TxtFooterLeft.Text = model.PageFooter.LeftText;
        TxtFooterRight.Text = model.PageFooter.RightText;
        TxtFooterLogo.Text = model.PageFooter.LogoImagePath ?? string.Empty;
        ChkPageFooterNumber.IsChecked = model.PageFooter.ShowPageNumber;
        ChkPageFooterTopLine.IsChecked = model.PageFooter.ShowTopLine;
        SetPageFooterDisplayMode(ResolvePageFooterDisplayMode(model.PageFooter));
        TxtPageFooterHeight.Text = ToUiNumber(model.PageFooter.HeightInCentimeters);
        ChkPageFooterLastPage.IsChecked = model.PageFooter.PrintOnLastPage;
        SetBaseFontFamily(model.BaseFontFamily);
        SetOutputMode(model.OutputMode);
        SetReportPurpose(model.Purpose);
        ApplyPageSetup(model.PageSetup);

        var mainDataset = model.Datasets.FirstOrDefault(dataset => dataset.Name == model.MainDatasetName)
            ?? model.Datasets.FirstOrDefault();
        var sqlMode = mainDataset?.CommandKind == CommandKind.Text;
        SetMainDatasetSourceMode(sqlMode ? MainDatasetSourceMode.SqlText : MainDatasetSourceMode.StoredProcedure);
        CmbStoredProcedure.ItemsSource = null;
        if (sqlMode)
        {
            this.mainDatasetSqlText = mainDataset?.Command ?? string.Empty;
            CmbStoredProcedure.Text = string.Empty;
        }
        else
        {
            this.mainDatasetSqlText = string.Empty;
            CmbStoredProcedure.Text = model.SourceStoredProcedureName
                ?? mainDataset?.Command
                ?? string.Empty;
        }

        this.fieldDrafts.Clear();
        if (mainDataset is not null)
        {
            foreach (var field in mainDataset.Fields.Select(DatasetFieldDraft.FromDatasetField))
            {
                this.fieldDrafts.Add(field);
            }
        }

        ApplyReportParameters(model.Parameters);
        var spParameters = BuildStoredProcedureParametersFromModel(model, mainDataset);
        GridParameters.ItemsSource = spParameters;

        this.currentMetadata = mainDataset is null || sqlMode
            ? null
            : new StoredProcedureMetadata(
                ParseSchemaName(CmbStoredProcedure.Text),
                ParseProcedureName(CmbStoredProcedure.Text),
                spParameters,
                mainDataset.Fields);
        UpdateStoredProcedureParameterChoices(spParameters);

        LblMetadataStatus.Text = "State loaded.";
    }

    private static IReadOnlyList<SpParameter> BuildStoredProcedureParametersFromModel(ReportModel model, DatasetConfig? mainDataset)
    {
        if (mainDataset is null)
        {
            return [];
        }

        var parametersByName = model.Parameters
            .ToDictionary(parameter => parameter.Name.TrimStart('@'), StringComparer.OrdinalIgnoreCase);

        return mainDataset.ParameterBindings
            .Select((binding, index) =>
            {
                var reportParameterName = binding.ReportParameterName.TrimStart('@');
                parametersByName.TryGetValue(reportParameterName, out var reportParameter);

                return new SpParameter(
                    EnsureAtPrefixLocal(binding.DatasetParameterName),
                    reportParameter?.SqlTypeName ?? "nvarchar",
                    reportParameter?.Nullable ?? true,
                    !string.IsNullOrWhiteSpace(reportParameter?.DefaultValueExpression)
                        || !string.IsNullOrWhiteSpace(reportParameter?.DefaultValueSql),
                    false,
                    index + 1);
            })
            .ToList();
    }

    private static string EnsureAtPrefixLocal(string value)
        => value.StartsWith('@') ? value : "@" + value;

    private static void UpsertReportVariable(
        ReportVariablesConfig variables,
        string name,
        string sourceColumnName,
        string fallbackValue)
    {
        var item = variables.Items.FirstOrDefault(variable =>
            string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ReportVariableConfig { Name = name };
            variables.Items.Add(item);
        }

        if (string.IsNullOrWhiteSpace(item.SourceColumnName))
        {
            item.SourceColumnName = sourceColumnName;
        }

        if (string.IsNullOrWhiteSpace(item.FallbackValue))
        {
            item.FallbackValue = fallbackValue;
        }

        if (string.IsNullOrWhiteSpace(item.StaticValue) && string.IsNullOrWhiteSpace(sourceColumnName))
        {
            item.StaticValue = fallbackValue;
        }
    }

    private static PageFooterDisplayMode ResolvePageFooterDisplayMode(PageFooterConfig footer)
        => (footer.PrintOnFirstPage, footer.PrintOnLastPage) switch
        {
            (true, true) => footer.DisplayMode,
            (true, false) => PageFooterDisplayMode.FirstPageOnly,
            (false, true) => PageFooterDisplayMode.AllExceptFirstPage,
            _ => PageFooterDisplayMode.AllPages
        };

    private PageFooterDisplayMode ReadPageFooterDisplayMode()
    {
        var tag = (CmbPageFooterDisplayMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<PageFooterDisplayMode>(tag, ignoreCase: true, out var mode)
            ? mode
            : PageFooterDisplayMode.AllPages;
    }

    private void SetPageFooterDisplayMode(PageFooterDisplayMode mode)
    {
        foreach (var item in CmbPageFooterDisplayMode.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CmbPageFooterDisplayMode.SelectedItem = item;
                return;
            }
        }

        CmbPageFooterDisplayMode.SelectedIndex = 0;
    }

    private static void ApplyPageFooterDisplayModeFlags(PageFooterConfig footer)
    {
        (footer.PrintOnFirstPage, footer.PrintOnLastPage) = footer.DisplayMode switch
        {
            PageFooterDisplayMode.FirstPageOnly => (true, false),
            PageFooterDisplayMode.LastPageOnly => (false, true),
            PageFooterDisplayMode.AllExceptFirstPage => (false, true),
            _ => (true, true)
        };
    }

    private static ReportBandLayoutMode ReadReportBandLayoutMode(ComboBox comboBox)
    {
        var tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<ReportBandLayoutMode>(tag, ignoreCase: true, out var mode)
            ? mode
            : ReportBandLayoutMode.Inline;
    }

    private int ReadReportSummaryColumnCount()
    {
        var tag = (CmbReportSummaryColumnCount.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(tag, out var count) ? Math.Clamp(count, 1, 3) : 1;
    }

    /// <summary>
    /// Reads tablix layout/style options from the Body / Tablix tab.
    /// </summary>
    private TablixStyleConfig BuildTablixStyle()
        => new()
        {
            GroupRenderMode = ReadEnumComboBox(CmbGroupRenderMode, GroupRenderMode.Band),
            MatrixExpectedColumnCount = Math.Clamp(ReadNonNegativeInt(TxtMatrixExpectedColumnCount.Text, 6), 1, 50),
            ShowTabularHorizontalSubtotals = ChkTabularHorizontalSubtotals.IsChecked == true,
            ShowTabularHorizontalGrandTotal = ChkTabularHorizontalGrandTotal.IsChecked == true,
            WidthPercent = ReadPercentOrDefault(TxtTablixWidthPercent.Text, 100.0d),
            ShadeBaseColor = NormalizeHexColor(TxtTablixShadeBaseColor.Text, "#EDEDED"),
            BorderColor = NormalizeHexColor(TxtTablixBorderColor.Text, "#A6A6A6"),
            BorderWidthInPoints = ReadPositiveDouble(TxtTablixBorderWidth.Text, 0.5d),
            FontFamily = string.IsNullOrWhiteSpace(CmbTablixFontFamily.Text) ? "Arial Narrow" : CmbTablixFontFamily.Text.Trim(),
            FontColor = NormalizeHexColor(TxtTablixFontColor.Text, "#000000"),
            FontSizeInPoints = ReadPositiveDouble(TxtTablixFontSize.Text, 9.0d)
        };

    private LocalizationConfig BuildLocalizationConfig()
        => new()
        {
            Enabled = ChkLocalizationEnabled.IsChecked == true,
            ReportId = ReadNonNegativeInt(TxtLocalizationReportId.Text, 0),
            DefaultLanguageId = ReadNonNegativeInt(TxtLocalizationDefaultLanguageId.Text, 3),
            GeneralReportId = ReadNonNegativeInt(TxtLocalizationGeneralReportId.Text, 0),
            AccessMode = LocalizationAccessMode.Table,
            TranslationTable = new TranslationObjectReference
            {
                Schema = TxtLocalizationTableSchema.Text.Trim(),
                Name = TxtLocalizationTableName.Text.Trim()
            },
            ReportTable = new TranslationObjectReference
            {
                Schema = TxtLocalizationReportRegistrySchema.Text.Trim(),
                Name = TxtLocalizationReportRegistryName.Text.Trim()
            },
            GenerateDefaultLanguageSeed = ChkLocalizationGenerateSeed.IsChecked == true,
            SkipKeysFromGeneralReport = ChkLocalizationSkipGeneralKeys.IsChecked == true,
            GenerateLanguageTemplatesFor = ParseIntegerList(TxtLocalizationTemplateLanguages.Text),
            LanguageTemplateValueMode = ReadEnumComboBox(CmbLocalizationTemplateMode, LanguageTemplateValueMode.CopyDefault)
        };

    private void ApplyLocalizationConfig(LocalizationConfig config)
    {
        ChkLocalizationEnabled.IsChecked = config.Enabled;
        TxtLocalizationReportId.Text = config.ReportId <= 0 ? string.Empty : config.ReportId.ToString(CultureInfo.CurrentCulture);
        TxtLocalizationDefaultLanguageId.Text = (config.DefaultLanguageId <= 0 ? 3 : config.DefaultLanguageId).ToString(CultureInfo.CurrentCulture);
        TxtLocalizationGeneralReportId.Text = config.GeneralReportId <= 0 ? "0" : config.GeneralReportId.ToString(CultureInfo.CurrentCulture);
        // Show exactly what was saved. Defaults from LocalizationConfig constructor only
        // appear for new (never-saved) configs.
        TxtLocalizationTableSchema.Text = config.TranslationTable.Schema ?? string.Empty;
        TxtLocalizationTableName.Text = config.TranslationTable.Name ?? string.Empty;
        TxtLocalizationReportRegistrySchema.Text = config.ReportTable.Schema ?? string.Empty;
        TxtLocalizationReportRegistryName.Text = config.ReportTable.Name ?? string.Empty;
        ChkLocalizationGenerateSeed.IsChecked = config.GenerateDefaultLanguageSeed;
        ChkLocalizationSkipGeneralKeys.IsChecked = config.SkipKeysFromGeneralReport;
        TxtLocalizationTemplateLanguages.Text = string.Join(", ", config.GenerateLanguageTemplatesFor);
        SetEnumComboBox(CmbLocalizationTemplateMode, config.LanguageTemplateValueMode);
    }

    private async void LocalizationRegisterReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RegisterReportInDatabaseAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Registration failed:" + Environment.NewLine + ex.Message,
                "Register report",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RegisterReportInDatabaseAsync()
    {
        var connectionString = TxtConnectionString.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(this, "Set the connection string first.", "Register report", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var internalName = TxtReportName.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(internalName))
        {
            MessageBox.Show(this, "Report name (internal name) is empty.", "Register report", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(TxtReportTitle.Text)
            ? internalName
            : TxtReportTitle.Text.Trim();

        var outputPath = TxtOutputPath.Text?.Trim() ?? string.Empty;
        var reportFileName = string.IsNullOrWhiteSpace(outputPath)
            ? internalName + ".rdl"
            : Path.GetFileName(outputPath);

        var registrySchema = TxtLocalizationReportRegistrySchema.Text?.Trim() ?? string.Empty;
        var registryName = TxtLocalizationReportRegistryName.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(registrySchema) || string.IsNullOrWhiteSpace(registryName))
        {
            MessageBox.Show(this, "Report registry schema/name must be filled.", "Register report", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var translationSchema = TxtLocalizationTableSchema.Text?.Trim() ?? string.Empty;
        var translationName = TxtLocalizationTableName.Text?.Trim() ?? string.Empty;
        var defaultLanguageId = ReadNonNegativeInt(TxtLocalizationDefaultLanguageId.Text, 3);
        var generalReportId = ReadNonNegativeInt(TxtLocalizationGeneralReportId.Text, 0);
        var skipGeneralKeys = ChkLocalizationSkipGeneralKeys.IsChecked == true;
        var localizationEnabled = ChkLocalizationEnabled.IsChecked == true;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var registryTarget = QuoteSqlIdentifier(registrySchema) + "." + QuoteSqlIdentifier(registryName);

            // 1. MERGE the report row.
            var mergeSql = $@"
MERGE INTO {registryTarget} AS T
USING (VALUES (@InternalName, @DisplayName, @ReportFileName, 1)) AS S(InternalName, DisplayName, ReportFileName, [Public])
   ON T.InternalName = S.InternalName
WHEN MATCHED THEN
    UPDATE SET DisplayName = S.DisplayName,
               ReportFileName = S.ReportFileName,
               [Public] = S.[Public]
WHEN NOT MATCHED THEN
    INSERT (InternalName, DisplayName, ReportFileName, [Public])
    VALUES (S.InternalName, S.DisplayName, S.ReportFileName, S.[Public]);

SELECT ReportId FROM {registryTarget} WHERE InternalName = @InternalName;";

            int reportId;
            using (var command = new SqlCommand(mergeSql, connection))
            {
                command.Parameters.Add("@InternalName", SqlDbType.NVarChar, 200).Value = internalName;
                command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 400).Value = displayName;
                command.Parameters.Add("@ReportFileName", SqlDbType.NVarChar, 400).Value = reportFileName;
                var scalar = await command.ExecuteScalarAsync();
                if (scalar is null || scalar == DBNull.Value)
                {
                    throw new InvalidOperationException("MERGE did not return a ReportId. Check the registry table schema.");
                }
                reportId = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
            }

            TxtLocalizationReportId.Text = reportId.ToString(CultureInfo.CurrentCulture);

            var translationsMerged = 0;
            if (localizationEnabled
                && !string.IsNullOrWhiteSpace(translationSchema)
                && !string.IsNullOrWhiteSpace(translationName)
                && defaultLanguageId > 0)
            {
                var stagedModel = BuildReportModelFromCurrentState();
                stagedModel.Localization.ReportId = reportId;
                var labels = LocalizationLabelCollector.Collect(stagedModel);
                if (labels.Count > 0)
                {
                    translationsMerged = await MergeDefaultLanguageTranslationsAsync(
                        connection,
                        translationSchema,
                        translationName,
                        reportId,
                        defaultLanguageId,
                        labels,
                        skipGeneralKeys && generalReportId > 0 ? generalReportId : (int?)null);
                }
            }

            MessageBox.Show(this,
                $"Report registered with ReportId = {reportId}." +
                (translationsMerged > 0 ? $"{Environment.NewLine}Default-language translations merged: {translationsMerged}." : string.Empty),
                "Register report",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static async Task<int> MergeDefaultLanguageTranslationsAsync(
        SqlConnection connection,
        string translationSchema,
        string translationName,
        int reportId,
        int languageId,
        IReadOnlyList<LocalizationLabel> labels,
        int? skipIfExistsForGeneralReportId)
    {
        var target = QuoteSqlIdentifier(translationSchema) + "." + QuoteSqlIdentifier(translationName);

        // When skipIfExistsForGeneralReportId is set, the source row is filtered out
        // if a row with the same key+language already exists for the general ReportId.
        // An empty source means MERGE does nothing for that key (no INSERT, no UPDATE).
        var sourceClause = skipIfExistsForGeneralReportId.HasValue
            ? $@"(
    SELECT @ReportId AS ReportId, @LanguageId AS LanguageId, @Key AS [Key], @Value AS [Value]
    WHERE NOT EXISTS (
        SELECT 1 FROM {target} AS G
        WHERE G.ReportId = @GeneralReportId
          AND G.LanguageId = @LanguageId
          AND G.[Key] = @Key
          AND G.Deleted = 0
    )
)"
            : "(VALUES (@ReportId, @LanguageId, @Key, @Value))";

        var sql = $@"
MERGE INTO {target} AS T
USING {sourceClause} AS S(ReportId, LanguageId, [Key], [Value])
   ON T.ReportId = S.ReportId
  AND T.LanguageId = S.LanguageId
  AND T.[Key] = S.[Key]
  AND T.Deleted = 0
WHEN MATCHED THEN
    UPDATE SET [Value] = S.[Value]
WHEN NOT MATCHED THEN
    INSERT (ReportId, LanguageId, [Key], [Value], Deleted)
    VALUES (S.ReportId, S.LanguageId, S.[Key], S.[Value], 0);";

        var merged = 0;
        foreach (var label in labels)
        {
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
            command.Parameters.Add("@LanguageId", SqlDbType.Int).Value = languageId;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = label.Key;
            command.Parameters.Add("@Value", SqlDbType.NVarChar, -1).Value = label.DefaultValue ?? string.Empty;
            if (skipIfExistsForGeneralReportId.HasValue)
            {
                command.Parameters.Add("@GeneralReportId", SqlDbType.Int).Value = skipIfExistsForGeneralReportId.Value;
            }

            var affected = await command.ExecuteNonQueryAsync();
            if (affected > 0)
            {
                merged++;
            }
        }

        return merged;
    }

    private static string QuoteSqlIdentifier(string value)
        => "[" + (value ?? string.Empty).Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>
    /// Applies saved tablix layout/style options to the Body / Tablix tab.
    /// </summary>
    private void ApplyTablixStyle(TablixStyleConfig style)
    {
        SetEnumComboBox(CmbGroupRenderMode, style.GroupRenderMode);
        TxtMatrixExpectedColumnCount.Text = Math.Clamp(style.MatrixExpectedColumnCount <= 0 ? 6 : style.MatrixExpectedColumnCount, 1, 50).ToString(CultureInfo.CurrentCulture);
        ChkTabularHorizontalSubtotals.IsChecked = style.ShowTabularHorizontalSubtotals;
        ChkTabularHorizontalGrandTotal.IsChecked = style.ShowTabularHorizontalGrandTotal;
        TxtTablixWidthPercent.Text = ToUiNumber(style.WidthPercent <= 0 ? 100.0d : Math.Clamp(style.WidthPercent, 1.0d, 100.0d));
        TxtTablixShadeBaseColor.Text = NormalizeHexColor(style.ShadeBaseColor, "#EDEDED");
        TxtTablixBorderColor.Text = NormalizeHexColor(style.BorderColor, "#A6A6A6");
        TxtTablixBorderWidth.Text = ToUiNumber(style.BorderWidthInPoints <= 0 ? 0.5d : style.BorderWidthInPoints);
        SetTablixFontFamily(style.FontFamily);
        TxtTablixFontColor.Text = NormalizeHexColor(style.FontColor, "#000000");
        TxtTablixFontSize.Text = ToUiNumber(style.FontSizeInPoints <= 0 ? 9.0d : style.FontSizeInPoints);
    }

    private void SetTablixFontFamily(string? fontFamily)
    {
        var normalized = string.IsNullOrWhiteSpace(fontFamily) ? "Arial Narrow" : fontFamily;
        foreach (var item in CmbTablixFontFamily.Items.OfType<string>())
        {
            if (string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase))
            {
                CmbTablixFontFamily.SelectedItem = item;
                return;
            }
        }

        CmbTablixFontFamily.Text = normalized;
    }

    private List<ReportSummaryColumnConfig> BuildReportSummaryColumnsFromUi(int count)
    {
        var columns = new[]
        {
            new ReportSummaryColumnConfig
            {
                TextTemplate = TxtReportSummaryColumn1Template.Text.Trim(),
                ShowTopLine = ChkReportSummaryColumn1Line.IsChecked == true,
                WidthPercent = ReadOptionalPercent(TxtReportSummaryColumn1WidthPercent.Text),
                VerticalAlign = ReadReportSummaryVerticalAlign(CmbReportSummaryColumn1VerticalAlign),
                PaddingInPoints = ReadPositiveDouble(TxtReportSummaryColumn1Padding.Text, 3.0d)
            },
            new ReportSummaryColumnConfig
            {
                TextTemplate = TxtReportSummaryColumn2Template.Text.Trim(),
                ShowTopLine = ChkReportSummaryColumn2Line.IsChecked == true,
                WidthPercent = ReadOptionalPercent(TxtReportSummaryColumn2WidthPercent.Text),
                VerticalAlign = ReadReportSummaryVerticalAlign(CmbReportSummaryColumn2VerticalAlign),
                PaddingInPoints = ReadPositiveDouble(TxtReportSummaryColumn2Padding.Text, 3.0d)
            },
            new ReportSummaryColumnConfig
            {
                TextTemplate = TxtReportSummaryColumn3Template.Text.Trim(),
                ShowTopLine = ChkReportSummaryColumn3Line.IsChecked == true,
                WidthPercent = ReadOptionalPercent(TxtReportSummaryColumn3WidthPercent.Text),
                VerticalAlign = ReadReportSummaryVerticalAlign(CmbReportSummaryColumn3VerticalAlign),
                PaddingInPoints = ReadPositiveDouble(TxtReportSummaryColumn3Padding.Text, 3.0d)
            }
        };

        return columns.Take(Math.Clamp(count, 1, 3)).ToList();
    }

    private void SetReportSummaryColumns(ReportSummaryConfig summary)
    {
        var count = Math.Clamp(summary.ColumnCount <= 0 ? 1 : summary.ColumnCount, 1, 3);
        SetComboBoxByTag(CmbReportSummaryColumnCount, count.ToString(CultureInfo.InvariantCulture));

        var columns = summary.Columns ?? new List<ReportSummaryColumnConfig>();

        ApplyReportSummaryColumn(columns.ElementAtOrDefault(0), TxtReportSummaryColumn1Template, ChkReportSummaryColumn1Line, TxtReportSummaryColumn1WidthPercent, CmbReportSummaryColumn1VerticalAlign, TxtReportSummaryColumn1Padding);
        ApplyReportSummaryColumn(columns.ElementAtOrDefault(1), TxtReportSummaryColumn2Template, ChkReportSummaryColumn2Line, TxtReportSummaryColumn2WidthPercent, CmbReportSummaryColumn2VerticalAlign, TxtReportSummaryColumn2Padding);
        ApplyReportSummaryColumn(columns.ElementAtOrDefault(2), TxtReportSummaryColumn3Template, ChkReportSummaryColumn3Line, TxtReportSummaryColumn3WidthPercent, CmbReportSummaryColumn3VerticalAlign, TxtReportSummaryColumn3Padding);
        this.activeReportSummaryTemplateBox = TxtReportSummaryColumn1Template;
        UpdateReportSummaryColumnVisibility();
    }

    private static void ApplyReportSummaryColumn(
        ReportSummaryColumnConfig? column,
        TextBox templateBox,
        CheckBox lineCheckBox,
        TextBox widthPercentBox,
        ComboBox verticalAlignComboBox,
        TextBox paddingBox)
    {
        templateBox.Text = column?.TextTemplate ?? string.Empty;
        lineCheckBox.IsChecked = column?.ShowTopLine == true;
        widthPercentBox.Text = column is not null && column.WidthPercent > 0
            ? ToUiNumber(column.WidthPercent)
            : string.Empty;
        SetComboBoxByTag(verticalAlignComboBox, string.IsNullOrWhiteSpace(column?.VerticalAlign) ? "Top" : column.VerticalAlign);
        paddingBox.Text = column is not null && column.PaddingInPoints >= 0
            ? ToUiNumber(column.PaddingInPoints)
            : "3";
    }

    private static string ReadReportSummaryVerticalAlign(ComboBox comboBox)
    {
        var tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, "Middle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "Bottom", StringComparison.OrdinalIgnoreCase)
            ? tag!
            : "Top";
    }

    private static void SetComboBoxByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void ReportSummaryColumnCount_Changed(object sender, SelectionChangedEventArgs e)
        => UpdateReportSummaryColumnVisibility();

    private void UpdateReportSummaryColumnVisibility()
    {
        if (PanelReportSummaryColumn1 is null)
        {
            return;
        }

        var count = ReadReportSummaryColumnCount();
        PanelReportSummaryColumn1.Visibility = Visibility.Visible;
        PanelReportSummaryColumn2.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelReportSummaryColumn3.Visibility = count >= 3 ? Visibility.Visible : Visibility.Collapsed;
        ColReportSummaryColumn1.Width = new GridLength(1, GridUnitType.Star);
        ColReportSummaryColumn2.Width = count >= 2
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        ColReportSummaryColumn3.Width = count >= 3
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private static T ReadEnumComboBox<T>(ComboBox comboBox, T fallback) where T : struct, Enum
    {
        var tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<T>(tag, ignoreCase: true, out var value) ? value : fallback;
    }

    private static void SetEnumComboBox<T>(ComboBox comboBox, T value) where T : struct, Enum
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void MemorandumLogoAlignment_Changed(object sender, SelectionChangedEventArgs e)
        => UpdateMemorandumLogoAlignmentAvailability();

    private void UpdateMemorandumLogoAlignmentAvailability()
    {
        // "Beside logo" + "Center" alignment doesn't make geometric sense — text would
        // have no room on either side. When Center is chosen, force text below.
        if (CmbMemorandumLogoAlignment is null || CmbMemorandumTextPlacement is null)
        {
            return;
        }

        var alignment = ReadEnumComboBox(CmbMemorandumLogoAlignment, MemorandumLogoAlignment.Left);
        if (alignment == MemorandumLogoAlignment.Center)
        {
            SetEnumComboBox(CmbMemorandumTextPlacement, MemorandumTextPlacement.BelowLogo);
        }
    }

    private static void SetReportBandLayoutMode(ComboBox comboBox, ReportBandLayoutMode mode)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string? BuildSubreportName(string? subreportPath)
    {
        if (string.IsNullOrWhiteSpace(subreportPath))
        {
            return null;
        }

        var value = subreportPath.Trim();
        var extension = Path.GetExtension(value);
        return string.IsNullOrWhiteSpace(extension)
            ? value
            : Path.GetFileNameWithoutExtension(value);
    }

    /// <summary>
    /// Replaces the report parameter grid rows and normalizes runtime metadata for display.
    /// </summary>
    private void ApplyReportParameters(IEnumerable<ReportParameter> parameters)
    {
        this.reportParameters.Clear();
        foreach (var parameter in NormalizeParameterOrdinals(parameters))
        {
            parameter.BindToDatasetParameterName = NormalizeOptional(parameter.BindToDatasetParameterName ?? string.Empty)?.TrimStart('@');
            EnsureRuntimeParameterDefaults(parameter);
            this.reportParameters.Add(parameter);
        }

        UpdateParameterDefinitionChoices([]);
    }

    private void UpdateStoredProcedureParameterChoices(IEnumerable<SpParameter> parameters)
    {
        var selectedValues = this.reportParameters
            .Select(parameter => NormalizeOptional(parameter.BindToDatasetParameterName ?? string.Empty)?.TrimStart('@'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        this.storedProcedureParameterChoices.Clear();
        this.storedProcedureParameterChoices.Add(new Choice<string>(string.Empty, string.Empty));

        foreach (var parameter in parameters
            .Where(parameter => !parameter.IsOutput)
            .OrderBy(parameter => parameter.OrdinalPosition <= 0 ? int.MaxValue : parameter.OrdinalPosition)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase))
        {
            var value = parameter.Name.TrimStart('@');
            this.storedProcedureParameterChoices.Add(new Choice<string>(value, EnsureAtPrefixLocal(value)));
            selectedValues.Remove(value);
        }

        foreach (var selectedValue in selectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            this.storedProcedureParameterChoices.Add(new Choice<string>(selectedValue, EnsureAtPrefixLocal(selectedValue)));
        }
    }

    private void AutoBindReportParametersToStoredProcedureParameters(IEnumerable<SpParameter> spParameters)
    {
        AutoBindReportParametersToStoredProcedureParameters(this.reportParameters, spParameters);
        GridReportParameters.Items.Refresh();
        UpdateStoredProcedureParameterChoices(spParameters);
    }

    private static void AutoBindReportParametersToStoredProcedureParameters(
        IEnumerable<ReportParameter> reportParameters,
        IEnumerable<SpParameter> spParameters)
    {
        var spParameterNames = spParameters
            .Where(parameter => !parameter.IsOutput)
            .Select(parameter => parameter.Name.TrimStart('@'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in reportParameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter.BindToDatasetParameterName))
            {
                continue;
            }

            var reportParameterName = parameter.Name.TrimStart('@');
            if (spParameterNames.TryGetValue(reportParameterName, out var matchingSpParameterName))
            {
                parameter.BindToDatasetParameterName = matchingSpParameterName;
            }
        }
    }

    private static void ApplyDatasetParameterBindings(ReportModel model)
    {
        var mainDataset = model.Datasets.FirstOrDefault(dataset => dataset.Name == model.MainDatasetName)
            ?? model.Datasets.FirstOrDefault();
        if (mainDataset is null)
        {
            return;
        }

        mainDataset.ParameterBindings = model.Parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.BindToDatasetParameterName))
            .Select(parameter => new DatasetParameterBinding
            {
                DatasetParameterName = parameter.BindToDatasetParameterName!.Trim(),
                ReportParameterName = parameter.Name.TrimStart('@')
            })
            .ToList();
    }

    /// <summary>
    /// Normalizes report parameter values before they are persisted or used for generation.
    /// </summary>
    private static void NormalizeReportParameters(IEnumerable<ReportParameter> parameters)
    {
        var parameterList = parameters.ToList();
        var ordinal = 1;
        foreach (var parameter in parameterList)
        {
            parameter.Name = parameter.Name.Trim().TrimStart('@');
            parameter.DefinitionName = NormalizeOptional(parameter.DefinitionName ?? string.Empty)?.TrimStart('@');
            parameter.Prompt = string.IsNullOrWhiteSpace(parameter.Prompt)
                ? parameter.Name
                : parameter.Prompt.Trim();
            parameter.SqlTypeName = string.IsNullOrWhiteSpace(parameter.SqlTypeName)
                ? "nvarchar"
                : parameter.SqlTypeName.Trim();
            parameter.DefaultValueExpression = NormalizeOptional(parameter.DefaultValueExpression ?? string.Empty);
            parameter.DefaultValueSql = NormalizeOptional(parameter.DefaultValueSql ?? string.Empty);
            parameter.DefaultValueDatasetName = NormalizeOptional(parameter.DefaultValueDatasetName ?? string.Empty);
            parameter.DefaultValueField = NormalizeOptional(parameter.DefaultValueField ?? string.Empty);
            if (string.IsNullOrWhiteSpace(parameter.DefaultValueSql))
            {
                ResetGeneratedDefaultDatasetReference(parameter);
            }

            parameter.DisplayFormat = NormalizeOptional(parameter.DisplayFormat ?? string.Empty);
            parameter.LookupSql = NormalizeOptional(parameter.LookupSql ?? string.Empty);
            parameter.DependsOnParameterName = NormalizeDependencyList(parameter.DependsOnParameterName, parameter.Name);
            parameter.DependencyFilterPath = NormalizeOptional(parameter.DependencyFilterPath ?? string.Empty);
            parameter.BindToDatasetParameterName = NormalizeOptional(parameter.BindToDatasetParameterName ?? string.Empty)?.TrimStart('@');
            parameter.CompareToParameterName = NormalizeOptional(parameter.CompareToParameterName ?? string.Empty)?.TrimStart('@');
            parameter.CompareOperator = NormalizeOptional(parameter.CompareOperator ?? string.Empty);
            parameter.ComparisonValueTemplate = NormalizeOptional(parameter.ComparisonValueTemplate ?? string.Empty);

            if (string.Equals(parameter.CompareToParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase))
            {
                parameter.CompareToParameterName = null;
            }

            if (string.IsNullOrWhiteSpace(parameter.CompareToParameterName))
            {
                parameter.CompareOperator = null;
                parameter.ComparisonValueTemplate = null;
            }
            else if (string.IsNullOrWhiteSpace(parameter.CompareOperator) || !CompareOperators.Contains(parameter.CompareOperator))
            {
                parameter.CompareOperator = ">=";
            }

            if (parameter.MultiValue)
            {
                parameter.Nullable = false;
            }

            if (parameter.OrdinalNumber <= 0)
            {
                parameter.OrdinalNumber = ordinal;
            }

            ordinal++;
        }

        RemoveForwardParameterReferences(parameterList);
    }

    private static void RemoveForwardParameterReferences(IReadOnlyList<ReportParameter> parameters)
    {
        var ordinalsByName = parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(parameter => parameter.OrdinalNumber), StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            var currentOrdinal = parameter.OrdinalNumber;
            var validDependencies = ParseDependencyNames(parameter.DependsOnParameterName)
                .Where(name => ordinalsByName.TryGetValue(name, out var ordinal) && ordinal < currentOrdinal)
                .ToList();
            parameter.DependsOnParameterName = validDependencies.Count == 0 ? null : string.Join(", ", validDependencies);

            if (!string.IsNullOrWhiteSpace(parameter.CompareToParameterName)
                && (!ordinalsByName.TryGetValue(parameter.CompareToParameterName, out var compareOrdinal)
                    || compareOrdinal >= currentOrdinal))
            {
                parameter.CompareToParameterName = null;
                parameter.CompareOperator = null;
                parameter.ComparisonValueTemplate = null;
            }
        }
    }

    private void CommitPendingGridEdits()
    {
        GridReportParameters.CommitEdit(DataGridEditingUnit.Cell, true);
        GridReportParameters.CommitEdit(DataGridEditingUnit.Row, true);
        GridFields.CommitEdit(DataGridEditingUnit.Cell, true);
        GridFields.CommitEdit(DataGridEditingUnit.Row, true);
        GridReportVariables.CommitEdit(DataGridEditingUnit.Cell, true);
        GridReportVariables.CommitEdit(DataGridEditingUnit.Row, true);
        NormalizeFieldDrafts();
    }

    private List<ReportVariableConfig> BuildReportVariablesFromGrid()
        => this.reportVariables
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
            .Select(variable => new ReportVariableConfig
            {
                Enabled = variable.Enabled,
                Name = variable.Name.Trim(),
                SourceColumnName = NormalizeOptional(variable.SourceColumnName ?? string.Empty),
                StaticValue = NormalizeOptional(variable.StaticValue ?? string.Empty),
                FallbackValue = NormalizeOptional(variable.FallbackValue ?? string.Empty)
            })
            .ToList();

    private void ApplyReportVariables(IEnumerable<ReportVariableConfig> variables)
    {
        this.reportVariables.Clear();
        foreach (var variable in variables.Where(variable => !string.IsNullOrWhiteSpace(variable.Name)))
        {
            this.reportVariables.Add(new ReportVariableConfig
            {
                Enabled = variable.Enabled,
                Name = variable.Name,
                SourceColumnName = variable.SourceColumnName,
                StaticValue = variable.StaticValue,
                FallbackValue = variable.FallbackValue
            });
        }

        if (this.reportVariables.Count == 0)
        {
            this.reportVariables.Add(new ReportVariableConfig
            {
                Name = "CompanyName",
                SourceColumnName = "CompanyName",
                FallbackValue = TxtCompanyName.Text.Trim()
            });
        }
    }

    /// <summary>
    /// Normalizes dataset column drafts before they are persisted or used by the RDL builder.
    /// </summary>
    private void NormalizeFieldDrafts()
    {
        foreach (var field in this.fieldDrafts)
        {
            field.WidthPercent = field.WidthPercent > 0 ? Math.Clamp(field.WidthPercent, 1.0d, 100.0d) : 0.0d;
            field.MatrixRole = Enum.IsDefined(field.MatrixRole) ? field.MatrixRole : MatrixFieldRole.None;
            field.MatrixLevel = field.MatrixRole is MatrixFieldRole.RowGroup or MatrixFieldRole.ColumnGroup
                ? Math.Clamp(field.MatrixLevel, 0, 10)
                : 0;
            field.GroupLevel = field.GroupLevel is >= 1 and <= 4 ? field.GroupLevel : 0;
            if (field.GroupLevel > 0)
            {
                field.AggregateFunction = null;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(field.AggregateFunction)
                && !DatasetFieldDraft.GetAllowedAggregates(field.SqlTypeName).Contains(field.AggregateFunction, StringComparer.OrdinalIgnoreCase))
            {
                field.AggregateFunction = null;
            }
        }

        GridFields.Items.Refresh();
    }

    /// <summary>
    /// Commits the report parameter grid to an ordered list for persistence and generation.
    /// </summary>
    private List<ReportParameter> BuildReportParametersFromGrid()
    {
        SortReportParametersByOrdinal();

        foreach (var parameter in this.reportParameters)
        {
            EnsureRuntimeParameterDefaults(parameter);
        }

        return this.reportParameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SortReportParametersByOrdinal()
    {
        var sortedParameters = this.reportParameters
            .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sortedParameters.SequenceEqual(this.reportParameters))
        {
            return;
        }

        this.reportParameters.Clear();
        foreach (var parameter in sortedParameters)
        {
            this.reportParameters.Add(parameter);
        }

    }

    /// <summary>
    /// Assigns missing ordinal values while preserving the existing parameter order.
    /// </summary>
    private static IEnumerable<ReportParameter> NormalizeParameterOrdinals(IEnumerable<ReportParameter> parameters)
    {
        var ordinal = 1;
        foreach (var parameter in parameters)
        {
            EnsureRuntimeParameterDefaults(parameter);
            if (parameter.OrdinalNumber <= 0)
            {
                parameter.OrdinalNumber = ordinal;
            }

            ordinal++;
            yield return parameter;
        }
    }

    /// <summary>
    /// Normalizes runtime-only parameter metadata for grid display and JSON persistence.
    /// </summary>
    private static void EnsureRuntimeParameterDefaults(ReportParameter parameter)
    {
        parameter.EntityKey = NormalizeOptional(parameter.EntityKey ?? string.Empty);
        parameter.ValueFieldTemplate = NormalizeOptional(parameter.ValueFieldTemplate ?? string.Empty);
        parameter.DisplayFieldTemplate = NormalizeOptional(parameter.DisplayFieldTemplate ?? string.Empty);
        parameter.DependencyFilterPath = NormalizeOptional(parameter.DependencyFilterPath ?? string.Empty);
    }

    /// <summary>
    /// Clears generated default dataset metadata when the parameter no longer has default SQL.
    /// </summary>
    private static void ResetGeneratedDefaultDatasetReference(ReportParameter parameter)
    {
        parameter.DefaultValueDatasetName = null;
        parameter.DefaultValueField = null;
    }

    private static void ApplyAuxiliaryParameterDatasets(ReportModel model)
    {
        var generatedDatasetNames = new HashSet<string>(
            model.Datasets.Select(dataset => dataset.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in model.Parameters.Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name)))
        {
            if (!string.IsNullOrWhiteSpace(parameter.LookupSql))
            {
                parameter.Lookup ??= new LookupConfig
                {
                    DatasetName = BuildLookupDatasetName(parameter.Name)
                };

                parameter.Lookup.ValueField = "Value";
                parameter.Lookup.LabelField = "Label";
                parameter.Lookup.DatasetName = EnsureUniqueDatasetName(parameter.Lookup.DatasetName, generatedDatasetNames);
                model.Datasets.Add(new DatasetConfig
                {
                    Name = parameter.Lookup.DatasetName,
                    Command = parameter.LookupSql.Trim(),
                    CommandKind = CommandKind.Text,
                    Fields =
                    [
                        new DatasetField(parameter.Lookup.ValueField, parameter.SqlTypeName, false, 1),
                        new DatasetField(parameter.Lookup.LabelField, "nvarchar", true, 2)
                    ],
                    ParameterBindings = BuildLookupParameterBindings(parameter)
                });
            }

            if (string.IsNullOrWhiteSpace(parameter.DefaultValueSql))
            {
                ResetGeneratedDefaultDatasetReference(parameter);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parameter.DefaultValueSql))
            {
                var defaultDatasetName = EnsureUniqueDatasetName(BuildDefaultDatasetName(parameter.Name), generatedDatasetNames);
                parameter.DefaultValueDatasetName = defaultDatasetName;
                parameter.DefaultValueField = "Value";
                model.Datasets.Add(new DatasetConfig
                {
                    Name = defaultDatasetName,
                    Command = parameter.DefaultValueSql.Trim(),
                    CommandKind = CommandKind.Text,
                    Fields =
                    [
                        new DatasetField("Value", parameter.SqlTypeName, true, 1)
                    ]
                });
            }
        }
    }

    private static List<DatasetParameterBinding> BuildLookupParameterBindings(ReportParameter parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter.DependsOnParameterName))
        {
            return [];
        }

        return ParseDependencyNames(parameter.DependsOnParameterName)
            .Select(dependencyName => new DatasetParameterBinding
            {
                DatasetParameterName = "@" + dependencyName,
                ReportParameterName = dependencyName
            })
            .ToList();
    }

    private static string? NormalizeDependencyList(string? dependencyList, string parameterName)
    {
        var currentName = parameterName.Trim().TrimStart('@');
        var dependencies = ParseDependencyNames(dependencyList)
            .Where(name => !string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return dependencies.Count == 0 ? null : string.Join(", ", dependencies);
    }

    private static List<string> ParseDependencyNames(string? dependencyList)
        => string.IsNullOrWhiteSpace(dependencyList)
            ? []
            : dependencyList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.Trim().TrimStart('@'))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

    private static string? GetPrimaryDependencyName(string? dependencyList)
        => ParseDependencyNames(dependencyList).FirstOrDefault();

    private List<string> GetEarlierParameterNames(ReportParameter parameter)
    {
        var currentOrdinal = GetEffectiveOrdinal(parameter);
        var currentName = parameter.Name.Trim().TrimStart('@');
        return this.reportParameters
            .Where(candidate => !ReferenceEquals(candidate, parameter)
                && GetEffectiveOrdinal(candidate) < currentOrdinal)
            .Select(candidate => candidate.Name.Trim().TrimStart('@'))
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private int GetEffectiveOrdinal(ReportParameter parameter)
    {
        if (parameter.OrdinalNumber > 0)
        {
            return parameter.OrdinalNumber;
        }

        var index = this.reportParameters.IndexOf(parameter);
        return index < 0 ? int.MaxValue : index + 1;
    }

    private bool IsMultiValueReportParameter(string? parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return false;
        }

        var normalizedName = parameterName.Trim().TrimStart('@');
        return this.reportParameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name.TrimStart('@'), normalizedName, StringComparison.OrdinalIgnoreCase))?.MultiValue == true;
    }

    private static string EnsureUniqueDatasetName(string datasetName, HashSet<string> usedNames)
    {
        var baseName = string.IsNullOrWhiteSpace(datasetName) ? "dsLookup" : datasetName.Trim();
        var candidate = baseName;
        var index = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = baseName + index.ToString(CultureInfo.InvariantCulture);
            index++;
        }

        return candidate;
    }

    private static string BuildDefaultDatasetName(string parameterName)
    {
        var normalized = parameterName.TrimStart('@');
        return "ds" + (string.IsNullOrWhiteSpace(normalized)
            ? "Default"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..]) + "Default";
    }

    private void SetOutputMode(OutputMode outputMode)
    {
        foreach (var item in CmbOutputMode.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), outputMode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CmbOutputMode.SelectedItem = item;
                return;
            }
        }

        CmbOutputMode.SelectedIndex = 0;
    }

    private void SetReportPurpose(ReportPurpose purpose)
    {
        foreach (var item in CmbReportPurpose.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), purpose.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CmbReportPurpose.SelectedItem = item;
                return;
            }
        }

        CmbReportPurpose.SelectedIndex = 0;
    }

    private PageSetupConfig BuildPageSetup()
    {
        var pageSize = ReadPageSize();
        var orientation = ReadPageOrientation();
        var (width, height) = GetPageDimensions(pageSize);

        if (orientation == PageOrientation.Landscape)
        {
            (width, height) = (height, width);
        }

        var pageSetup = new PageSetupConfig
        {
            PageSizeName = pageSize,
            Orientation = orientation,
            WidthInCentimeters = width,
            HeightInCentimeters = height,
            LeftMarginInCentimeters = ReadPositiveDouble(TxtMarginLeft.Text, 1.0d),
            RightMarginInCentimeters = ReadPositiveDouble(TxtMarginRight.Text, 1.0d),
            TopMarginInCentimeters = ReadPositiveDouble(TxtMarginTop.Text, 1.0d),
            BottomMarginInCentimeters = ReadPositiveDouble(TxtMarginBottom.Text, 1.0d)
        };

        return pageSetup;
    }

    private void ApplyPageSetup(PageSetupConfig pageSetup)
    {
        SetPageSize(pageSetup.PageSizeName);
        SetPageOrientation(pageSetup.Orientation);
        TxtMarginLeft.Text = ToUiNumber(pageSetup.LeftMarginInCentimeters);
        TxtMarginRight.Text = ToUiNumber(pageSetup.RightMarginInCentimeters);
        TxtMarginTop.Text = ToUiNumber(pageSetup.TopMarginInCentimeters);
        TxtMarginBottom.Text = ToUiNumber(pageSetup.BottomMarginInCentimeters);
    }

    private PageOrientation ReadPageOrientation()
    {
        var tag = (CmbPageOrientation.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, nameof(PageOrientation.Landscape), StringComparison.OrdinalIgnoreCase)
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;
    }

    private void SetPageOrientation(PageOrientation orientation)
    {
        foreach (var item in CmbPageOrientation.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), orientation.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CmbPageOrientation.SelectedItem = item;
                return;
            }
        }

        CmbPageOrientation.SelectedIndex = 0;
    }

    private string ReadPageSize()
        => (CmbPageSize.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? CmbPageSize.Text.Trim()
            ?? "A4";

    private void SetPageSize(string? pageSizeName)
    {
        foreach (var item in CmbPageSize.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), pageSizeName, StringComparison.OrdinalIgnoreCase))
            {
                CmbPageSize.SelectedItem = item;
                return;
            }
        }

        CmbPageSize.Text = string.IsNullOrWhiteSpace(pageSizeName) ? "A4" : pageSizeName;
    }

    private static (double Width, double Height) GetPageDimensions(string pageSizeName)
    {
        return pageSizeName.Trim().ToUpperInvariant() switch
        {
            "A3" => (29.7d, 42.0d),
            "LETTER" => (21.59d, 27.94d),
            "LEGAL" => (21.59d, 35.56d),
            _ => (21.0d, 29.7d)
        };
    }

    private string ReadBaseFontFamily()
    {
        var selected = CmbBaseFont.SelectedItem?.ToString();
        var typed = CmbBaseFont.Text.Trim();
        return string.IsNullOrWhiteSpace(typed)
            ? (string.IsNullOrWhiteSpace(selected) ? "Arial" : selected)
            : typed;
    }

    private void SetBaseFontFamily(string? fontFamily)
    {
        var normalized = string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily;
        foreach (var item in CmbBaseFont.Items.OfType<string>())
        {
            if (string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase))
            {
                CmbBaseFont.SelectedItem = item;
                return;
            }
        }

        CmbBaseFont.Text = normalized;
    }

    private string ReadStoredProcedureName()
        => (CmbStoredProcedure.SelectedItem as StoredProcedureSummary)?.DisplayName
            ?? CmbStoredProcedure.Text.Trim();

    private string BuildDefaultStateFileName()
    {
        var name = string.IsNullOrWhiteSpace(TxtReportName.Text)
            ? "report"
            : TxtReportName.Text.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + ".sp2rdl.json";
    }

    private string BuildDefaultReportFileName(string extension)
    {
        var name = string.IsNullOrWhiteSpace(TxtReportName.Text)
            ? ReadStoredProcedureName()
            : TxtReportName.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Report";
        }

        name = ReadReportPurpose() switch
        {
            ReportPurpose.MemorandumSubreport when !name.StartsWith("Memorandum_", StringComparison.OrdinalIgnoreCase)
                => "Memorandum_" + name,
            ReportPurpose.ReportSummarySubreport when !name.StartsWith("ReportSummary_", StringComparison.OrdinalIgnoreCase)
                => "ReportSummary_" + name,
            _ => name
        };

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + extension;
    }

    private static string? NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatStaticValidValues(IEnumerable<StaticValidValue> values)
        => string.Join(
            Environment.NewLine,
            values
                .Where(value => !string.IsNullOrWhiteSpace(value.Value))
                .Select(value => $"{value.Value.Trim()} | {(string.IsNullOrWhiteSpace(value.Label) ? value.Value.Trim() : value.Label.Trim())}"));

    private static List<StaticValidValue> ParseStaticValidValues(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var values = new List<StaticValidValue>();
        foreach (var rawLine in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            var value = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            values.Add(new StaticValidValue
            {
                Value = value,
                Label = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : value
            });
        }

        return values;
    }

    private static double ReadPositiveDouble(string value, double fallback)
    {
        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : fallback;
    }

    private static double ReadOptionalPercent(string value)
    {
        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0
            ? Math.Clamp(result, 1.0d, 100.0d)
            : 0.0d;
    }

    private static double ReadPercentOrDefault(string value, double fallback)
    {
        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0
            ? Math.Clamp(result, 1.0d, 100.0d)
            : fallback;
    }

    private static int ReadNonNegativeInt(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result) && result >= 0
            ? result
            : fallback;

    private static List<int> ParseIntegerList(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => int.TryParse(item, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result) ? result : 0)
                .Where(result => result > 0)
                .Distinct()
                .ToList();

    private static string NormalizeHexColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = "#" + trimmed;
        }

        return Regex.IsMatch(trimmed, "^#[0-9A-Fa-f]{6}$") ? trimmed.ToUpperInvariant() : fallback;
    }

    private static string ToUiNumber(double value)
        => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string? SanitizeConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            builder.Remove("Password");
            builder.Remove("Pwd");
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static string ParseSchemaName(string storedProcedureName)
    {
        var parts = storedProcedureName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? parts[0] : "dbo";
    }

    private static string ParseProcedureName(string storedProcedureName)
    {
        var parts = storedProcedureName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? parts[1] : storedProcedureName;
    }

    private void TxtTablixShadeBaseColor_TextChanged(object sender, TextChangedEventArgs e)
    {

    }

    private static string BuildLookupDatasetName(string parameterName)
    {
        var normalized = parameterName.TrimStart('@');
        return "ds" + (string.IsNullOrWhiteSpace(normalized)
            ? "Lookup"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..]);
    }
}
#pragma warning restore CS0618
