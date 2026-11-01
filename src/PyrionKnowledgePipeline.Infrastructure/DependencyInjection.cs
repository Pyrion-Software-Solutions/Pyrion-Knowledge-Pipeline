using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PyrionKnowledgePipeline.Application;

namespace PyrionKnowledgePipeline.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddLocalInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));
        services.AddSingleton<IDocumentStore, LocalDocumentStore>();
        services.AddSingleton<ISettingsStore, LocalSettingsStore>();
        services.AddSingleton<IDocumentParser, DocxDocumentParser>();
        services.AddSingleton<IDocumentContractValidator, DocumentContractValidator>();
        services.AddHttpClient("LmStudio", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        services.AddTransient<ILmStudioClient, LmStudioClient>();
        services.AddTransient<PreparationWorkflow>();
        return services;
    }
}
