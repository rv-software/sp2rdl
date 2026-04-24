using System.Collections.ObjectModel;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using sp2rdlGenExtension.Model;
using sp2rdlGenExtension.Persistence;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
public partial class ReportSetupDialog : Window
{
    private readonly string solutionDirectory;
    private readonly SqlIntrospector sqlIntrospector;
    private readonly ObservableCollection<DatasetFieldDraft> fieldDrafts = new();
    private StoredProcedureMetadata? currentMetadata;

    internal ReportGenerationRequest Request { get; private set; } = new();

    internal ReportSetupDialog(string solutionDirectory, SqlIntrospector sqlIntrospector)
    {
        this.solutionDirectory = solutionDirectory;
        this.sqlIntrospector = sqlIntrospector;
        InitializeComponent();
        GridFields.ItemsSource = this.fieldDrafts;
    }

    private void ConnectionButton_Click(object sender, RoutedEventArgs e)
        => _ = SelectConnectionAsync();

    private async Task SelectConnectionAsync()
    {
        try
        {
            var dialog = new DatabaseConnectionDialog(this.solutionDirectory, TxtConnectionString.Text.Trim())
            {
                Owner = this,
                ShowActivated = true
            };

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
                LblMetadataStatus.Text = string.Empty;

                await LoadProceduresAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open connection dialog:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadProceduresButton_Click(object sender, RoutedEventArgs e)
        => _ = LoadProceduresAsync();

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
            CmbStoredProcedure.ItemsSource = await Task.Run(() =>
                this.sqlIntrospector.ListStoredProceduresAsync(connectionString, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
            LblMetadataStatus.Text = "Stored procedures loaded.";
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
        => _ = InspectProcedureAsync();

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
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load state:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
            this.currentMetadata = await Task.Run(() =>
                this.sqlIntrospector.ReadStoredProcedureAsync(connectionString, storedProcedureName, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());

            GridParameters.ItemsSource = this.currentMetadata.Parameters;
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

    private async Task SuggestColumnsAsync()
    {
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
            var suggestedFields = await Task.Run(() =>
                this.sqlIntrospector.SuggestFieldsFromProcedureTextAsync(connectionString, storedProcedureName, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());

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
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not suggest columns from procedure text:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var reportModel = BuildReportModelFromCurrentState();

        Request = new ReportGenerationRequest
        {
            ConnectionString = TxtConnectionString.Text.Trim(),
            StoredProcedureName = (CmbStoredProcedure.SelectedItem as Services.StoredProcedureSummary)?.DisplayName ?? string.Empty,
            OutputPath = TxtOutputPath.Text.Trim(),
            ReportModel = reportModel
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private OutputMode ReadOutputMode()
    {
        var tag = (CmbOutputMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, nameof(OutputMode.Rdlc), StringComparison.OrdinalIgnoreCase)
            ? OutputMode.Rdlc
            : OutputMode.Rdl;
    }

    private StoredProcedureMetadata? BuildCurrentMetadata()
    {
        if (this.currentMetadata is null)
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

    private ReportModel BuildReportModelFromCurrentState()
    {
        var metadata = BuildCurrentMetadata();
        var reportModel = metadata is null
            ? BuildReportModelWithoutMetadata()
            : ReportModelFactory.FromStoredProcedure(metadata);

        reportModel.Name = TxtReportName.Text.Trim();
        reportModel.Author = NormalizeOptional(TxtAuthor.Text);
        reportModel.Description = NormalizeOptional(TxtDescription.Text);
        reportModel.OutputMode = ReadOutputMode();
        reportModel.SourceConnectionString = SanitizeConnectionString(TxtConnectionString.Text);
        reportModel.SourceStoredProcedureName = ReadStoredProcedureName();
        reportModel.OutputPath = NormalizeOptional(TxtOutputPath.Text);
        reportModel.PageSetup = BuildPageSetup();
        reportModel.PageHeader.LeftText = TxtHeaderLeft.Text.Trim();
        reportModel.PageHeader.RightText = TxtHeaderRight.Text.Trim();

        return reportModel;
    }

    private ReportModel BuildReportModelWithoutMetadata()
    {
        var storedProcedureName = ReadStoredProcedureName();
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
            Name = "DsMain",
            Command = storedProcedureName,
            CommandKind = CommandKind.StoredProcedure,
            Fields = fields
        };

        return new ReportModel
        {
            Name = TxtReportName.Text.Trim(),
            MainDatasetName = dataset.Name,
            Datasets = string.IsNullOrWhiteSpace(storedProcedureName) && fields.Count == 0 ? [] : [dataset]
        };
    }

    private void ApplyReportModel(ReportModel model)
    {
        TxtReportName.Text = model.Name;
        TxtAuthor.Text = model.Author ?? string.Empty;
        TxtDescription.Text = model.Description ?? string.Empty;
        TxtConnectionString.Text = model.SourceConnectionString ?? string.Empty;
        TxtOutputPath.Text = model.OutputPath ?? string.Empty;
        TxtHeaderLeft.Text = model.PageHeader.LeftText;
        TxtHeaderRight.Text = model.PageHeader.RightText;
        SetOutputMode(model.OutputMode);
        ApplyPageSetup(model.PageSetup);

        CmbStoredProcedure.ItemsSource = null;
        CmbStoredProcedure.Text = model.SourceStoredProcedureName
            ?? model.Datasets.FirstOrDefault(dataset => dataset.Name == model.MainDatasetName)?.Command
            ?? model.Datasets.FirstOrDefault()?.Command
            ?? string.Empty;

        var mainDataset = model.Datasets.FirstOrDefault(dataset => dataset.Name == model.MainDatasetName)
            ?? model.Datasets.FirstOrDefault();

        this.fieldDrafts.Clear();
        if (mainDataset is not null)
        {
            foreach (var field in mainDataset.Fields.Select(DatasetFieldDraft.FromDatasetField))
            {
                this.fieldDrafts.Add(field);
            }
        }

        GridReportParameters.ItemsSource = model.Parameters;
        GridParameters.ItemsSource = model.Parameters
            .Select((parameter, index) => new SpParameter(
                "@" + parameter.Name.TrimStart('@'),
                parameter.SqlTypeName,
                parameter.Nullable,
                !string.IsNullOrWhiteSpace(parameter.DefaultValueExpression),
                false,
                index + 1))
            .ToList();

        this.currentMetadata = mainDataset is null
            ? null
            : new StoredProcedureMetadata(
                ParseSchemaName(CmbStoredProcedure.Text),
                ParseProcedureName(CmbStoredProcedure.Text),
                GridParameters.ItemsSource is IReadOnlyList<SpParameter> spParameters ? spParameters : [],
                mainDataset.Fields);

        LblMetadataStatus.Text = "State loaded.";
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

    private PageSetupConfig BuildPageSetup()
    {
        var orientation = ReadPageOrientation();
        var pageSetup = new PageSetupConfig
        {
            Orientation = orientation,
            LeftMarginInCentimeters = ReadPositiveDouble(TxtMarginLeft.Text, 1.0d),
            RightMarginInCentimeters = ReadPositiveDouble(TxtMarginRight.Text, 1.0d),
            TopMarginInCentimeters = ReadPositiveDouble(TxtMarginTop.Text, 1.0d),
            BottomMarginInCentimeters = ReadPositiveDouble(TxtMarginBottom.Text, 1.0d)
        };

        if (orientation == PageOrientation.Landscape)
        {
            pageSetup.WidthInCentimeters = 29.7d;
            pageSetup.HeightInCentimeters = 21.0d;
        }

        return pageSetup;
    }

    private void ApplyPageSetup(PageSetupConfig pageSetup)
    {
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

    private static string? NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double ReadPositiveDouble(string value, double fallback)
    {
        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : fallback;
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
}
#pragma warning restore CS0618
