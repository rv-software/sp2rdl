using Microsoft;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;
using sp2rdlGenExtension.Services;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace sp2rdlGenExtension
{
    /// <summary>
    /// Visual Studio command that opens the report generator setup dialog.
    /// </summary>
    [VisualStudioContribution]
    internal class Command1 : Command
    {
        private readonly TraceSource logger;
        private readonly ReportDialogService reportDialogService;

        /// <summary>
        /// Initializes a new instance of the <see cref="Command1"/> class.
        /// </summary>
        /// <param name="traceSource">Trace source instance to utilize.</param>
        /// <param name="reportDialogService">Service that owns WPF dialog creation.</param>
        public Command1(TraceSource traceSource, ReportDialogService reportDialogService)
        {
            this.logger = Requires.NotNull(traceSource, nameof(traceSource));
            this.reportDialogService = Requires.NotNull(reportDialogService, nameof(reportDialogService));
        }

        /// <inheritdoc />
        public override CommandConfiguration CommandConfiguration => new("%sp2rdlGenExtension.Command1.DisplayName%")
        {
            // Use this object initializer to set optional parameters for the command. The required parameter,
            // displayName, is set above. DisplayName is localized and references an entry in .vsextension\string-resources.json.
            Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
            Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu]
        };

        /// <inheritdoc />
        public override Task InitializeAsync(CancellationToken cancellationToken)
            => base.InitializeAsync(cancellationToken);

        /// <inheritdoc />
        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        {
            try
            {
                var solutionDirectory = await ResolveSolutionDirectoryAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(solutionDirectory))
                {
                    await this.Extensibility.Shell().ShowPromptAsync(
                        "Could not determine the current solution folder. Open a solution and try again.",
                        PromptOptions.OK,
                        cancellationToken);
                    return;
                }

                var ownerHwnd = GetForegroundWindow();
                // Setup dialog drives generation in-place; it stays open across multiple
                // Generate clicks and writes the output itself, so nothing to do here on close.
                _ = this.reportDialogService.ShowSetupDialog(solutionDirectory, ownerHwnd);
            }
            catch (Exception ex)
            {
                var error = BuildSafeExceptionSummary(ex);
                this.logger.TraceEvent(TraceEventType.Error, 0, error);
                await this.Extensibility.Shell().ShowPromptAsync(
                    $"Generation setup failed:{Environment.NewLine}{error}",
                    PromptOptions.OK,
                    cancellationToken);
            }
        }

        private static string BuildSafeExceptionSummary(Exception ex)
        {
            var details = new List<string>
            {
                ex.GetType().FullName ?? ex.GetType().Name,
                $"HResult: 0x{ex.HResult:X8}"
            };

            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                details.Add(ex.StackTrace);
            }

            return string.Join(Environment.NewLine, details);
        }

        private async Task<string?> ResolveSolutionDirectoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                var results = await this.Extensibility.Workspaces().QuerySolutionAsync(
                    query => query,
                    cancellationToken);

                var solution = results.FirstOrDefault();
                if (solution is not null && !string.IsNullOrWhiteSpace(solution.Path))
                {
                    return Path.GetDirectoryName(solution.Path);
                }
            }
            catch
            {
            }

            return ResolveSolutionDirectoryFallback();
        }

        private static string ResolveSolutionDirectoryFallback()
        {
            var candidates = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            };

            foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var directory = new DirectoryInfo(candidate);
                while (directory is not null)
                {
                    if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any())
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            return Directory.GetCurrentDirectory();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
