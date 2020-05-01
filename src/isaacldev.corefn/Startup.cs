using isaacldev.corefn.Domain;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


[assembly: FunctionsStartup(typeof(isaacldev.corefn.Startup))]
namespace isaacldev.corefn
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddApplicationInsightsTelemetry();
            var configDescriptor = builder.Services.SingleOrDefault(tc => tc.ServiceType == typeof(TelemetryConfiguration));
            if (configDescriptor?.ImplementationFactory != null)
            {
                var implFactory = configDescriptor.ImplementationFactory;
                builder.Services.AddSingleton<ITelemetryInitializer, CloneIPAddress>();
                builder.Services.AddSingleton(provider =>
                {
                    if (implFactory.Invoke(provider) is TelemetryConfiguration config)
                    {
                        // Construct a new TelemetryConfiguration based off the existing config
                        var newConfig = new TelemetryConfiguration(config.InstrumentationKey, config.TelemetryChannel)
                        {
                            ApplicationIdProvider = config.ApplicationIdProvider
                        };
                        config.TelemetryInitializers.ToList().ForEach(initializer => newConfig.TelemetryInitializers.Add(initializer));

                        newConfig.TelemetryProcessorChainBuilder.Build();
                        newConfig.TelemetryProcessors.OfType<ITelemetryModule>().ToList().ForEach(module => module.Initialize(newConfig));
                        return newConfig;
                    }
                    return null;
                });
            }
        }
    }
}