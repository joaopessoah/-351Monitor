using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Exports;
using M351.Infrastructure.Intervalization;
using M351.Infrastructure.Maintenance;
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

// Jobs de retenção/purga (F4.6 — Seção 7.6): cada serviço na Infrastructure (NpgsqlDataSource +
// RunOnceAsync invocável pelos testes); o Worker só agenda.
builder.Services.AddSingleton<PartitionMaintenanceService>(sp => new PartitionMaintenanceService(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<PartitionMaintenanceService>>()));
builder.Services.AddSingleton<RetentionPurgeService>(sp => new RetentionPurgeService(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<RetentionPurgeService>>()));
builder.Services.AddSingleton<HousekeepingService>(sp => new HousekeepingService(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<HousekeepingService>>()));

// Quartz (Seção 7.6): Intervalization a cada 60 s; DailyAggregation a cada 15 min;
// ExportWorker a cada 15 s ("contínuo" da spec via polling curto — padrão dos demais jobs);
// jobs noturnos de retenção/purga (F4.6) em cron no fuso America/Sao_Paulo (tzdata no container):
// PartitionMaintenance 02:00, RetentionPurge 02:30, Housekeeping 03:00 — escalonados para não
// concorrerem pelo I/O do banco (o PartitionMaintenance dropa partições, o RetentionPurge deleta
// agregados, o Housekeeping varre auth/exports).
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

    // tzdata existe no container (America/Sao_Paulo já usado no staging); CronScheduleBuilder
    // .InTimeZone garante o horário LOCAL BRT mesmo com o container em UTC.
    var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    var partitionKey = new JobKey("partition-maintenance");
    quartz.AddJob<PartitionMaintenanceJob>(options => options.WithIdentity(partitionKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(partitionKey)
        .WithIdentity("partition-maintenance-0200-brt")
        .WithCronSchedule("0 0 2 * * ?", cron => cron.InTimeZone(saoPaulo)));

    var retentionKey = new JobKey("retention-purge");
    quartz.AddJob<RetentionPurgeJob>(options => options.WithIdentity(retentionKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(retentionKey)
        .WithIdentity("retention-purge-0230-brt")
        .WithCronSchedule("0 30 2 * * ?", cron => cron.InTimeZone(saoPaulo)));

    var housekeepingKey = new JobKey("housekeeping");
    quartz.AddJob<HousekeepingJob>(options => options.WithIdentity(housekeepingKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(housekeepingKey)
        .WithIdentity("housekeeping-0300-brt")
        .WithCronSchedule("0 0 3 * * ?", cron => cron.InTimeZone(saoPaulo)));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
await host.RunAsync();
