using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Exports;
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
builder.Services.AddSingleton<DailyAggregationService>(sp => new DailyAggregationService(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<DailyAggregationService>>()));

// ExportWorker (F3.5): CSVs assíncronos no diretório COMPARTILHADO com a API
// (Exports:Directory — volume em staging; default relativo em dev local)
builder.Services.AddSingleton<ExportService>(sp => new ExportService(
    sp.GetRequiredService<NpgsqlDataSource>(),
    builder.Configuration[$"{ExportOptions.SectionName}:{nameof(ExportOptions.Directory)}"]
        ?? new ExportOptions().Directory,
    sp.GetRequiredService<ILogger<ExportService>>()));

// Quartz (Seção 7.6): Intervalization a cada 60 s; DailyAggregation a cada 15 min;
// ExportWorker a cada 15 s ("contínuo" da spec via polling curto — padrão dos demais jobs);
// demais jobs (PartitionMaintenance, RetentionPurge) entram nas fases seguintes.
builder.Services.AddQuartz(quartz =>
{
    var intervalizationKey = new JobKey("intervalization");
    quartz.AddJob<IntervalizationJob>(options => options.WithIdentity(intervalizationKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(intervalizationKey)
        .WithIdentity("intervalization-60s")
        .StartNow()
        .WithSimpleSchedule(schedule => schedule.WithIntervalInSeconds(60).RepeatForever()));

    var dailyAggregationKey = new JobKey("daily-aggregation");
    quartz.AddJob<DailyAggregationJob>(options => options.WithIdentity(dailyAggregationKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(dailyAggregationKey)
        .WithIdentity("daily-aggregation-15min")
        .StartNow()
        .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(15).RepeatForever()));

    var exportKey = new JobKey("export");
    quartz.AddJob<ExportJob>(options => options.WithIdentity(exportKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(exportKey)
        .WithIdentity("export-15s")
        .StartNow()
        .WithSimpleSchedule(schedule => schedule.WithIntervalInSeconds(15).RepeatForever()));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
await host.RunAsync();
