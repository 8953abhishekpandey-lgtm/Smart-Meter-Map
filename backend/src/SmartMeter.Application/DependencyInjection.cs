using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmartMeter.Application.Common;
using SmartMeter.Application.Common.Behaviors;

namespace SmartMeter.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            // order: metrics/logging -> validation -> cache -> handler
            cfg.AddOpenBehavior(typeof(PerformanceBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(CachingBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        services.AddSingleton<SmartMeterMetrics>();
        return services;
    }
}
