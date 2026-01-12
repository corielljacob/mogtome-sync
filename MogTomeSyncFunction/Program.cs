using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddAutoMapper(typeof(Program));

builder.Services.AddSingleton(new HttpClient());

await builder.Build().RunAsync();
