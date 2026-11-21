using FluentValidation;

namespace SmartMeter.Application.Common;

public sealed class FleetFilterValidator : AbstractValidator<FleetFilter>
{
    public FleetFilterValidator()
    {
        RuleFor(x => x.ZoneIds.Count).LessThanOrEqualTo(500).WithMessage("Too many zones selected.");
        RuleFor(x => x.NspIds.Count).LessThanOrEqualTo(100).WithMessage("Too many NSPs selected.");
        RuleFor(x => x.ManufacturerIds.Count).LessThanOrEqualTo(200).WithMessage("Too many manufacturers selected.");
        RuleFor(x => x).Must(x => x.InstalledFrom is null || x.InstalledTo is null || x.InstalledFrom <= x.InstalledTo)
            .WithMessage("installedFrom must be on or before installedTo.");
        RuleFor(x => x.Health).IsInEnum();
    }
}
