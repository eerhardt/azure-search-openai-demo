#:sdk Aspire.AppHost.Sdk@13.1.0
#:package Aspire.Hosting.Azure.AppContainers@13.1.0
#:package Aspire.Hosting.Azure.CognitiveServices@13.1.0
#:package Aspire.Hosting.Azure.Search@13.1.0
#:package Aspire.Hosting.Azure.Storage@13.1.0
#:package Aspire.Hosting.JavaScript@13.1.0
#:package Aspire.Hosting.Python@13.1.0

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("env");

var storage = builder.AddAzureStorage("storage");
var content = storage.AddBlobContainer("content");

var search = builder.AddAzureSearch("search");

var openai = builder.AddAzureOpenAI("openai");
var chatModel = openai.AddDeployment("chat", "gpt-4o", "2024-08-06")
    .WithProperties(m => m.SkuCapacity = 30);
var textEmbedding = openai.AddDeployment("text-embedding", "text-embedding-3-large", "1")
    .WithProperties(m => m.SkuCapacity = 200);
    
var backend = builder.AddPythonModule("backend", "./backend", "quart")
    .WithHttpEndpoint(env: "PORT")
    .WithArgs(c =>
    {
        // In run mode, set up for local development with hot reload
        // In publish mode, use a production server
        if (builder.ExecutionContext.IsRunMode)
        {
            c.Args.Add("--app");
            c.Args.Add("main.py");

            c.Args.Add("run");

            var endpoint = ((IResourceWithEndpoints)c.Resource).GetEndpoint("http");
            c.Args.Add("--port");
            c.Args.Add(endpoint.Property(EndpointProperty.TargetPort));

            c.Args.Add("--host");
            c.Args.Add(endpoint.EndpointAnnotation.TargetHost);

            c.Args.Add("--reload");
        }
        else
        {
             c.Args.Add("-k");
             c.Args.Add("uvicorn.workers.UvicornWorker");

             c.Args.Add("-b");
             c.Args.Add("0.0.0.0:8000");
             
             c.Args.Add("main:app");
        }
    })
    .WithAzureEnvironment(openai, search, storage, content, textEmbedding, chatModel)
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        var c = app.Template.Containers.Single().Value;
        if (c != null)
        {
            c.Resources.Cpu = 1.0;
            c.Resources.Memory = "2.0Gi";
        }
    });

var frontend = builder.AddViteApp("frontend", "./frontend")
    .WithReference(backend)
    .WaitFor(backend);

backend.PublishWithContainerFiles(frontend, "./static");

var prepdocs = builder.AddPythonApp("prepdocs", "./backend", "prepdocs.py")
    .WithArgs("../../data/*")
    .WithAzureEnvironment(openai, search, storage, content, textEmbedding, chatModel)
    .WithEnvironment("OPENAI_HOST", "azure")
    .WithEnvironment("USE_LOCAL_PDF_PARSER", "true")
    .WithEnvironment("USE_LOCAL_HTML_PARSER", "true")
    .ExcludeFromManifest()
    .WithExplicitStart();

builder.Build().Run();

static class Extensions
{
    public static IResourceBuilder<T> WithAzureEnvironment<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureOpenAIResource> openai,
        IResourceBuilder<AzureSearchResource> search,
        IResourceBuilder<AzureStorageResource> storage,
        IResourceBuilder<AzureBlobStorageContainerResource> content,
        IResourceBuilder<AzureOpenAIDeploymentResource> textEmbedding,
        IResourceBuilder<AzureOpenAIDeploymentResource> chatModel)
        where T : IResourceWithEnvironment
    {
        return builder
            .WithEnvironment("AZURE_STORAGE_ACCOUNT", storage.Resource.NameOutputReference)
            .WithEnvironment("AZURE_STORAGE_CONTAINER", content.Resource.BlobContainerName)
            .WithEnvironment("AZURE_SEARCH_INDEX", "gptkbindex")
            .WithEnvironment("AZURE_SEARCH_ENDPOINT", search.Resource.UriExpression)
            .WithEnvironment("AZURE_OPENAI_ENDPOINT", openai.Resource.UriExpression)
            .WithEnvironment("AZURE_OPENAI_CHATGPT_MODEL", chatModel.Resource.ModelName)
            .WithEnvironment("AZURE_OPENAI_CHATGPT_DEPLOYMENT", chatModel.Resource.DeploymentName)
            .WithEnvironment("AZURE_OPENAI_EMB_DEPLOYMENT", textEmbedding.Resource.DeploymentName)
            .WithEnvironment("AZURE_OPENAI_EMB_MODEL_NAME", textEmbedding.Resource.ModelName)
            .WithEnvironment("AZURE_OPENAI_EMB_DIMENSIONS", "3072")
            .WithEnvironment(ctx =>
            {
                if (ctx.ExecutionContext.IsPublishMode)
                {
                    ctx.EnvironmentVariables["RUNNING_IN_PRODUCTION"] = "true";
                }
            });
    }
}
