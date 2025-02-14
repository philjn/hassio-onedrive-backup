using hassio_onedrive_backup.Contracts;
using hassio_onedrive_backup.Graph;
using hassio_onedrive_backup.Hass;
using hassio_onedrive_backup.Storage;
using hassio_onedrive_backup.Sync;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using onedrive_backup;
using onedrive_backup.Extensions;
using onedrive_backup.Graph;
using onedrive_backup.Hass;
using onedrive_backup.Models;
using onedrive_backup.Telemetry;
using System.Collections;

namespace hassio_onedrive_backup
{
	public class Program
	{
		private const string clientId = "b8a647cf-eccf-4c7f-a0a6-2cbec5d0b94d";
		private const string addonDirectory = "/data";
		private static readonly List<string> scopes = new() { "Files.ReadWrite.AppFolder" };

		private static Orchestrator _orchestrator;
		private static string _baseDirectory;

		static void Main(string[] args)
		{
			ConsoleLogger logger = new();

			try
			{
				_baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

#if DEBUG
				IHassioClient hassIoClient = new HassioClientMock();
				var addonOptions = AddonOptionsManager.ReadOptions(logger);

#else

            Directory.SetCurrentDirectory(addonDirectory);
            var addonOptions = AddonOptionsManager.ReadOptions(logger);
            string supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")!;
            IHassioClient hassIoClient = new HassioClient(supervisorToken, addonOptions.HassAPITimeoutMinutes, logger);
#endif
				logger.SetLogLevel(addonOptions.LogLevel);
				string timeZoneId = hassIoClient.GetTimeZoneAsync().Result;
				var dateTimeProvider = new DateTimeHelper(timeZoneId);
				logger.SetDateTimeProvider(dateTimeProvider);
				var builder = WebApplication.CreateBuilder(args);
				builder.Services.AddSingleton<IDateTimeProvider>(dateTimeProvider);
				LocalStorage.InitializeTempStorage(logger);
				var addons = hassIoClient.GetAddonsAsync().Result;
				logger.LogVerbose($"Detected Addons: {string.Join(",", addons.Select(addon => addon.Slug))}");
				HassContext hassContext = null;
				var addonInfo = hassIoClient.GetAddonInfo("self").Result;
				hassContext = new HassContext { IngressUrl = addonInfo.DataProperty.IngressUrl, Addons = addons };
				logger.LogVerbose($"Ingress URL: {addonInfo.DataProperty.IngressUrl}");
				builder.Services.AddSingleton(hassContext);

				// Add services to the container.
				builder.Services.AddRazorPages();
				builder.Services.AddServerSideBlazor();
				builder.Services.AddSingleton<ConsoleLogger>(logger);
				builder.Services.AddSingleton(addonOptions);
				builder.Services.AddSingleton<IHassioClient>(hassIoClient);
				

				IGraphHelper graphHelper = new GraphHelper(scopes,clientId, dateTimeProvider, logger);
				builder.Services.AddSingleton<IGraphHelper>(graphHelper);
				builder.Services.AddSingleton<HassOnedriveEntityState>();
				builder.Services.AddSingleton<HassOnedriveFileSyncEntityState>();
				builder.Services.AddSingleton<HassOnedriveFreeSpaceEntityState>();
				builder.Services.AddSingleton<Orchestrator>();
				builder.Services.AddSingleton<SettingsFields>();
				builder.Services.AddSingleton<TelemetryManager>();
				builder.WebHost.UseUrls("http://*:8099");

				if (!builder.Environment.IsDevelopment())
				{
					builder.Logging.ClearProviders();
					// builder.Logging.AddConsole();
				}

				var app = builder.Build();
				_orchestrator = app.Services.GetService<Orchestrator>();
				_orchestrator.Start();

				app.UseIncomingHassFirewallMiddleware();
				if (!app.Environment.IsDevelopment())
				{
					app.UseWhen(ctx => !ctx.Request.Path
					.StartsWithSegments("/_framework/blazor.server.js"),
						subApp => subApp.UseStaticFiles(new StaticFileOptions
						{
							FileProvider = new PhysicalFileProvider($"{_baseDirectory}/wwwroot")
						}));
				}
				else
				{
					logger.LogInfo("Dev Mode");
				}


				app.UsePathBase($"{hassContext?.HeaderIngressPath ?? "/"}");

				// Configure the HTTP request pipeline.
				if (!app.Environment.IsDevelopment())
				{
					app.UseExceptionHandler("/Error");
				}

				app.UseStaticFiles();
				app.UseRouting();

				// app.UseHassUrlExtractor();

				app.MapBlazorHub();
				app.MapFallbackToPage("/_Host");
				app.Run();

			}
			catch (Exception ex)
			{
				logger?.LogError(ex.ToString());
				throw;
			}
		}
	}
}