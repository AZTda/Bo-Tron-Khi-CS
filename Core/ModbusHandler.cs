using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;

namespace Bo_Tron_Khi_CS
{
    // ===================================================
    // MODBUS RESULT — structured return instead of exceptions
    // ===================================================
    public class ModbusResult<T>
    {
        public bool Success { get; set; }
        public T Data { get; set; }
        public string ErrorMessage { get; set; }
        public int RetryCount { get; set; } // how many retries before success (0 = first try)

        public static ModbusResult<T> Ok(T data, int retries = 0) =>
            new ModbusResult<T> { Success = true, Data = data, RetryCount = retries };

        public static ModbusResult<T> Fail(string error, int retries = 0) =>
            new ModbusResult<T> { Success = false, ErrorMessage = error, RetryCount = retries };
    }

    // ===================================================
    // CONNECTION HEALTH EVENT
    // ===================================================
    public class ConnectionHealthEventArgs : EventArgs
    {
        public int ConsecutiveErrors { get; }
        public bool IsHealthy { get; }
        public string LastError { get; }
        public ConnectionHealthEventArgs(int errors, bool healthy, string lastErr)
        {
            ConsecutiveErrors = errors;
            IsHealthy = healthy;
            LastError = lastErr;
        }
    }

    // ===================================================
    // MODBUS HANDLER — Robust Communication Engine
    // ===================================================
    public class ModbusHandler
    {
        private SerialPort _serialPort;
        private TcpClient _tcpClient;
        private NetworkStream _tcpStream;
        private readonly object _lock = new object();
        private ushort _transactionId = 0;

        // Event-driven RX buffer (replaces blocking Read)
        private readonly List<byte> _rxBuffer = new List<byte>();
        private readonly ManualResetEventSlim _rxSignal = new ManualResetEventSlim(false);
        private readonly object _rxLock = new object();

        // RTU inter-frame timing
        private DateTime _lastTransactionEnd = DateTime.MinValue;
        private double _silentIntervalMs = 2.0; // 3.5 char times, recalculated on connect

        // Retry configuration
        public int MaxRetries { get; set; } = 3;
        private static readonly int[] BackoffMs = { 50, 100, 200 };

        // Health monitoring
        private int _consecutiveErrors = 0;
        private const int HealthThreshold = 5;
        public int ConsecutiveErrors => _consecutiveErrors;
        public event EventHandler<ConnectionHealthEventArgs> ConnectionHealthChanged;

        // Config properties
        public string Port { get; set; } = "Virtual Sim";
        public int Baudrate { get; set; } = 19200;
        public string Parity { get; set; } = "E";
        public double Timeout { get; set; } = 0.5; // seconds
        public bool IsConnected { get; private set; } = false;
        public bool IsTcp { get; set; } = false;
        public string TcpIp { get; set; } = "127.0.0.1";
        public int TcpPort { get; set; } = 502;

        // Sim variables (for Virtual Sim)
        private readonly float[] _simSccmPV = new float[6];
        private readonly float[] _simSccmSP = new float[6] { 0, 0, 0, 0, 0, 0 };
        private readonly ushort[] _simDacEn = new ushort[6] { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _simMinSccm = new float[6] { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _simMaxSccm = new float[6] { 500, 500, 50, 200, 100, 100 };
        private readonly float[] _simMinV = new float[6] { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _simMaxV = new float[6] { 5000, 5000, 5000, 5000, 5000, 5000 };
        private ushort _simRelay1 = 0;
        private ushort _simRelay2 = 0;

        // Simulator: concentration/flow control state (mirrors firmware concMfcSetValue_t)
        private ushort _simMode = 1;    // 1=Concentration, 0=Direct Sccm
        private ushort _simIsRun = 0;
        private float  _simGas1Ppm = 0;
        private float  _simGas2Ppm = 0;
        private float  _simGas3Ppm = 0;
        private float  _simTotalFlow = 400.0f;
        private float  _simCo1 = 1000.0f; // bottle concentration gas1
        private float  _simCo2 = 1000.0f;
        private float  _simCo3 = 1000.0f;
        private float  _simTempSet = 25.0f; // pcStatus.tempSet

        private float _simE5ccPV = 25.0f;
        private float _simE5ccSP = 25.0f;
        private ushort _simE5ccRunStop = 1; // 1 = Stop, 0 = Run
        private ushort _simE5ccAT = 0;
        private ushort _simE5ccStatus = 1; // bit 0 = 1 (Stop)
        private ushort _simAlm1 = 500; // 50.0C
        private ushort _simAlm2 = 500;
        private ushort _simP = 100; // 10.0
        private ushort _simI = 240;
        private ushort _simD = 40;
        private ushort _simCtrlPeriod = 12;
        private ushort _simMvHi = 1000;
        private ushort _simMvLo = 0;
        private ushort _simInputShift = 0;
        private ushort _simSpHi = 3000;
        private ushort _simSpLo = 0;

        private readonly Random _rand = new Random();

        // ===================================================
        // CONNECTION MANAGEMENT
        // ===================================================
        public bool Connect()
        {
            lock (_lock)
            {
                Disconnect();
                if (Port == "Virtual Sim")
                {
                    IsConnected = true;
                    _consecutiveErrors = 0;
                    return true;
                }

                try
                {
                    if (IsTcp)
                    {
                        _tcpClient = new TcpClient();
                        var result = _tcpClient.BeginConnect(TcpIp, TcpPort, null, null);
                        bool success = result.AsyncWaitHandle.WaitOne((int)(Timeout * 1000));
                        if (!success)
                        {
                            _tcpClient.Close();
                            throw new TimeoutException("TCP Connection timeout");
                        }
                        _tcpClient.EndConnect(result);
                        _tcpStream = _tcpClient.GetStream();
                        _tcpStream.ReadTimeout = (int)(Timeout * 1000);
                        _tcpStream.WriteTimeout = (int)(Timeout * 1000);
                    }
                    else
                    {
                        System.IO.Ports.Parity p = System.IO.Ports.Parity.Even;
                        if (Parity == "O") p = System.IO.Ports.Parity.Odd;
                        else if (Parity == "N") p = System.IO.Ports.Parity.None;

                        _serialPort = new SerialPort(Port, Baudrate, p, 8, StopBits.One)
                        {
                            ReadTimeout = (int)(Timeout * 1000),
                            WriteTimeout = (int)(Timeout * 1000),
                            ReceivedBytesThreshold = 1 // fire DataReceived ASAP
                        };

                        // Event-driven: wire up DataReceived BEFORE opening
                        _serialPort.DataReceived += OnSerialDataReceived;
                        _serialPort.Open();

                        // Calculate RTU inter-frame silent interval
                        // Modbus RTU: 3.5 character times, each char = 11 bits (start + 8data + parity + stop)
                        _silentIntervalMs = (3.5 * 11.0 / Baudrate) * 1000.0;
                        if (_silentIntervalMs < 1.75) _silentIntervalMs = 1.75; // minimum 1.75ms per Modbus spec for high baudrates
                    }

                    IsConnected = true;
                    _consecutiveErrors = 0;
                    return true;
                }
                catch (Exception)
                {
                    IsConnected = false;
                    return false;
                }
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                try
                {
                    if (_serialPort != null)
                    {
                        _serialPort.DataReceived -= OnSerialDataReceived;
                        _serialPort.Close();
                    }
                    _serialPort = null;

                    _tcpStream?.Close();
                    _tcpStream = null;

                    _tcpClient?.Close();
                    _tcpClient = null;
                }
                catch { }
                IsConnected = false;
            }
        }

        // ===================================================
        // EVENT-DRIVEN SERIAL RX
        // ===================================================
        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = sender as SerialPort;
                if (sp == null || !sp.IsOpen) return;

                int available = sp.BytesToRead;
                if (available <= 0) return;

                byte[] chunk = new byte[available];
                int read = sp.Read(chunk, 0, available);

                if (read > 0)
                {
                    lock (_rxLock)
                    {
                        _rxBuffer.AddRange(new ArraySegment<byte>(chunk, 0, read));
                        _rxSignal.Set(); // Wake up anyone waiting for data
                    }
                }
            }
            catch { /* port closed during read — safe to ignore */ }
        }

        /// <summary>
        /// Wait for at least 'count' bytes to arrive in the RX buffer.
        /// Uses ManualResetEventSlim for efficient wake-on-data instead of blocking Read.
        /// Returns the bytes or null on timeout/cancellation.
        /// </summary>
        private byte[] WaitForBytes(int count, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                lock (_rxLock)
                {
                    if (_rxBuffer.Count >= count)
                    {
                        byte[] result = new byte[count];
                        _rxBuffer.CopyTo(0, result, 0, count);
                        _rxBuffer.RemoveRange(0, count);
                        return result;
                    }
                    _rxSignal.Reset(); // prepare to wait
                }

                // Wait for new data or timeout, whichever comes first
                int remaining = Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds);
                _rxSignal.Wait(Math.Min(remaining, 50), ct); // check every 50ms max
            }

            return null; // timeout
        }

        /// <summary>
        /// Read exactly 'count' bytes from TCP stream with timeout and cancellation.
        /// </summary>
        private byte[] TcpReadBytes(int count, int timeoutMs, CancellationToken ct)
        {
            byte[] buffer = new byte[count];
            int total = 0;
            var sw = Stopwatch.StartNew();

            while (total < count && sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    int read = _tcpStream.Read(buffer, total, count - total);
                    if (read <= 0) break;
                    total += read;
                }
                catch (IOException) { break; }
            }

            return total >= count ? buffer : null;
        }

        // ===================================================
        // RTU INTER-FRAME TIMING
        // ===================================================
        private void EnforceInterFrameDelay()
        {
            if (IsTcp) return; // TCP/IP doesn't need inter-frame delay

            double elapsed = (DateTime.Now - _lastTransactionEnd).TotalMilliseconds;
            if (elapsed < _silentIntervalMs)
            {
                int waitMs = (int)Math.Ceiling(_silentIntervalMs - elapsed);
                if (waitMs > 0) Thread.Sleep(waitMs);
            }
        }

        // ===================================================
        // PUBLIC API — New (ModbusResult<T>)
        // ===================================================
        public ModbusResult<ushort[]> TryReadHoldingRegisters(byte slave, ushort startAddress, ushort count, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                UpdateSimulation();
                return ModbusResult<ushort[]>.Ok(SimReadHolding(slave, startAddress, count));
            }
            return PerformReadTransaction(slave, 0x03, startAddress, count, ct);
        }

        public ModbusResult<ushort[]> TryReadInputRegisters(byte slave, ushort startAddress, ushort count, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                UpdateSimulation();
                return ModbusResult<ushort[]>.Ok(SimReadInput(slave, startAddress, count));
            }
            return PerformReadTransaction(slave, 0x04, startAddress, count, ct);
        }

        public ModbusResult<bool> TryWriteSingleRegister(byte slave, ushort address, ushort value, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                SimWriteSingle(slave, address, value);
                return ModbusResult<bool>.Ok(true);
            }
            return PerformWriteTransaction(slave, 0x06, address, value, null, ct);
        }

        public ModbusResult<bool> TryWriteMultipleRegisters(byte slave, ushort startAddress, ushort[] values, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                SimWriteMultiple(slave, startAddress, values);
                return ModbusResult<bool>.Ok(true);
            }
            return PerformWriteTransaction(slave, 0x10, startAddress, (ushort)values.Length, values, ct);
        }

        // ===================================================
        // PUBLIC API — Legacy (backward-compatible wrappers)
        // These throw exceptions on failure like the old API
        // ===================================================
        public ushort[] ReadHoldingRegisters(byte slave, ushort startAddress, ushort count)
        {
            var result = TryReadHoldingRegisters(slave, startAddress, count);
            if (!result.Success) throw new IOException(result.ErrorMessage);
            return result.Data;
        }

        public ushort[] ReadInputRegisters(byte slave, ushort startAddress, ushort count)
        {
            var result = TryReadInputRegisters(slave, startAddress, count);
            if (!result.Success) throw new IOException(result.ErrorMessage);
            return result.Data;
        }

        public void WriteSingleRegister(byte slave, ushort address, ushort value)
        {
            var result = TryWriteSingleRegister(slave, address, value);
            if (!result.Success) throw new IOException(result.ErrorMessage);
        }

        public void WriteMultipleRegisters(byte slave, ushort startAddress, ushort[] values)
        {
            var result = TryWriteMultipleRegisters(slave, startAddress, values);
            if (!result.Success) throw new IOException(result.ErrorMessage);
        }

        // ===================================================
        // CORE TRANSACTION ENGINE — with retry + event-driven RX
        // ===================================================
        private ModbusResult<ushort[]> PerformReadTransaction(byte slave, byte fc, ushort addr, ushort count, CancellationToken ct)
        {
            string lastError = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Backoff delay before retry (not on first attempt)
                if (attempt > 0)
                {
                    int backoff = (attempt - 1 < BackoffMs.Length) ? BackoffMs[attempt - 1] : BackoffMs[BackoffMs.Length - 1];
                    Thread.Sleep(backoff);
                }

                lock (_lock)
                {
                    if (!IsConnected)
                    {
                        lastError = "Not connected to Modbus";
                        continue;
                    }

                    try
                    {
                        // 1. Enforce RTU inter-frame silence
                        EnforceInterFrameDelay();

                        // 2. Build request
                        byte[] request = BuildReadRequest(slave, fc, addr, count);

                        // 3. Flush RX buffer and send
                        FlushRxBuffer();
                        SendRequest(request);

                        // 4. Wait for response (event-driven, not blocking)
                        int timeoutMs = (int)(Timeout * 1000);
                        ushort[] registers = ReceiveReadResponse(slave, fc, count, timeoutMs, ct);

                        // 5. Success — update health and return
                        _lastTransactionEnd = DateTime.Now;
                        RecordSuccess();
                        return ModbusResult<ushort[]>.Ok(registers, attempt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        _lastTransactionEnd = DateTime.Now;
                    }
                }
            }

            // All retries exhausted
            RecordError(lastError);
            return ModbusResult<ushort[]>.Fail($"Read failed after {MaxRetries + 1} attempts: {lastError}", MaxRetries);
        }

        private ModbusResult<bool> PerformWriteTransaction(byte slave, byte fc, ushort addrOrQty, ushort countOrValue, ushort[] writeValues, CancellationToken ct)
        {
            string lastError = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    int backoff = (attempt - 1 < BackoffMs.Length) ? BackoffMs[attempt - 1] : BackoffMs[BackoffMs.Length - 1];
                    Thread.Sleep(backoff);
                }

                lock (_lock)
                {
                    if (!IsConnected)
                    {
                        lastError = "Not connected to Modbus";
                        continue;
                    }

                    try
                    {
                        EnforceInterFrameDelay();

                        byte[] request = BuildWriteRequest(slave, fc, addrOrQty, countOrValue, writeValues);

                        FlushRxBuffer();
                        SendRequest(request);

                        int timeoutMs = (int)(Timeout * 1000);
                        ReceiveWriteResponse(slave, fc, timeoutMs, ct);

                        _lastTransactionEnd = DateTime.Now;
                        RecordSuccess();
                        return ModbusResult<bool>.Ok(true, attempt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        _lastTransactionEnd = DateTime.Now;
                    }
                }
            }

            RecordError(lastError);
            return ModbusResult<bool>.Fail($"Write failed after {MaxRetries + 1} attempts: {lastError}", MaxRetries);
        }

        // ===================================================
        // REQUEST BUILDERS
        // ===================================================
        private byte[] BuildReadRequest(byte slave, byte fc, ushort addr, ushort count)
        {
            if (IsTcp)
            {
                _transactionId++;
                byte[] request = new byte[12];
                request[0] = (byte)(_transactionId >> 8);
                request[1] = (byte)(_transactionId & 0xFF);
                request[2] = 0; request[3] = 0; // Protocol ID
                request[4] = 0; request[5] = 6; // Length
                request[6] = slave;
                request[7] = fc;
                request[8] = (byte)(addr >> 8);
                request[9] = (byte)(addr & 0xFF);
                request[10] = (byte)(count >> 8);
                request[11] = (byte)(count & 0xFF);
                return request;
            }
            else
            {
                byte[] request = new byte[8];
                request[0] = slave;
                request[1] = fc;
                request[2] = (byte)(addr >> 8);
                request[3] = (byte)(addr & 0xFF);
                request[4] = (byte)(count >> 8);
                request[5] = (byte)(count & 0xFF);
                ushort crc = CalculateCRC(request, 6);
                request[6] = (byte)(crc & 0xFF);
                request[7] = (byte)(crc >> 8);
                return request;
            }
        }

        private byte[] BuildWriteRequest(byte slave, byte fc, ushort addr, ushort countOrValue, ushort[] writeValues)
        {
            if (IsTcp)
            {
                _transactionId++;
                int pduLen = (fc == 0x10) ? (7 + writeValues.Length * 2) : 6;
                byte[] request = new byte[6 + pduLen];

                // MBAP Header
                request[0] = (byte)(_transactionId >> 8);
                request[1] = (byte)(_transactionId & 0xFF);
                request[2] = 0; request[3] = 0;
                request[4] = (byte)(pduLen >> 8);
                request[5] = (byte)(pduLen & 0xFF);
                request[6] = slave;
                request[7] = fc;
                request[8] = (byte)(addr >> 8);
                request[9] = (byte)(addr & 0xFF);
                request[10] = (byte)(countOrValue >> 8);
                request[11] = (byte)(countOrValue & 0xFF);

                if (fc == 0x10)
                {
                    request[12] = (byte)(writeValues.Length * 2);
                    for (int i = 0; i < writeValues.Length; i++)
                    {
                        request[13 + i * 2] = (byte)(writeValues[i] >> 8);
                        request[14 + i * 2] = (byte)(writeValues[i] & 0xFF);
                    }
                }
                return request;
            }
            else
            {
                int reqLen = (fc == 0x10) ? (9 + writeValues.Length * 2) : 8;
                byte[] request = new byte[reqLen];
                request[0] = slave;
                request[1] = fc;
                request[2] = (byte)(addr >> 8);
                request[3] = (byte)(addr & 0xFF);
                request[4] = (byte)(countOrValue >> 8);
                request[5] = (byte)(countOrValue & 0xFF);

                if (fc == 0x10)
                {
                    request[6] = (byte)(writeValues.Length * 2);
                    for (int i = 0; i < writeValues.Length; i++)
                    {
                        request[7 + i * 2] = (byte)(writeValues[i] >> 8);
                        request[8 + i * 2] = (byte)(writeValues[i] & 0xFF);
                    }
                }

                ushort crc = CalculateCRC(request, reqLen - 2);
                request[reqLen - 2] = (byte)(crc & 0xFF);
                request[reqLen - 1] = (byte)(crc >> 8);
                return request;
            }
        }

        // ===================================================
        // SEND + RECEIVE (event-driven)
        // ===================================================
        private void FlushRxBuffer()
        {
            if (IsTcp) return;
            lock (_rxLock)
            {
                _rxBuffer.Clear();
                _rxSignal.Reset();
            }
            try { _serialPort?.DiscardInBuffer(); } catch { }
        }

        private void SendRequest(byte[] request)
        {
            if (IsTcp)
            {
                _tcpStream.Write(request, 0, request.Length);
            }
            else
            {
                _serialPort.Write(request, 0, request.Length);
            }
        }

        private ushort[] ReceiveReadResponse(byte slave, byte fc, ushort expectedCount, int timeoutMs, CancellationToken ct)
        {
            if (IsTcp)
            {
                // TCP: MBAP header (7) + FC (1) + byteCount (1) = 9 bytes minimum
                byte[] header = TcpReadBytes(9, timeoutMs, ct);
                if (header == null) throw new IOException("Timeout reading TCP Modbus response header");

                byte responseFC = header[7];
                if ((responseFC & 0x80) != 0)
                    throw new Exception($"Modbus Exception: 0x{header[8]:X2}");

                byte byteCount = header[8];
                byte[] data = TcpReadBytes(byteCount, timeoutMs, ct);
                if (data == null) throw new IOException("Timeout reading TCP Modbus response data");

                ushort[] registers = new ushort[byteCount / 2];
                for (int i = 0; i < registers.Length; i++)
                    registers[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                return registers;
            }
            else
            {
                var sw = Stopwatch.StartNew();
                int skipped = 0;

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();
                    int remainMs = timeoutMs - (int)sw.ElapsedMilliseconds;

                    byte[] b = WaitForBytes(1, Math.Min(remainMs, 100), ct);
                    if (b == null) continue;

                    if (b[0] != slave)
                    {
                        skipped++;
                        continue;
                    }

                    remainMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                    byte[] hdr2 = WaitForBytes(2, Math.Min(remainMs, 100), ct);
                    if (hdr2 == null) break;

                    byte respFc = hdr2[0];
                    if (respFc != fc)
                    {
                        if ((respFc & 0x80) != 0)
                            throw new Exception($"Modbus Exception: 0x{hdr2[1]:X2}");
                        skipped += 3;
                        continue;
                    }

                    byte byteCount = hdr2[1];
                    remainMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                    byte[] dataPlusCrc = WaitForBytes(byteCount + 2, remainMs, ct);
                    if (dataPlusCrc == null) throw new IOException("Timeout waiting for RTU response data");

                    byte[] fullResponse = new byte[3 + byteCount + 2];
                    fullResponse[0] = slave; fullResponse[1] = respFc; fullResponse[2] = byteCount;
                    Buffer.BlockCopy(dataPlusCrc, 0, fullResponse, 3, byteCount + 2);

                    ushort receivedCrc = (ushort)(dataPlusCrc[byteCount] | (dataPlusCrc[byteCount + 1] << 8));
                    ushort calculatedCrc = CalculateCRC(fullResponse, fullResponse.Length - 2);
                    if (receivedCrc != calculatedCrc)
                        throw new IOException($"CRC mismatch: received 0x{receivedCrc:X4}, calculated 0x{calculatedCrc:X4}");

                    if (skipped > 0)
                        Console.WriteLine($"[INFO] Read response: skipped {skipped} E5CC bus bytes before finding slave {slave}");

                    ushort[] registers = new ushort[byteCount / 2];
                    for (int i = 0; i < registers.Length; i++)
                        registers[i] = (ushort)((dataPlusCrc[i * 2] << 8) | dataPlusCrc[i * 2 + 1]);
                    return registers;
                }

                throw new IOException($"Timeout waiting for RTU read response from slave {slave} (skipped {skipped} E5CC bus bytes)");
            }
        }

        private void ReceiveWriteResponse(byte slave, byte fc, int timeoutMs, CancellationToken ct)
        {
            if (IsTcp)
            {
                // TCP write response: MBAP(7) + FC(1) + addr(2) + qty/val(2) = 12 bytes
                byte[] resp = TcpReadBytes(12, timeoutMs, ct);
                if (resp == null) throw new IOException("Timeout reading TCP write response");

                if ((resp[7] & 0x80) != 0)
                    throw new Exception($"Modbus Exception: 0x{resp[8]:X2}");
            }
            else
            {
                var sw = Stopwatch.StartNew();
                int skipped = 0;

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();
                    int remainMs = timeoutMs - (int)sw.ElapsedMilliseconds;

                    byte[] b = WaitForBytes(1, Math.Min(remainMs, 100), ct);
                    if (b == null) continue;

                    if (b[0] != slave)
                    {
                        skipped++;
                        continue;
                    }

                    remainMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                    byte[] fcByte = WaitForBytes(1, Math.Min(remainMs, 50), ct);
                    if (fcByte == null) break;

                    byte respFc = fcByte[0];

                    if ((respFc & 0x80) != 0)
                    {
                        byte[] excRest = WaitForBytes(3, Math.Min(remainMs, 50), ct);
                        byte excCode = (excRest != null) ? excRest[0] : (byte)0xFF;
                        throw new Exception($"Modbus Exception: 0x{excCode:X2}");
                    }

                    if (respFc != fc)
                    {
                        skipped += 2;
                        continue;
                    }

                    remainMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                    byte[] rest = WaitForBytes(6, remainMs, ct);
                    if (rest == null) break;

                    byte[] resp = new byte[8];
                    resp[0] = slave;
                    resp[1] = respFc;
                    Buffer.BlockCopy(rest, 0, resp, 2, 6);

                    if (skipped > 0)
                        Console.WriteLine($"[INFO] Write response: skipped {skipped} E5CC bus bytes before finding slave {slave}");

                    ushort receivedCrcLE = (ushort)(resp[6] | (resp[7] << 8));
                    ushort calculatedCrc = CalculateCRC(resp, 6);
                    if (receivedCrcLE != calculatedCrc)
                        Console.WriteLine($"[WARN] Write CRC mismatch (slave={slave}, fc=0x{fc:X2}): " +
                            $"recv=0x{receivedCrcLE:X4}, calc=0x{calculatedCrc:X4}. Accepting.");

                    return;
                }

                throw new IOException(
                    $"Timeout waiting for write response from slave {slave} (skipped {skipped} E5CC bus bytes)");
            }
        }

        // ===================================================
        // HEALTH MONITORING
        // ===================================================
        private void RecordSuccess()
        {
            if (_consecutiveErrors > 0)
            {
                _consecutiveErrors = 0;
                ConnectionHealthChanged?.Invoke(this, new ConnectionHealthEventArgs(0, true, null));
            }
        }

        private void RecordError(string error)
        {
            _consecutiveErrors++;
            bool wasHealthy = _consecutiveErrors <= HealthThreshold;
            ConnectionHealthChanged?.Invoke(this, new ConnectionHealthEventArgs(_consecutiveErrors, _consecutiveErrors < HealthThreshold, error));
        }

        // ===================================================
        // UTILITIES
        // ===================================================
        public static ushort CalculateCRC(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        // ===================================================
        // FLOAT <-> REGISTER CONVERSION
        // Firmware (ARM Cortex-M, little-endian) stores float in inputReg[] as:
        //   reg[n]   = lo_word = (byte1<<8)|byte0  (lower address)
        //   reg[n+1] = hi_word = (byte3<<8)|byte2  (higher address)
        // So Modbus sends lo_word first, hi_word second.
        // => RegsToFloat(lo, hi) and FloatToRegs returns [lo, hi]
        // ===================================================
        public static float RegsToFloat(ushort lo, ushort hi)
        {
            byte[] bytes = new byte[4];
            bytes[0] = (byte)(lo & 0xFF);
            bytes[1] = (byte)(lo >> 8);
            bytes[2] = (byte)(hi & 0xFF);
            bytes[3] = (byte)(hi >> 8);
            return BitConverter.ToSingle(bytes, 0);
        }

        public static ushort[] FloatToRegs(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            ushort lo = (ushort)(bytes[0] | (bytes[1] << 8));
            ushort hi = (ushort)(bytes[2] | (bytes[3] << 8));
            return new ushort[] { lo, hi };  // lo at [0] (lower address), hi at [1]
        }

        // ===================================================
        // DEVICE SIMULATION INTERNAL LOGIC
        // ===================================================
        private void UpdateSimulation()
        {
            // Simulate actual MFC flows heading towards setpoints if DAC is enabled
            for (int i = 0; i < 6; i++)
            {
                if (_simDacEn[i] == 1)
                {
                    float target = _simSccmSP[i];
                    _simSccmPV[i] += (target - _simSccmPV[i]) * 0.15f; // lag filter
                }
                else
                {
                    _simSccmPV[i] += (0.0f - _simSccmPV[i]) * 0.25f; // goes to 0
                }
                // Add minor noise
                if (_simSccmPV[i] > 0.5f)
                {
                    _simSccmPV[i] += (float)(_rand.NextDouble() - 0.5) * 0.1f;
                }
            }

            // Simulate E5CC temperature controller rising/falling to setpoint when RUNning
            if (_simE5ccRunStop == 0) // RUN
            {
                float target = _simE5ccSP;
                _simE5ccPV += (target - _simE5ccPV) * 0.05f; // heating/cooling lag
                
                // If Auto-Tune is running, simulate it oscillating slightly and then stopping
                if (_simE5ccAT == 1)
                {
                    _simE5ccPV += (float)(Math.Sin(DateTime.Now.Ticks / 10000000.0) * 0.5);
                    _simE5ccStatus = (ushort)((_simE5ccStatus & ~(1 << 0)) | (1 << 2)); // Stop = 0, AT = 1
                }
                else
                {
                    _simE5ccStatus = (ushort)(_simE5ccStatus & ~((1 << 0) | (1 << 2))); // Stop = 0, AT = 0
                }
            }
            else
            {
                _simE5ccPV += (25.0f - _simE5ccPV) * 0.01f; // cools down to room temp
                _simE5ccStatus = (ushort)((_simE5ccStatus | (1 << 0)) & ~(1 << 2)); // Stop = 1, AT = 0
            }

            // Check temp alarms
            if (_simE5ccPV >= _simAlm1 / 10.0f)
            {
                _simE5ccStatus |= (1 << 3); // trigger alarm 1
            }
            else
            {
                _simE5ccStatus &= unchecked((ushort)~(1 << 3));
            }
        }

        private ushort[] SimReadHolding(byte slave, ushort addr, ushort count)
        {
            ushort[] regs = new ushort[count];
            if (slave == 2) // Mixing Board
            {
                for (int i = 0; i < count; i++)
                {
                    int curr = addr + i;
                    if (curr == 20) regs[i] = _simRelay1;
                    else if (curr == 21) regs[i] = _simRelay2;
                    else if (curr >= 0 && curr < 48) // Config registers
                    {
                        int ch = curr / 8;
                        int param = curr % 8;
                        float val = 0;
                        if (param < 2) val = _simMinSccm[ch];
                        else if (param < 4) val = _simMaxSccm[ch];
                        else if (param < 6) val = _simMinV[ch];
                        else val = _simMaxV[ch];

                        ushort[] split = FloatToRegs(val);
                        regs[i] = (param % 2 == 0) ? split[0] : split[1];
                    }
                    else if (curr >= 60 && curr < 78) // Setpoint + DAC en
                    {
                        int ch = (curr - 60) / 3;
                        int param = (curr - 60) % 3;
                        if (param < 2)
                        {
                            ushort[] split = FloatToRegs(_simSccmSP[ch]);
                            regs[i] = (param == 0) ? split[0] : split[1];
                        }
                        else
                        {
                            regs[i] = _simDacEn[ch];
                        }
                    }
                }
            }
            else if (slave == 1) // E5CC
            {
                for (int i = 0; i < count; i++)
                {
                    int curr = addr + i;
                    if (curr == 0x0000) regs[i] = _simE5ccRunStop;
                    else if (curr == 0x0002) regs[i] = _simE5ccAT;
                    else if (curr == 0x0100) regs[i] = _simE5ccStatus;
                    else if (curr == 0x2000) regs[i] = (ushort)(_simE5ccPV * 10);
                    else if (curr == 0x2001) regs[i] = (ushort)(_simE5ccSP * 10);
                    else if (curr == 0x2002) regs[i] = (ushort)((_simE5ccRunStop == 0) ? 600 : 0); // simulated MV %
                    else if (curr == 0x2100) regs[i] = (ushort)(_simE5ccSP * 10);
                    else if (curr == 0x2200) regs[i] = _simAlm1;
                    else if (curr == 0x2201) regs[i] = _simAlm2;
                    else if (curr == 0x2300) regs[i] = _simP;
                    else if (curr == 0x2301) regs[i] = _simI;
                    else if (curr == 0x2302) regs[i] = _simD;
                    else if (curr == 0x2303) regs[i] = _simCtrlPeriod;
                    else if (curr == 0x2304) regs[i] = _simMvHi;
                    else if (curr == 0x2305) regs[i] = _simMvLo;
                    else if (curr == 0x2400) regs[i] = _simInputShift;
                    else if (curr == 0x2401) regs[i] = _simSpHi;
                    else if (curr == 0x2402) regs[i] = _simSpLo;
                }
            }
            return regs;
        }

        private ushort[] SimReadInput(byte slave, ushort addr, ushort count)
        {
            ushort[] regs = new ushort[count];
            if (slave == 2) // Mixing Board
            {
                // Compute simulated flows for display
                float simFlow1=0, simFlow2=0, simFlow3=0, simFlow4=0, simFlow5=0, simFlow6=0;
                if (_simIsRun == 1)
                {
                    if (_simMode == 1) // Concentration mode: firmware calculates flows
                    {
                        float tot = _simTotalFlow;
                        float q3 = (_simCo1 > 0) ? (_simGas1Ppm / _simCo1) * tot : 0;
                        float q5 = (_simCo2 > 0) ? (_simGas2Ppm / _simCo2) * tot : 0;
                        float q6 = (_simCo3 > 0) ? (_simGas3Ppm / _simCo3) * tot : 0;
                        q3 = Math.Min(q3, _simMaxSccm[2]);
                        q5 = Math.Min(q5, _simMaxSccm[4]);
                        q6 = Math.Min(q6, _simMaxSccm[5]);
                        simFlow2 = Math.Max(0, tot - q3 - q5 - q6);
                        simFlow3 = q3; simFlow5 = q5; simFlow6 = q6;
                    }
                    else // Direct Sccm mode
                    {
                        simFlow3 = _simGas1Ppm;
                        simFlow5 = _simGas2Ppm;
                        simFlow6 = _simGas3Ppm;
                        simFlow2 = Math.Max(0, _simTotalFlow - simFlow3 - simFlow5 - simFlow6);
                    }
                }
                // Simulate approach to setpoint (lag filter)
                _simSccmSP[0] = simFlow1; _simSccmSP[1] = simFlow2;
                _simSccmSP[2] = simFlow3; _simSccmSP[3] = simFlow4;
                _simSccmSP[4] = simFlow5; _simSccmSP[5] = simFlow6;

                for (int i = 0; i < count; i++)
                {
                    int curr = addr + i;

                    // Legacy IR 0-11 (old firmware): flow PV 6 channels
                    if (curr >= 0 && curr < 12)
                    {
                        int ch = curr / 2;
                        ushort[] split = FloatToRegs(_simSccmPV[ch]);
                        regs[i] = (curr % 2 == 0) ? split[0] : split[1];
                    }
                    // HMI Block IR 200+ (V5 firmware)
                    else if (curr >= 200)
                    {
                        int off = curr - 200;
                        switch (off)
                        {
                            case 0: regs[i] = (ushort)(_simTempSet * 10); break;  // tempSet
                            case 1: regs[i] = (ushort)(_simE5ccPV * 10); break;   // tempReal
                            // gas1 SP (float, 2 regs)
                            case 2: regs[i] = FloatToRegs(_simGas1Ppm)[0]; break;
                            case 3: regs[i] = FloatToRegs(_simGas1Ppm)[1]; break;
                            // gas1 PV actual (simulated as concGas1 = gas1Ppm when running)
                            case 4: regs[i] = FloatToRegs(_simIsRun==1 ? _simGas1Ppm : 0)[0]; break;
                            case 5: regs[i] = FloatToRegs(_simIsRun==1 ? _simGas1Ppm : 0)[1]; break;
                            // gas2 SP
                            case 6: regs[i] = FloatToRegs(_simGas2Ppm)[0]; break;
                            case 7: regs[i] = FloatToRegs(_simGas2Ppm)[1]; break;
                            // gas2 PV
                            case 8: regs[i] = FloatToRegs(_simIsRun==1 ? _simGas2Ppm : 0)[0]; break;
                            case 9: regs[i] = FloatToRegs(_simIsRun==1 ? _simGas2Ppm : 0)[1]; break;
                            // gas3 SP
                            case 10: regs[i] = FloatToRegs(_simGas3Ppm)[0]; break;
                            case 11: regs[i] = FloatToRegs(_simGas3Ppm)[1]; break;
                            // gas3 PV
                            case 12: regs[i] = FloatToRegs(_simIsRun==1 ? _simGas3Ppm : 0)[0]; break;
                            case 13: regs[i] = FloatToRegs(_simIsRun==1 ? _simGas3Ppm : 0)[1]; break;
                            // time uint32 (off 14-15) - skip
                            case 16: regs[i] = _simMode; break;      // mode
                            case 17: regs[i] = _simIsRun; break;     // isRun
                            case 18: regs[i] = _simRelay1; break;    // RelayVan1
                            default:
                                // flowReal[6] at offsets 19-30
                                if (off >= 19 && off <= 30)
                                {
                                    int fIdx = (off - 19) / 2;
                                    int fPart = (off - 19) % 2;
                                    float[] flows = { simFlow1, simFlow2, simFlow3, simFlow4, simFlow5, simFlow6 };
                                    regs[i] = FloatToRegs(_simSccmPV[fIdx])[fPart];
                                }
                                // flowSet[6] at offsets 32-43
                                else if (off >= 32 && off <= 43)
                                {
                                    int fIdx = (off - 32) / 2;
                                    int fPart = (off - 32) % 2;
                                    float[] spArr = { simFlow1, simFlow2, simFlow3, simFlow4, simFlow5, simFlow6 };
                                    regs[i] = FloatToRegs(fIdx < 6 ? spArr[fIdx] : 0)[fPart];
                                }
                                break;
                        }
                    }
                }
            }
            return regs;
        }

        private void SimWriteSingle(byte slave, ushort addr, ushort val)
        {
            if (slave == 2)
            {
                if (addr == 20) _simRelay1 = val;
                else if (addr == 21) _simRelay2 = val;
                else if (addr >= 60 && addr < 78)
                {
                    int ch = (addr - 60) / 3;
                    int param = (addr - 60) % 3;
                    if (param == 2) _simDacEn[ch] = val;
                }
            }
            else if (slave == 1)
            {
                if (addr == 0x0000)
                {
                    // E5CC Operation Command: high byte = command code, low byte = info
                    ushort cmdCode = (ushort)(val >> 8);
                    ushort cmdInfo = (ushort)(val & 0xFF);
                    if (cmdCode == 0x01) // Run/Stop command
                    {
                        _simE5ccRunStop = cmdInfo; // 0 = Run, 1 = Stop
                    }
                    else if (cmdCode == 0x02) // Auto-Tune command
                    {
                        _simE5ccAT = (ushort)(cmdInfo == 0 ? 1 : 0); // Info 00 = execute, 01 = cancel
                        if (cmdInfo == 0) // AT Execute
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                Thread.Sleep(10000);
                                _simE5ccAT = 0;
                                _simP = (ushort)(_rand.Next(80, 150));
                                _simI = (ushort)(_rand.Next(150, 300));
                                _simD = (ushort)(_rand.Next(30, 80));
                            });
                        }
                    }
                }
                else if (addr == 0x2103) _simE5ccSP = val / 10.0f;
                else if (addr == 0x2200) _simAlm1 = val;
                else if (addr == 0x2201) _simAlm2 = val;
                else if (addr == 0x2300) _simP = val;
                else if (addr == 0x2301) _simI = val;
                else if (addr == 0x2302) _simD = val;
                else if (addr == 0x2303) _simCtrlPeriod = val;
                else if (addr == 0x2304) _simMvHi = val;
                else if (addr == 0x2305) _simMvLo = val;
                else if (addr == 0x2400) _simInputShift = val;
                else if (addr == 0x2401) _simSpHi = val;
                else if (addr == 0x2402) _simSpLo = val;
            }
        }

        private void SimWriteMultiple(byte slave, ushort addr, ushort[] vals)
        {
            if (slave == 2)
            {
                for (int i = 0; i < vals.Length; i++)
                {
                    int curr = addr + i;

                    // HR 20-21: Relay control (Van + Pump)
                    if (curr == 20) { _simRelay1 = vals[i]; continue; }
                    if (curr == 21) { _simRelay2 = vals[i]; continue; }

                    // HR 30+: concMfcSetValue_t — concentration/flow control
                    if (curr >= 30 && curr <= 49)
                    {
                        int off = curr - 30;
                        switch (off)
                        {
                            case 0: _simMode = vals[i]; break;
                            case 1: _simIsRun = vals[i]; break;
                            // gas1 float (regs 2-3)
                            case 2 when vals.Length > i + 1:
                                _simGas1Ppm = RegsToFloat(vals[i], vals[i + 1]); break;
                            // gas2 float (regs 4-5)
                            case 4 when vals.Length > i + 1:
                                _simGas2Ppm = RegsToFloat(vals[i], vals[i + 1]); break;
                            // gas3 float (regs 6-7)
                            case 6 when vals.Length > i + 1:
                                _simGas3Ppm = RegsToFloat(vals[i], vals[i + 1]); break;
                        }
                        continue;
                    }

                    // HR 270: STOP_ALL
                    if (curr == 270) { _simIsRun = 0; _simRelay1 = 0; _simRelay2 = 0; continue; }

                    // HR 0-47: config registers
                    if (curr >= 0 && curr < 48)
                    {
                        int ch = curr / 8;
                        int param = curr % 8;
                        if (param == 0 && vals.Length >= i + 2)
                            _simMinSccm[ch] = RegsToFloat(vals[i], vals[i + 1]);
                        else if (param == 2 && vals.Length >= i + 2)
                            _simMaxSccm[ch] = RegsToFloat(vals[i], vals[i + 1]);
                        else if (param == 4 && vals.Length >= i + 2)
                            _simMinV[ch] = RegsToFloat(vals[i], vals[i + 1]);
                        else if (param == 6 && vals.Length >= i + 2)
                            _simMaxV[ch] = RegsToFloat(vals[i], vals[i + 1]);
                    }
                    // HR 60-77: old setpoint registers (kept for compatibility)
                    else if (curr >= 60 && curr < 78)
                    {
                        int ch = (curr - 60) / 3;
                        int param = (curr - 60) % 3;
                        if (param == 0 && vals.Length >= i + 2)
                            _simSccmSP[ch] = RegsToFloat(vals[i], vals[i + 1]);
                        else if (param == 2)
                            _simDacEn[ch] = vals[i];
                    }
                }
            }
            else if (slave == 1)
            {
                // E5CC simulator: handle batch writes for PID, SP limits, alarms, and SP
                for (int i = 0; i < vals.Length; i++)
                {
                    int curr = addr + i;
                    if (curr == 0x2103) _simE5ccSP = vals[i] / 10.0f;
                    else if (curr == 0x2200) _simAlm1 = vals[i];
                    else if (curr == 0x2201) _simAlm2 = vals[i];
                    else if (curr == 0x2300) _simP = vals[i];
                    else if (curr == 0x2301) _simI = vals[i];
                    else if (curr == 0x2302) _simD = vals[i];
                    else if (curr == 0x2303) _simCtrlPeriod = vals[i];
                    else if (curr == 0x2304) _simMvHi = vals[i];
                    else if (curr == 0x2305) _simMvLo = vals[i];
                    else if (curr == 0x2400) _simInputShift = vals[i];
                    else if (curr == 0x2401) _simSpHi = vals[i];
                    else if (curr == 0x2402) _simSpLo = vals[i];
                }
            }
        }
    }
}
