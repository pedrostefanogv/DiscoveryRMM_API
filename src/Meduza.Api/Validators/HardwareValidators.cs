using FluentValidation;
using Meduza.Api.Controllers;
using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Api.Validators;

public class HardwareReportRequestValidator : AbstractValidator<HardwareReportRequest>
{
    public HardwareReportRequestValidator()
    {
        RuleFor(x => x.Hostname).Length(2, 100).When(x => x.Hostname is not null);
        RuleFor(x => x.DisplayName).MaximumLength(100).When(x => x.DisplayName is not null);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.OperatingSystem).MaximumLength(100).When(x => x.OperatingSystem is not null);
        RuleFor(x => x.OsVersion).MaximumLength(100).When(x => x.OsVersion is not null);
        RuleFor(x => x.AgentVersion).MaximumLength(100).When(x => x.AgentVersion is not null);
        RuleFor(x => x.LastIpAddress).MaximumLength(45).When(x => x.LastIpAddress is not null);
        RuleFor(x => x.MacAddress).MaximumLength(17).When(x => x.MacAddress is not null);

        RuleForEach(x => x.Components!.Disks)
            .SetValidator(new DiskInfoValidator())
            .When(x => x.Components?.Disks is not null);
        RuleForEach(x => x.Components!.NetworkAdapters)
            .SetValidator(new NetworkAdapterInfoValidator())
            .When(x => x.Components?.NetworkAdapters is not null);
        RuleForEach(x => x.Components!.MemoryModules)
            .SetValidator(new MemoryModuleInfoValidator())
            .When(x => x.Components?.MemoryModules is not null);
    }
}

public class DiskInfoValidator : AbstractValidator<DiskInfo>
{
    public DiskInfoValidator()
    {
        RuleFor(x => x.DriveLetter).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Label).MaximumLength(200);
        RuleFor(x => x.FileSystem).MaximumLength(50);
        RuleFor(x => x.MediaType).MaximumLength(50);
    }
}

public class NetworkAdapterInfoValidator : AbstractValidator<NetworkAdapterInfo>
{
    public NetworkAdapterInfoValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.MacAddress).MaximumLength(32);
        RuleFor(x => x.IpAddress).MaximumLength(45);
        RuleFor(x => x.SubnetMask).MaximumLength(45);
        RuleFor(x => x.Gateway).MaximumLength(45);
        RuleFor(x => x.DnsServers).MaximumLength(500);
        RuleFor(x => x.AdapterType).MaximumLength(50);
        RuleFor(x => x.Speed).MaximumLength(50);
    }
}

public class MemoryModuleInfoValidator : AbstractValidator<MemoryModuleInfo>
{
    public MemoryModuleInfoValidator()
    {
        RuleFor(x => x.Slot).MaximumLength(50);
        RuleFor(x => x.MemoryType).MaximumLength(50);
        RuleFor(x => x.Manufacturer).MaximumLength(200);
        RuleFor(x => x.PartNumber).MaximumLength(100);
        RuleFor(x => x.SerialNumber).MaximumLength(100);
    }
}
