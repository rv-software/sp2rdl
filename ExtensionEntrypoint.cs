using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;
using sp2rdlGenExtension.Generation;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension
{
    /// <summary>
    /// Extension entrypoint for the VisualStudio.Extensibility extension.
    /// </summary>
    [VisualStudioContribution]
    internal class ExtensionEntrypoint : Extension
    {
        /// <inheritdoc/>
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            Metadata = new(
                    id: "sp2rdlGenExtension.fbe665f0-55f1-4a62-9121-d8f1e9f48fdb",
                    version: this.ExtensionAssemblyVersion,
                    publisherName: "Publisher name",
                    displayName: "sp2rdlGenExtension",
                    description: "Extension description"),
        };

        /// <inheritdoc />
        protected override void InitializeServices(IServiceCollection serviceCollection)
        {
            base.InitializeServices(serviceCollection);

            serviceCollection.AddSingleton<RdlBuilder>();
            serviceCollection.AddSingleton<ReportOutputWriter>();
            serviceCollection.AddSingleton<ReportDialogService>();
            serviceCollection.AddSingleton<SqlIntrospector>();
        }
    }
}
