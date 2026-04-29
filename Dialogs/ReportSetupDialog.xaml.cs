using System.Collections.ObjectModel;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using sp2rdlGenExtension.Model;
using sp2rdlGenExtension.Persistence;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

internal sealed record Choice<T>(T Value, string Label);

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
    private static readonly IReadOnlyList<string> AggregateOptions = [string.Empty, "Sum", "Count", "CountDistinct", "Min", "Max", "Avg"];
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
    private readonly ObservableCollection<DatasetFieldDraft> fieldDrafts = new();
    private readonly ObservableCollection<ReportParameter> reportParameters = new();
    private readonly ObservableCollection<ReportVariableConfig> reportVariables = new();
    private readonly ObservableCollection<Choice<string>> storedProcedureParameterChoices = new();
    private readonly CancellationTokenSource cts = new();
    private StoredProcedureMetadata? currentMetadata;

    internal ReportGenerationRequest Request { get; private set; } = new();

    internal ReportSetupDialog(string solutionDirectory, SqlIntrospector sqlIntrospector)
    {
        this.solutionDirectory = solutionDirectory;
        this.sqlIntrospector = sqlIntrospector;
        InitializeComponent();
        LoadInstalledFonts();
        ColFieldSqlType.ItemsSource = SqlTypeNames;
        ColFieldGroupLevel.ItemsSource = GroupLevels;
        ColFieldAggregate.ItemsSource = AggregateOptions;
        ColParameterSqlType.ItemsSource = SqlTypeNames;
        ColParameterControlType.ItemsSource = Enum.GetValues(typeof(ControlType));
        ColParameterCompareOperator.ItemsSource = CompareOperators;
        ColParameterBindToSpParam.ItemsSource = this.storedProcedureParameterChoices;
        GridFields.ItemsSource = this.fieldDrafts;
        GridReportParameters.ItemsSource = this.reportParameters;
        GridReportVariables.ItemsSource = this.reportVariables;
        TxtMemorandumTemplate.Text = "<b>{CompanyName}</b>";
        GridReportParameters.RowEditEnding += GridReportParameters_RowEditEnding;
        this.Closed += OnClosed;
    }

    private void GridReportParameters_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        SortReportParametersByOrdinal();
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
        CmbBaseFont.SelectedItem = fontFamilies.FirstOrDefault(fontFamily =>
            string.Equals(fontFamily, "Arial", StringComparison.OrdinalIgnoreCase));
        CmbBaseFont.Text = CmbBaseFont.SelectedItem?.ToString() ?? fontFamilies.FirstOrDefault() ?? "Arial";
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
        }
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

    private void MemorandumCompanyPlaceholderButton_Click(object sender, RoutedEventArgs e)
        => InsertMemorandumText("{CompanyName}");

    private void MemorandumReportTitlePlaceholderButton_Click(object sender, RoutedEventArgs e)
        => InsertMemorandumText("{ReportTitle}");

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
        => SetTemplatePreviewMode(TxtMemorandumTemplate, BrowserMemorandumPreview, ChkMemorandumPreview.IsChecked == true);

    private void ReportSummaryPreviewCheckBox_Changed(object sender, RoutedEventArgs e)
        => SetTemplatePreviewMode(TxtReportSummaryTemplate, BrowserReportSummaryPreview, ChkReportSummaryPreview.IsChecked == true);

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
        else if (ReferenceEquals(sender, TxtReportSummaryTemplate) && ChkReportSummaryPreview.IsChecked == true)
        {
            UpdateTemplatePreview(TxtReportSummaryTemplate, BrowserReportSummaryPreview);
        }
    }

    private void SetTemplatePreviewMode(TextBox editor, WebBrowser preview, bool enabled)
    {
        if (enabled)
        {
            UpdateTemplatePreview(editor, preview);
            editor.Visibility = Visibility.Collapsed;
            preview.Visibility = Visibility.Visible;
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
            values[variable.Name.Trim()] = FirstNonBlank(variable.StaticValue, variable.FallbackValue);
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

        var variables = GetAvailablePlaceholderNames().ToList();
        if (variables.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        foreach (var variable in variables)
        {
            var menuItem = new MenuItem { Header = "{" + variable + "}" };
            menuItem.Click += (_, _) =>
            {
                textBox.SelectedText = "{" + variable + "}";
                textBox.Focus();
            };
            menu.Items.Add(menuItem);
        }

        textBox.ContextMenu = menu;
    }

    private IEnumerable<string> GetAvailablePlaceholderNames()
        => this.reportVariables
            .Where(variable => variable.Enabled && !string.IsNullOrWhiteSpace(variable.Name))
            .Select(variable => variable.Name.Trim())
            .Concat(["CompanyName", "ReportTitle"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

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

    private void AggregateDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();

        if (sender is not Button button || button.DataContext is not DatasetFieldDraft field)
        {
            return;
        }

        var contextMenu = new ContextMenu();
        var clearItem = new MenuItem
        {
            Header = "(none)",
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(field.AggregateFunction)
        };
        clearItem.Click += (_, _) =>
        {
            field.AggregateFunction = null;
            GridFields.Items.Refresh();
        };
        contextMenu.Items.Add(clearItem);
        contextMenu.Items.Add(new Separator());

        foreach (var aggregate in DatasetFieldDraft.GetAllowedAggregates(field.SqlTypeName))
        {
            var menuItem = new MenuItem
            {
                Header = aggregate,
                IsCheckable = true,
                IsChecked = string.Equals(field.AggregateFunction, aggregate, StringComparison.OrdinalIgnoreCase)
            };
            menuItem.Click += (_, _) =>
            {
                field.AggregateFunction = aggregate;
                field.GroupLevel = 0;
                GridFields.Items.Refresh();
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

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var reportModel = BuildReportModelFromCurrentState();

        Request = new ReportGenerationRequest
        {
            ConnectionString = TxtConnectionString.Text.Trim(),
            StoredProcedureName = ReadStoredProcedureName(),
            OutputPath = TxtOutputPath.Text.Trim(),
            ReportModel = reportModel
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
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
        CommitPendingGridEdits();

        var metadata = BuildCurrentMetadata();
        var reportModel = metadata is null
            ? BuildReportModelWithoutMetadata()
            : ReportModelFactory.FromStoredProcedure(metadata);

        reportModel.Name = TxtReportName.Text.Trim();
        reportModel.Author = NormalizeOptional(TxtAuthor.Text);
        reportModel.Description = NormalizeOptional(TxtDescription.Text);
        reportModel.OutputMode = ReadOutputMode();
        reportModel.SourceConnectionString = NormalizeOptional(TxtConnectionString.Text);
        reportModel.SourceStoredProcedureName = ReadStoredProcedureName();
        reportModel.OutputPath = NormalizeOptional(TxtOutputPath.Text);
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
        reportModel.Memorandum.ShowBottomLine = ChkMemorandumBottomLine.IsChecked == true;
        reportModel.Memorandum.HeightInCentimeters = ReadPositiveDouble(TxtMemorandumHeight.Text, reportModel.Memorandum.HeightInCentimeters);
        reportModel.ReportSummary.Enabled = ChkReportSummaryEnabled.IsChecked == true;
        reportModel.ReportSummary.LayoutMode = ReadReportBandLayoutMode(CmbReportSummaryLayoutMode);
        reportModel.ReportSummary.SubreportPath = NormalizeOptional(TxtReportSummarySubreport.Text);
        reportModel.ReportSummary.SubreportName = BuildSubreportName(reportModel.ReportSummary.SubreportPath);
        reportModel.ReportSummary.SubreportServerPath = NormalizeOptional(TxtReportSummarySubreportServerPath.Text);
        reportModel.ReportSummary.FallbackToInline = ChkReportSummaryFallbackInline.IsChecked == true;
        reportModel.ReportSummary.TextTemplate = TxtReportSummaryTemplate.Text.Trim();
        reportModel.ReportSummary.ShowTopLine = ChkReportSummaryTopLine.IsChecked == true;
        reportModel.ReportSummary.HeightInCentimeters = ReadPositiveDouble(TxtReportSummaryHeight.Text, reportModel.ReportSummary.HeightInCentimeters);
        reportModel.PageSetup = BuildPageSetup();
        reportModel.PageHeader.Enabled = ChkPageHeaderEnabled.IsChecked == true;
        reportModel.PageHeader.LeftText = TxtHeaderLeft.Text.Trim();
        reportModel.PageHeader.RightText = TxtHeaderRight.Text.Trim();
        reportModel.PageHeader.HeightInCentimeters = ReadPositiveDouble(TxtPageHeaderHeight.Text, reportModel.PageHeader.HeightInCentimeters);
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
            Name = "dsMain",
            Command = storedProcedureName,
            CommandKind = CommandKind.StoredProcedure,
            Fields = fields
        };

        return new ReportModel
        {
            Name = TxtReportName.Text.Trim(),
            MainDatasetName = dataset.Name,
            Datasets = string.IsNullOrWhiteSpace(storedProcedureName) && fields.Count == 0 ? [] : [dataset],
            Parameters = BuildReportParametersFromGrid()
        };
    }

    private void ApplyReportModel(ReportModel model)
    {
        TxtReportName.Text = model.Name;
        TxtAuthor.Text = model.Author ?? string.Empty;
        TxtDescription.Text = model.Description ?? string.Empty;
        TxtConnectionString.Text = model.SourceConnectionString ?? string.Empty;
        TxtOutputPath.Text = model.OutputPath ?? string.Empty;
        TxtReportTitle.Text = model.ReportTitle.Text;
        ChkReportTitleEnabled.IsChecked = model.ReportTitle.Enabled;
        TxtCompanyName.Text = model.CompanyInfo.Text;
        TxtCompanySql.Text = model.CompanyInfo.SqlExpression ?? string.Empty;
        TxtCompanyEndpoint.Text = model.CompanyInfo.BackendEndpoint ?? string.Empty;
        TxtReportVariablesSql.Text = model.ReportVariables.DynamicSource.SqlExpression;
        ApplyReportVariables(model.ReportVariables.Items);
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
        ChkMemorandumBottomLine.IsChecked = model.Memorandum.ShowBottomLine;
        TxtMemorandumHeight.Text = ToUiNumber(model.Memorandum.HeightInCentimeters);
        ChkReportSummaryEnabled.IsChecked = model.ReportSummary.Enabled;
        SetReportBandLayoutMode(CmbReportSummaryLayoutMode, model.ReportSummary.LayoutMode);
        TxtReportSummarySubreport.Text = model.ReportSummary.SubreportPath ?? model.ReportSummary.SubreportName ?? string.Empty;
        TxtReportSummarySubreportServerPath.Text = model.ReportSummary.SubreportServerPath ?? model.ReportSummary.SubreportName ?? string.Empty;
        ChkReportSummaryFallbackInline.IsChecked = model.ReportSummary.FallbackToInline;
        TxtReportSummaryTemplate.Text = model.ReportSummary.TextTemplate;
        ChkReportSummaryTopLine.IsChecked = model.ReportSummary.ShowTopLine;
        TxtReportSummaryHeight.Text = ToUiNumber(model.ReportSummary.HeightInCentimeters);
        ChkPageHeaderEnabled.IsChecked = model.PageHeader.Enabled;
        TxtHeaderLeft.Text = model.PageHeader.LeftText;
        TxtHeaderRight.Text = model.PageHeader.RightText;
        TxtPageHeaderHeight.Text = ToUiNumber(model.PageHeader.HeightInCentimeters);
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

        ApplyReportParameters(model.Parameters);
        var spParameters = BuildStoredProcedureParametersFromModel(model, mainDataset);
        GridParameters.ItemsSource = spParameters;

        this.currentMetadata = mainDataset is null
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

        item.SourceColumnName = sourceColumnName;
        item.FallbackValue = fallbackValue;
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

    private void ApplyReportParameters(IEnumerable<ReportParameter> parameters)
    {
        this.reportParameters.Clear();
        foreach (var parameter in NormalizeParameterOrdinals(parameters))
        {
            parameter.BindToDatasetParameterName = NormalizeOptional(parameter.BindToDatasetParameterName ?? string.Empty)?.TrimStart('@');
            this.reportParameters.Add(parameter);
        }

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

    private static void NormalizeReportParameters(IEnumerable<ReportParameter> parameters)
    {
        var parameterList = parameters.ToList();
        var ordinal = 1;
        foreach (var parameter in parameterList)
        {
            parameter.Name = parameter.Name.Trim().TrimStart('@');
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
            parameter.DisplayFormat = NormalizeOptional(parameter.DisplayFormat ?? string.Empty);
            parameter.LookupSql = NormalizeOptional(parameter.LookupSql ?? string.Empty);
            parameter.DependsOnParameterName = NormalizeDependencyList(parameter.DependsOnParameterName, parameter.Name);
            parameter.BindToDatasetParameterName = NormalizeOptional(parameter.BindToDatasetParameterName ?? string.Empty)?.TrimStart('@');
            parameter.CompareToParameterName = NormalizeOptional(parameter.CompareToParameterName ?? string.Empty)?.TrimStart('@');
            parameter.CompareOperator = NormalizeOptional(parameter.CompareOperator ?? string.Empty);

            if (string.Equals(parameter.CompareToParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase))
            {
                parameter.CompareToParameterName = null;
            }

            if (string.IsNullOrWhiteSpace(parameter.CompareToParameterName))
            {
                parameter.CompareOperator = null;
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


    private void NormalizeFieldDrafts()
    {
        foreach (var field in this.fieldDrafts)
        {
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

    private List<ReportParameter> BuildReportParametersFromGrid()
    {
        SortReportParametersByOrdinal();

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

    private static IEnumerable<ReportParameter> NormalizeParameterOrdinals(IEnumerable<ReportParameter> parameters)
    {
        var ordinal = 1;
        foreach (var parameter in parameters)
        {
            if (parameter.OrdinalNumber <= 0)
            {
                parameter.OrdinalNumber = ordinal;
            }

            ordinal++;
            yield return parameter;
        }
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

    private static string BuildLookupDatasetName(string parameterName)
    {
        var normalized = parameterName.TrimStart('@');
        return "ds" + (string.IsNullOrWhiteSpace(normalized)
            ? "Lookup"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..]);
    }
}
#pragma warning restore CS0618
