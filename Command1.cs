using Microsoft;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;
using sp2rdlGenExtension.Generation;
using sp2rdlGenExtension.Services;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace sp2rdlGenExtension
{
    /// <summary>
    /// Command1 handler.
    /// </summary>
    [VisualStudioContribution]
    internal class Command1 : Command
    {
        private readonly TraceSource logger;
        private readonly SqlIntrospector sqlIntrospector;
        private readonly ReportOutputWriter outputWriter;
        private readonly ReportDialogService reportDialogService;

        /// <summary>
        /// Initializes a new instance of the <see cref="Command1"/> class.
        /// </summary>
        /// <param name="traceSource">Trace source instance to utilize.</param>
        /// <param name="sqlIntrospector">Stored procedure metadata reader.</param>
        /// <param name="outputWriter">Report output writer.</param>
        public Command1(
            TraceSource traceSource,
            SqlIntrospector sqlIntrospector,
            ReportOutputWriter outputWriter,
            ReportDialogService reportDialogService)
        {
            // This optional TraceSource can be used for logging in the command. You can use dependency injection to access
            // other services here as well.
            this.logger = Requires.NotNull(traceSource, nameof(traceSource));
            this.sqlIntrospector = Requires.NotNull(sqlIntrospector, nameof(sqlIntrospector));
            this.outputWriter = Requires.NotNull(outputWriter, nameof(outputWriter));
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
        {
            // Use InitializeAsync for any one-time setup or initialization.
            return base.InitializeAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        {
            try
            {
                _ = this.sqlIntrospector;
                _ = this.outputWriter;

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
                var request = this.reportDialogService.ShowSetupDialog(solutionDirectory, ownerHwnd);
                if (request is null)
                {
                    return;
                }

                await this.Extensibility.Shell().ShowPromptAsync(
                    $"Setup dialog returned request for '{request.ReportModel.Name}'. Generation wiring is next.",
                    PromptOptions.OK,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.TraceEvent(TraceEventType.Error, 0, ex.ToString());
                await this.Extensibility.Shell().ShowPromptAsync(
                    $"Generation setup failed:{Environment.NewLine}{ex.Message}",
                    PromptOptions.OK,
                    cancellationToken);
            }
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
