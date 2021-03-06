﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using Microsoft.ApplicationInsights.AspNetCore;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.SnapshotCollector;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SignService.Services;
using SignService.SigningTools;
using SignService.Utils;

namespace SignService
{
    public class Startup
    {
        readonly IHostingEnvironment environment;
        readonly string contentPath;
        public static string ManifestLocation { get; private set; }
        public Startup(IHostingEnvironment env, IConfiguration configuration)
        {
            environment = env;
            Configuration = configuration;

            contentPath = env.ContentRootPath;
            // If we're running on Azure App Services, we have to invoke from the underlying
            // location due to CSRSS/registration-free COM manifest issues

            // running on azure
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REGION_NAME")))
            {
                var home = Environment.GetEnvironmentVariable("HOME_EXPANDED");
                if (!string.IsNullOrWhiteSpace(home))
                {
                    contentPath = $@"{home}\site\wwwroot";
                }
            }

            var is64bit = IntPtr.Size == 8;
            var basePath = Path.Combine(contentPath, $"tools\\SDK\\{(is64bit ? "x64" : "x86")}");
            ManifestLocation = Path.Combine(contentPath, "tools", "SDK", is64bit ? "x64" : "x86", "SignTool.exe.manifest");

            //
            // Ensure we invoke wintrust!DllMain before we get too far.
            // This will call wintrust!RegisterSipsFromIniFile and read in wintrust.dll.ini
            // to swap out some local SIPs. Internally, wintrust will call LoadLibraryW
            // on each DLL= entry, so we need to also adjust our DLL search path or we'll
            // load unwanted system-provided copies.
            //
            Kernel32.SetDllDirectoryW(basePath);
            Kernel32.LoadLibraryW($@"{basePath}\wintrust.dll");
            Kernel32.LoadLibraryW($@"{basePath}\mssign32.dll");
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Configure SnapshotCollector from application settings
            services.Configure<SnapshotCollectorConfiguration>(Configuration.GetSection(nameof(SnapshotCollectorConfiguration)));

            // Add SnapshotCollector telemetry processor.
            services.AddSingleton<ITelemetryProcessorFactory>(sp => new SnapshotCollectorTelemetryProcessorFactory(sp));

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            // Add framework services.
            services.AddAuthentication(sharedOptions =>
                                       {
                                           //  sharedOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                                           //sharedOptions.DefaultAuthenticateScheme = OpenIdConnectDefaults.AuthenticationScheme;
                                           sharedOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                                       })
                    .AddAzureAdBearer(options => Configuration.Bind("AzureAd", options))
                    .AddAzureAd(options => Configuration.Bind("AzureAd", options))
                    .AddCookie();

            services.AddSession();

            services.Configure<ResourceIds>(Configuration.GetSection("Resources"));
            services.Configure<AdminConfig>(Configuration.GetSection("Admin"));
            services.Configure<WindowsSdkFiles>(ConfigureWindowsSdkFiles);

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<ITelemetryLogger, TelemetryLogger>();
            services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();

            // Add in our User wrapper
            services.AddScoped<IUser, HttpContextUser>();

            // The Key Vault Service must be scoped as the context is per user in the request
            services.AddScoped<IKeyVaultService, KeyVaultService>();

            // Admin service contains per-user context
            services.AddScoped<IUserAdminService, UserAdminService>();
            services.AddScoped<IGraphHttpService, GraphHttpService>();
            services.AddScoped<IKeyVaultAdminService, KeyVaultAdminService>();

            // Code signing tools contain per-user/request data
            services.AddScoped<IAppxFileFactory, AppxFileFactory>();
            services.AddScoped<ICodeSignService, AzureSignToolSignService>();
            services.AddScoped<ICodeSignService, VsixSignService>();
            services.AddScoped<ICodeSignService, MageSignService>();
            services.AddScoped<ICodeSignService, AppInstallerService>();
            services.AddScoped<ICodeSignService, NuGetSignService>();

            services.AddScoped<ISigningToolAggregate, SigningToolAggregate>();

            services.AddMvc()
                    .SetCompatibilityVersion(CompatibilityVersion.Version_2_1); ;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app,
                              IHostingEnvironment env,
                              ILoggerFactory loggerFactory,
                              IServiceProvider serviceProvider,
                              IApplicationConfiguration applicationConfiguration)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            loggerFactory.AddApplicationInsights(serviceProvider, LogLevel.Information);

            TelemetryConfiguration.Active.TelemetryInitializers.Add(new VersionTelemetry());

            // Retreive application specific config from Azure AD
            applicationConfiguration.InitializeAsync().Wait();

            JsonSerializerSettings jsonSettingsProvider()
            {
                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new CoreContractResolver(app.ApplicationServices),
                };
                return settings;
            }

            JsonConvert.DefaultSettings = jsonSettingsProvider;

            // This is here because we need to P/Invoke into clr.dll for _AxlPublicKeyBlobToPublicKeyToken 
            var is64bit = IntPtr.Size == 8;
            var windir = Environment.GetEnvironmentVariable("windir");
            var fxDir = is64bit ? "Framework64" : "Framework";
            var netfxDir = $@"{windir}\Microsoft.NET\{fxDir}\v4.0.30319";
            AddEnvironmentPaths(new[] { netfxDir });
            

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseSession();

            app.UseAuthentication();

            app.UseMvc(routes =>
                       {
                           routes.MapRoute(
                               name: "default",
                               template: "{controller=Home}/{action=Index}/{id?}");
                       });
        }

        static void AddEnvironmentPaths(IEnumerable<string> paths)
        {
            var path = new[] { Environment.GetEnvironmentVariable("PATH") ?? string.Empty };
            var newPath = string.Join(Path.PathSeparator.ToString(), path.Concat(paths));
            Environment.SetEnvironmentVariable("PATH", newPath);
        }

        void ConfigureWindowsSdkFiles(WindowsSdkFiles options)
        {
            var is64bit = IntPtr.Size == 8;
            var basePath = Path.Combine(contentPath, $"tools\\SDK\\{(is64bit ? "x64" : "x86")}");
            options.MakeAppxPath = Path.Combine(basePath, "makeappx.exe");
        }

        class SnapshotCollectorTelemetryProcessorFactory : ITelemetryProcessorFactory
        {
            readonly IServiceProvider _serviceProvider;

            public SnapshotCollectorTelemetryProcessorFactory(IServiceProvider serviceProvider) =>
                _serviceProvider = serviceProvider;

            public ITelemetryProcessor Create(ITelemetryProcessor next)
            {
                var snapshotConfigurationOptions = _serviceProvider.GetService<IOptions<SnapshotCollectorConfiguration>>();
                return new SnapshotCollectorTelemetryProcessor(next, configuration: snapshotConfigurationOptions.Value);
            }
        }

        class VersionTelemetry : ITelemetryInitializer
        {
            public void Initialize(ITelemetry telemetry)
            {
                telemetry.Context.Component.Version = Program.AssemblyInformationalVersion;
            }
        }


    }
}
