using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChemFlow.Domain.Industrial;

/// <summary>
/// Abstraction for an industrial Modbus TCP (IEC 61158 / MBAP) master client
/// used by distributed Software-in-the-Loop supervisory controllers.
/// </summary>
public interface IModbusTcpClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    Task<ushort[]> ReadHoldingRegistersAsync(
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default);

    Task<bool[]> ReadCoilsAsync(
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default);

    Task WriteSingleRegisterAsync(
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default);

    Task WriteSingleCoilAsync(
        ushort address,
        bool state,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory creating <see cref="IModbusTcpClient"/> sessions on demand.
/// </summary>
public interface IModbusTcpClientFactory
{
    IModbusTcpClient CreateClient();
}
