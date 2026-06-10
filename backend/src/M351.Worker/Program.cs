using M351.Infrastructure.Intervalization;
using M351.Worker;
using Npgsql;
using Quartz;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(loggerConfiguration =>
    loggerConfiguration.ReadFrom.Configuration(builder.Configuration));

// NpgsqlDataSource singleton (mesmo padrão da API); a infra injeta ConnectionStrings__Default
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default ausente no worker.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IntervalizationService>(sp => new IntervalizationService(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<IntervalizationService>>()));

// Quartz (Seção 7.6): Intervalization a cada 60 s; demais jobs (DailyAggregation,
// PartitionMaintenance, RetentionPurge) entram nas fases seguintes.
builder.Services.AddQuartz(quartz =>
{
    var jobKey = new JobKey("intervalization");
    quartz.AddJob<IntervalizationJob>(options => options.WithIdentity(jobKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(jobKey)
        .WithIdentity("intervalization-60s")
        .StartNow()
        .WithSimpleSchedule(schedule => schedule.WithIntervalInSeconds(60).RepeatForever()));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
await host.RunAsync();
