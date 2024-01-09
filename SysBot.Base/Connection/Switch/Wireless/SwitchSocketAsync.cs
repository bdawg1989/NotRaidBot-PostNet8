using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchOffsetType;

namespace SysBot.Base
{
    /// <summary>
    /// Connection to a Nintendo Switch hosting the sys-module via a socket (WiFi).
    /// </summary>
    /// <remarks>
    /// Interactions are performed asynchronously.
    /// </remarks>
    public sealed class SwitchSocketAsync : SwitchSocket, ISwitchConnectionAsync
    {
        public SwitchSocketAsync(IWirelessConnectionConfig cfg) : base(cfg) { }

        public override void Connect()
        {
            if (Connected)
            {
                Log("Already connected, skipping reconnection.");
                return;
            }

            Log("Connecting to device...");
            try
            {
                Connection.Connect(Info.IP, Info.Port);
                Log("Connected!");
                Label = Name;
            }
            catch (Exception ex)
            {
                Log($"Error during connection: {ex.Message}");
                // You might want to rethrow the exception or handle it based on your application's needs
            }
        }

        public override void Reset()
        {
            var ip = Info.IP;
            if (Connected)
                Disconnect();

            // Socket will update "Connected" condition itself based on the success of the most recent read/write call.
            // We want to ensure we initialize the Socket if we're resetting after a crash.
            InitializeSocket();
            Log("Connecting to device...");
            var address = Dns.GetHostAddresses(ip);
            foreach (IPAddress adr in address)
            {
                IPEndPoint ep = new(adr, Info.Port);
                try
                {
                    Connection.Connect(ep);
                }
                catch
                {
                    return;
                }
                Log("Connected!");
            }
        }

        public void Reconnect()
        {
            Disconnect(); // Disconnect first
            Thread.Sleep(1000); // Optional: wait a short period before reconnecting
            Connect(); // Then reconnect
        }

        public override void Disconnect()
        {
            if (!Connected)
            {
                Log("Already disconnected.");
                return;
            }

            Log("Disconnecting from device...");
            try
            {
                Connection.Shutdown(SocketShutdown.Both);
                Connection.Close(); // Ensures the socket is fully closed
            }
            catch (Exception ex)
            {
                Log($"Error during disconnection: {ex.Message}");
            }
            finally
            {
                InitializeSocket(); // Reinitialize the socket for future connections
                Log("Disconnected and reset socket.");
            }
        }

        private int Read(byte[] buffer)
        {
            int br = Connection.Receive(buffer, 0, 1, SocketFlags.None);
            while (buffer[br - 1] != (byte)'\n')
                br += Connection.Receive(buffer, br, 1, SocketFlags.None);
            return br;
        }

        /// <summary> Only call this if you are sending small commands. </summary>
        public async Task<int> SendAsync(byte[] buffer, CancellationToken token)
        {
            int maxRetries = 3;
            int attempts = 0;
            while (attempts < maxRetries)
            {
                try
                {
                    return await Task.Run(() => Connection.Send(buffer), token).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    Log($"SendAsync failed: {ex.Message}. Attempt {attempts + 1} of {maxRetries}");
                    if (attempts >= maxRetries - 1) // Before the last retry, attempt to reconnect
                    {
                        Log("Attempting to reconnect before the final retry.");
                        Reconnect();
                    }

                    attempts++;
                    if (attempts >= maxRetries) throw;
                    await Task.Delay(2000, token); // Wait before retrying
                }
            }
            return 0;
        }

        private async Task<byte[]> ReadBytesFromCmdAsync(byte[] cmd, int length, CancellationToken token)
        {
            int maxRetries = 3; // Maximum number of retries
            int delayBetweenRetries = 2000; // Delay in milliseconds between retries
            int attempts = 0;

            while (attempts < maxRetries)
            {
                try
                {
                    await SendAsync(cmd, token).ConfigureAwait(false);

                    var buffer = new byte[(length * 2) + 1];
                    int bytesRead = await Task.Run(() => Read(buffer), token).ConfigureAwait(false);

                    // Check if the expected number of bytes were read
                    if (bytesRead < buffer.Length)
                    {
                        throw new InvalidOperationException("Incomplete data read from the socket.");
                    }

                    return Decoder.ConvertHexByteStringToBytes(buffer);
                }
                catch (Exception ex)
                {
                    Log($"ReadBytesFromCmdAsync failed on attempt {attempts + 1}: {ex.Message}");

                    if (attempts >= maxRetries - 1) // Before the last retry, attempt to reconnect
                    {
                        Log("Attempting to reconnect before the final retry.");
                        Reconnect();
                    }

                    attempts++;
                    if (attempts >= maxRetries)
                    {
                        Log("Maximum retry attempts reached, throwing exception.");
                        throw; // Rethrow the exception if all retries fail
                    }

                    await Task.Delay(delayBetweenRetries, token); // Wait for a while before retrying
                }
            }

            throw new InvalidOperationException("Failed to read bytes from command after several attempts.");
        }

        public async Task<byte[]> ReadBytesAsync(uint offset, int length, CancellationToken token) => await Read(offset, length, Heap, token).ConfigureAwait(false);
        public async Task<byte[]> ReadBytesMainAsync(ulong offset, int length, CancellationToken token) => await Read(offset, length, Main, token).ConfigureAwait(false);
        public async Task<byte[]> ReadBytesAbsoluteAsync(ulong offset, int length, CancellationToken token) => await Read(offset, length, Absolute, token).ConfigureAwait(false);

        public async Task<byte[]> ReadBytesMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => await ReadMulti(offsetSizes, Heap, token).ConfigureAwait(false);
        public async Task<byte[]> ReadBytesMainMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => await ReadMulti(offsetSizes, Main, token).ConfigureAwait(false);
        public async Task<byte[]> ReadBytesAbsoluteMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => await ReadMulti(offsetSizes, Absolute, token).ConfigureAwait(false);

        public async Task WriteBytesAsync(byte[] data, uint offset, CancellationToken token) => await Write(data, offset, Heap, token).ConfigureAwait(false);
        public async Task WriteBytesMainAsync(byte[] data, ulong offset, CancellationToken token) => await Write(data, offset, Main, token).ConfigureAwait(false);
        public async Task WriteBytesAbsoluteAsync(byte[] data, ulong offset, CancellationToken token) => await Write(data, offset, Absolute, token).ConfigureAwait(false);

        public async Task<ulong> GetMainNsoBaseAsync(CancellationToken token)
        {
            byte[] baseBytes = await ReadBytesFromCmdAsync(SwitchCommand.GetMainNsoBase(), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(baseBytes, 0, 8);
            return BitConverter.ToUInt64(baseBytes, 0);
        }

        public async Task<ulong> GetHeapBaseAsync(CancellationToken token)
        {
            var baseBytes = await ReadBytesFromCmdAsync(SwitchCommand.GetHeapBase(), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(baseBytes, 0, 8);
            return BitConverter.ToUInt64(baseBytes, 0);
        }

        public async Task<string> GetTitleID(CancellationToken token)
        {
            var bytes = await ReadRaw(SwitchCommand.GetTitleID(), 17, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).Trim();
        }

        public async Task<string> GetBotbaseVersion(CancellationToken token)
        {
            // Allows up to 9 characters for version, and trims extra '\0' if unused.
            var bytes = await ReadRaw(SwitchCommand.GetBotbaseVersion(), 10, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).Trim('\0');
        }

        public async Task<string> GetGameInfo(string info, CancellationToken token)
        {
            var bytes = await ReadRaw(SwitchCommand.GetGameInfo(info), 17, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).Trim(new char[] { '\0', '\n' });
        }

        public async Task<bool> IsProgramRunning(ulong pid, CancellationToken token)
        {
            var bytes = await ReadRaw(SwitchCommand.IsProgramRunning(pid), 17, token).ConfigureAwait(false);
            return ulong.TryParse(Encoding.ASCII.GetString(bytes).Trim(), out var value) && value == 1;
        }

        private async Task<byte[]> Read(ulong offset, int length, SwitchOffsetType type, CancellationToken token)
        {
            var method = type.GetReadMethod();
            if (length <= MaximumTransferSize)
            {
                var cmd = method(offset, length);
                return await ReadBytesFromCmdAsync(cmd, length, token).ConfigureAwait(false);
            }

            byte[] result = new byte[length];
            for (int i = 0; i < length; i += MaximumTransferSize)
            {
                int len = MaximumTransferSize;
                int delta = length - i;
                if (delta < MaximumTransferSize)
                    len = delta;

                var cmd = method(offset + (uint)i, len);
                var bytes = await ReadBytesFromCmdAsync(cmd, len, token).ConfigureAwait(false);
                bytes.CopyTo(result, i);
                await Task.Delay((MaximumTransferSize / DelayFactor) + BaseDelay, token).ConfigureAwait(false);
            }
            return result;
        }

        private async Task<byte[]> ReadMulti(IReadOnlyDictionary<ulong, int> offsetSizes, SwitchOffsetType type, CancellationToken token)
        {
            var method = type.GetReadMultiMethod();
            var cmd = method(offsetSizes);
            var totalSize = offsetSizes.Values.Sum();
            return await ReadBytesFromCmdAsync(cmd, totalSize, token).ConfigureAwait(false);
        }

        private async Task Write(byte[] data, ulong offset, SwitchOffsetType type, CancellationToken token)
        {
            var method = type.GetWriteMethod();
            if (data.Length <= MaximumTransferSize)
            {
                var cmd = method(offset, data);
                await SendAsync(cmd, token).ConfigureAwait(false);
                return;
            }
            int byteCount = data.Length;
            for (int i = 0; i < byteCount; i += MaximumTransferSize)
            {
                var slice = data.SliceSafe(i, MaximumTransferSize);
                var cmd = method(offset + (uint)i, slice);
                await SendAsync(cmd, token).ConfigureAwait(false);
                await Task.Delay((MaximumTransferSize / DelayFactor) + BaseDelay, token).ConfigureAwait(false);
            }
        }

        public async Task<byte[]> ReadRaw(byte[] command, int length, CancellationToken token)
        {
            await SendAsync(command, token).ConfigureAwait(false);
            var buffer = new byte[length];
            var _ = Read(buffer);
            return buffer;
        }

        public async Task SendRaw(byte[] command, CancellationToken token)
        {
            await SendAsync(command, token).ConfigureAwait(false);
        }

        public async Task<byte[]> PointerPeek(int size, IEnumerable<long> jumps, CancellationToken token)
        {
            return await ReadBytesFromCmdAsync(SwitchCommand.PointerPeek(jumps, size), size, token).ConfigureAwait(false);
        }

        public async Task PointerPoke(byte[] data, IEnumerable<long> jumps, CancellationToken token)
        {
            await SendAsync(SwitchCommand.PointerPoke(jumps, data), token).ConfigureAwait(false);
        }

        public async Task<ulong> PointerAll(IEnumerable<long> jumps, CancellationToken token)
        {
            var offsetBytes = await ReadBytesFromCmdAsync(SwitchCommand.PointerAll(jumps), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(offsetBytes, 0, 8);
            return BitConverter.ToUInt64(offsetBytes, 0);
        }

        public async Task<ulong> PointerRelative(IEnumerable<long> jumps, CancellationToken token)
        {
            var offsetBytes = await ReadBytesFromCmdAsync(SwitchCommand.PointerRelative(jumps), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(offsetBytes, 0, 8);
            return BitConverter.ToUInt64(offsetBytes, 0);
        }

        public async Task<byte[]> PixelPeek(CancellationToken token)
        {
            await SendAsync(SwitchCommand.PixelPeek(), token).ConfigureAwait(false);
            await Task.Delay(Connection.ReceiveBufferSize / DelayFactor + BaseDelay, token).ConfigureAwait(false);

            var data = await FlexRead(token).ConfigureAwait(false);
            var result = Array.Empty<byte>();
            try
            {
                result = Decoder.ConvertHexByteStringToBytes(data);
            }
            catch (Exception e)
            {
                LogError($"Malformed screenshot data received:\n{e.Message}");
            }

            return result;
        }

        private async Task<byte[]> FlexRead(CancellationToken token)
        {
            List<byte> flexBuffer = new();
            int available = Connection.Available;
            Connection.ReceiveTimeout = 1_000;

            do
            {
                byte[] buffer = new byte[available];
                try
                {
                    Connection.Receive(buffer, available, SocketFlags.None);
                    flexBuffer.AddRange(buffer);
                }
                catch (Exception ex)
                {
                    LogError($"Socket exception thrown while receiving data:\n{ex.Message}");
                    return Array.Empty<byte>();
                }

                await Task.Delay(MaximumTransferSize / DelayFactor + BaseDelay, token).ConfigureAwait(false);
                available = Connection.Available;
            } while (flexBuffer.Count == 0 || flexBuffer.Last() != (byte)'\n');

            Connection.ReceiveTimeout = 0;
            return flexBuffer.ToArray();
        }

        public async Task<long> GetUnixTime(CancellationToken token)
        {
            var result = await ReadBytesFromCmdAsync(SwitchCommand.GetUnixTime(), 8, token).ConfigureAwait(false);
            Array.Reverse(result);
            return BitConverter.ToInt64(result, 0);
        }
    }
}