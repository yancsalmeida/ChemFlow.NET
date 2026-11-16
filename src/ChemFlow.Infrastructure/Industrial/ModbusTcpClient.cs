using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChemFlow.Domain.Industrial;

namespace ChemFlow.Infrastructure.Industrial;

/// <summary>
/// Asynchronous IEC 61158 / MBAP Modbus TCP master client built on <see cref="TcpClient"/>
/// with exact TCP framing, thread-safe request serialization, and timeout protection.
/// </summary>
public sealed class ModbusTcpClient : IModbusTcpClient
{
    private readonly byte _unitId;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public ModbusTcpClient(byte unitId = 1, TimeSpan? timeout = null)
    {
        _unitId = unitId;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public bool IsConnected => _tcpClient is { Connected: true } && _stream is not null;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Modbus TCP port must be in 1..65535.");

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseStreamsLocked();
            var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            _tcpClient = client;
            _stream = client.GetStream();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        if (count is 0 or > 125)
            throw new ArgumentOutOfRangeException(nameof(count), "Holding register count must be in 1..125.");

        byte[] pdu = new byte[5];
        pdu[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), count);

        byte[] resp = await SendRequestAsync(pdu, cancellationToken).ConfigureAwait(false);
        int expectedByteCount = count * 2;
        if (resp.Length < 2 + expectedByteCount || resp[1] != expectedByteCount)
            throw new InvalidDataException($"Invalid FC 0x03 response length ({resp.Length}).");

        var values = new ushort[count];
        for (int i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(2 + i * 2, 2));

        return values;
    }

    public async Task<bool[]> ReadCoilsAsync(
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        if (count is 0 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(count), "Coil count must be in 1..2000.");

        byte[] pdu = new byte[5];
        pdu[0] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), count);

        byte[] resp = await SendRequestAsync(pdu, cancellationToken).ConfigureAwait(false);
        int expectedByteCount = (count + 7) / 8;
        if (resp.Length < 2 + expectedByteCount || resp[1] != expectedByteCount)
            throw new InvalidDataException($"Invalid FC 0x01 response length ({resp.Length}).");

        var coils = new bool[count];
        for (int i = 0; i < count; i++)
            coils[i] = (resp[2 + i / 8] & (1 << (i % 8))) != 0;

        return coils;
    }

    public async Task WriteSingleRegisterAsync(
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        byte[] pdu = new byte[5];
        pdu[0] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), value);

        byte[] resp = await SendRequestAsync(pdu, cancellationToken).ConfigureAwait(false);
        if (resp.Length < 5)
            throw new InvalidDataException($"Invalid FC 0x06 echo length ({resp.Length}).");
    }

    public async Task WriteSingleCoilAsync(
        ushort address,
        bool state,
        CancellationToken cancellationToken = default)
    {
        byte[] pdu = new byte[5];
        pdu[0] = 0x05;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)(state ? 0xFF00 : 0x0000));

        byte[] resp = await SendRequestAsync(pdu, cancellationToken).ConfigureAwait(false);
        if (resp.Length < 5)
            throw new InvalidDataException($"Invalid FC 0x05 echo length ({resp.Length}).");
    }

    private async Task<byte[]> SendRequestAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is null || _tcpClient is not { Connected: true })
                throw new InvalidOperationException("Modbus TCP client is not connected.");

            ushort txId = unchecked(++_transactionId);
            byte[] frame = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), txId);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0); // Modbus Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)(1 + pdu.Length));
            frame[6] = _unitId;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            await _stream.WriteAsync(frame, timeoutCts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            byte[] header = new byte[7];
            await ReadExactAsync(_stream, header, timeoutCts.Token).ConfigureAwait(false);

            ushort respTxId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            ushort respLen = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            if (respTxId != txId)
                throw new InvalidDataException($"Modbus transaction mismatch: expected {txId}, got {respTxId}.");

            int pduLen = respLen - 1;
            if (pduLen <= 0 || pduLen > 512)
                throw new InvalidDataException($"Invalid Modbus response PDU length: {pduLen}.");

            byte[] respPdu = new byte[pduLen];
            await ReadExactAsync(_stream, respPdu, timeoutCts.Token).ConfigureAwait(false);

            byte fc = pdu[0];
            if (respPdu[0] == (fc | 0x80))
            {
                byte exCode = respPdu.Length > 1 ? respPdu[1] : (byte)0;
                throw new InvalidOperationException($"Modbus exception for FC 0x{fc:X2}: code 0x{exCode:X2}.");
            }

            if (respPdu[0] != fc)
                throw new InvalidDataException($"Unexpected function code: expected 0x{fc:X2}, got 0x{respPdu[0]:X2}.");

            return respPdu;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Remote Modbus TCP server closed the connection.");
            offset += read;
        }
    }

    private void CloseStreamsLocked()
    {
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseStreamsLocked();
        }
        finally
        {
            _ioLock.Release();
            _ioLock.Dispose();
        }
    }
}

/// <summary>
/// Default factory producing live <see cref="ModbusTcpClient"/> connections.
/// </summary>
public sealed class ModbusTcpClientFactory : IModbusTcpClientFactory
{
    public IModbusTcpClient CreateClient() => new ModbusTcpClient();
}
