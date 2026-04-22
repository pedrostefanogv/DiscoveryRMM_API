using FluentValidation;
using Discovery.Api.Controllers;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Api.Validators;

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
        RuleForEach(x => x.Components!.Printers)
            .SetValidator(new PrinterInfoValidator())
            .When(x => x.Components?.Printers is not null);
        RuleForEach(x => x.Components!.ListeningPorts)
            .SetValidator(new ListeningPortInfoValidator())
            .When(x => x.Components?.ListeningPorts is not null);
        RuleForEach(x => x.Components!.OpenSockets)
            .SetValidator(new OpenSocketInfoValidator())
            .When(x => x.Components?.OpenSockets is not null);
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

public class PrinterInfoValidator : AbstractValidator<PrinterInfo>
{
    public PrinterInfoValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DriverName).MaximumLength(200);
        RuleFor(x => x.PortName).MaximumLength(100);
        RuleFor(x => x.PrinterStatus).MaximumLength(100);
        RuleFor(x => x.ShareName).MaximumLength(200);
        RuleFor(x => x.Location).MaximumLength(200);
    }
}

public class ListeningPortInfoValidator : AbstractValidator<ListeningPortInfo>
{
    public ListeningPortInfoValidator()
    {
        RuleFor(x => x.ProcessName).MaximumLength(256).When(x => x.ProcessName is not null);
        RuleFor(x => x.ProcessPath).MaximumLength(1024).When(x => x.ProcessPath is not null);
        RuleFor(x => x.Protocol).MaximumLength(16).When(x => x.Protocol is not null);
        RuleFor(x => x.Address).MaximumLength(128).When(x => x.Address is not null);
        RuleFor(x => x.Port).InclusiveBetween(0, 65535);
        RuleFor(x => x.ProcessId).GreaterThanOrEqualTo(0);
    }
}

public class OpenSocketInfoValidator : AbstractValidator<OpenSocketInfo>
{
    public OpenSocketInfoValidator()
    {
        RuleFor(x => x.ProcessName).MaximumLength(256).When(x => x.ProcessName is not null);
        RuleFor(x => x.ProcessPath).MaximumLength(1024).When(x => x.ProcessPath is not null);
        RuleFor(x => x.Protocol).MaximumLength(16).When(x => x.Protocol is not null);
        RuleFor(x => x.Family).MaximumLength(16).When(x => x.Family is not null);
        RuleFor(x => x.LocalAddress).MaximumLength(128).When(x => x.LocalAddress is not null);
        RuleFor(x => x.RemoteAddress).MaximumLength(128).When(x => x.RemoteAddress is not null);
        RuleFor(x => x.LocalPort).InclusiveBetween(0, 65535);
        RuleFor(x => x.RemotePort).InclusiveBetween(0, 65535);
        RuleFor(x => x.ProcessId).GreaterThanOrEqualTo(0);
    }
}
