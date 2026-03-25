using HomelabBackup.Core.Config;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Infrastructure;

public sealed class TransferServiceFactory(ILoggerFactory loggerFactory)
{
    public ITransferService Create(DestinationConfig destination) => destination.Type switch
    {
        DestinationType.Ssh => new SftpService(destination, loggerFactory.CreateLogger<SftpService>()),
        DestinationType.Local => new LocalTransferService(destination, loggerFactory.CreateLogger<LocalTransferService>()),
        DestinationType.Smb => new SmbTransferService(destination, loggerFactory.CreateLogger<SmbTransferService>()),
        _ => throw new ArgumentOutOfRangeException(nameof(destination), $"Unknown destination type: {destination.Type}")
    };
}
