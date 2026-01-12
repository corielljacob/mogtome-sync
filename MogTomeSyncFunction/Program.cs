using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddAutoMapper(typeof(Program));

builder.Services.AddSingleton(new HttpClient());

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

await builder.Build().RunAsync();
