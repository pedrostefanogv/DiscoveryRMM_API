using FluentValidation;
using Meduza.Api.Controllers;
using Meduza.Core.Entities;

namespace Meduza.Api.Validators;

public class HardwareReportRequestValidator : AbstractValidator<HardwareReportRequest>
{
    public HardwareReportRequestValidator()
    {
        RuleForEach(x => x.Disks).SetValidator(new DiskInfoValidator()).When(x => x.Disks is not null);
        RuleForEach(x => x.NetworkAdapters).SetValidator(new NetworkAdapterInfoValidator()).When(x => x.NetworkAdapters is not null);
        RuleForEach(x => x.MemoryModules).SetValidator(new MemoryModuleInfoValidator()).When(x => x.MemoryModules is not null);
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
