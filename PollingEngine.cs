using System;
using System.Diagnostics;
using System.Threading;

namespace Bo_Tron_Khi_CS
{
    public class PolledDataEventArgs : EventArgs
    {
        public PolledData Data { get; }
        public PolledDataEventArgs(PolledData data) => Data = data;
    }

    public class PolledData
    {
        // Mixing Board (Slave 3) — from HMI Input Register block (IR 200+)
        public float[] SccmPV { get; set; } = new float[6];   // actual flows IR 219-230 (flowReal)
        public float[] SccmSP { get; set; } = new float[6];   // setpoint flows IR 232-243 (flowSet)
        public ushort[] DacEnable { get; set; } = new ushort[6]; // not directly from IR, unused for now
        public ushort Relay1 { get; set; }   // IR 218 = RelayVan1_Read()
        public ushort Relay2 { get; set; }   // not in HMI block directly, default 0
        public ushort BoardStatus { get; set; } // IR 216 = mode, IR 217 = isRun

        // Concentration values (from HMI block)
        public float ConcGas1SP { get; set; }  // IR 202-203 = concMfcSetValue.gas1
        public float ConcGas1PV { get; set; }  // IR 204-205 = massflowValue.concGas1
        public float ConcGas2SP { get; set; }  // IR 206-207
        public float ConcGas2PV { get; set; }  // IR 208-209
        public float ConcGas3SP { get; set; }  // IR 210-211
        public float ConcGas3PV { get; set; }  // IR 212-213
        public ushort TempSet { get; set; }    // IR 200 = pcStatus.tempSet
        public ushort TempReal { get; set; }   // IR 201 = pcStatus.tempReal (from board E5CC master)
        public ushort IsRunning { get; set; }  // IR 217 = concMfcSetValue.isRun
        public ushort Mode { get; set; }       // IR 216 = concMfcSetValue.mode

        // E5CC (Slave 1, PC Mode)
        public float E5ccPV { get; set; }
        public float E5ccSP { get; set; }
        public float E5ccMV { get; set; }
        public ushort E5ccStatus { get; set; }
        
        // Error tracking
        public string ErrorMessage { get; set; }
        public bool IsError => !string.IsNullOrEmpty(ErrorMessage);

        // Communication quality indicators
        public int TotalRetries { get; set; }
        public int FailedTransactions { get; set; }
    }

    public class PollingEngine
    {
        private readonly ModbusHandler _handler;
        private readonly SystemConfig _config;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private int _targetIntervalMs = 500;

        // Adaptive polling state
        private int _currentIntervalMs = 500;
        private int _consecutiveCycleErrors = 0;
        private const int ErrorSlowdownThreshold = 3;  // after 3 bad cycles, slow down
        private const int SlowPollIntervalMs = 2000;    // degraded polling rate
        private const int MinPollIntervalMs = 100;

        public event EventHandler<PolledDataEventArgs> DataPolled;
        public PolledData LastData { get; private set; }
        


        /// <summary>
        /// True if polling has degraded due to repeated errors.
        /// </summary>
        public bool IsDegraded => _consecutiveCycleErrors >= ErrorSlowdownThreshold;

        public PollingEngine(ModbusHandler handler, SystemConfig config)
        {
            _handler = handler;
            _config = config;
            _targetIntervalMs = (int)(config.poll_interval * 1000);
            if (_targetIntervalMs < MinPollIntervalMs) _targetIntervalMs = MinPollIntervalMs;
            _currentIntervalMs = _targetIntervalMs;
        }

        public void Start()
        {
            if (_cts != null) return; // already running

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => PollLoop(_cts.Token)) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            var cts = _cts;
            if (cts == null) return;

            cts.Cancel(); // immediately signals the CancellationToken
            _thread?.Join(2000); // wait up to 2s for clean exit
            _cts = null;
            _thread = null;
        }

        private void PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var cycleTimer = Stopwatch.StartNew();
                var data = new PolledData();
                int totalRetries = 0;
                int failedTx = 0;

                try
                {
                    if (_handler.IsConnected)
                    {
                        // === Poll HMI Status Block: IR 200 to IR 243 (44 registers) ===
                        // Firmware fills this block on every ReadInputRegisters(addr >= 200)
                        // Layout (relative to IR 200 base):
                        //   +0  = tempSet (uint16)
                        //   +1  = tempReal (uint16)
                        //   +2,+3  = gas1 SP (float)
                        //   +4,+5  = gas1 PV (float)
                        //   +6,+7  = gas2 SP (float)
                        //   +8,+9  = gas2 PV (float)
                        //   +10,+11= gas3 SP (float)
                        //   +12,+13= gas3 PV (float)
                        //   +14,+15= time (uint32)
                        //   +16   = mode
                        //   +17   = isRun
                        //   +18   = RelayVan1 state
                        //   +19..+30 = flowReal[6] (6 floats = 12 words)
                        //   +31   = pipe value
                        //   +32..+43 = flowSet[6] (6 floats = 12 words)
                        var hmiResult = _handler.TryReadInputRegisters((byte)_config.mixing_slave, 200, 44, ct);
                        if (hmiResult.Success && hmiResult.Data != null && hmiResult.Data.Length >= 44)
                        {
                            var d = hmiResult.Data;
                            data.TempSet  = d[0];
                            data.TempReal = d[1];
                            data.ConcGas1SP = ModbusHandler.RegsToFloat(d[2], d[3]);
                            data.ConcGas1PV = ModbusHandler.RegsToFloat(d[4], d[5]);
                            data.ConcGas2SP = ModbusHandler.RegsToFloat(d[6], d[7]);
                            data.ConcGas2PV = ModbusHandler.RegsToFloat(d[8], d[9]);
                            data.ConcGas3SP = ModbusHandler.RegsToFloat(d[10], d[11]);
                            data.ConcGas3PV = ModbusHandler.RegsToFloat(d[12], d[13]);
                            // d[14], d[15] = time uint32 (skip for now)
                            data.Mode      = d[16];
                            data.IsRunning = d[17];
                            data.Relay1    = d[18];  // RelayVan1 current state (IR 218)
                            // flowReal[6] at offsets 19..30
                            for (int i = 0; i < 6; i++)
                                data.SccmPV[i] = ModbusHandler.RegsToFloat(d[19 + i * 2], d[20 + i * 2]);
                            // d[31] = pipe value (skip)
                            // flowSet[6] at offsets 32..43
                            for (int i = 0; i < 6; i++)
                                data.SccmSP[i] = ModbusHandler.RegsToFloat(d[32 + i * 2], d[33 + i * 2]);
                            data.BoardStatus = data.IsRunning;
                            totalRetries += hmiResult.RetryCount;
                        }
                        else
                        {
                            failedTx++;
                            totalRetries += hmiResult.RetryCount;
                        }

                        // Read HR 20-21 (Relay Van1 + Van2/Pump state) from mixing board
                        // ADR_HOLDING_RELAY_VAN_CONTROL = 20 → HR20 = RelayVan1, HR21 = RelayVan2 (Pump)
                        var relayResult = _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 20, 2, ct);
                        if (relayResult.Success && relayResult.Data != null && relayResult.Data.Length >= 2)
                        {
                            // Do NOT overwrite data.Relay1 (Valve) with HR 20 because the firmware
                            // does not update HR 20 when the valve is toggled via register 201.
                            // data.Relay1 remains populated from the true hardware state at IR 218.
                            data.Relay2 = relayResult.Data[1]; // HR 21 = Pump ON/OFF
                            totalRetries += relayResult.RetryCount;
                        }
                        else
                        {
                            // fallback: keep Relay1 from IR 218, Relay2 stays 0
                            totalRetries += relayResult.RetryCount;
                        }

                        // 4. Poll E5CC Temp PV, SP, MV and Status (PC Mode: direct communication)
                        {
                            // PC communicates with E5CC directly (Slave 1)
                            // E5CC returns raw ×10 values: 1320 = 132.0°C
                            var e5Result = _handler.TryReadHoldingRegisters((byte)_config.e5cc_slave, 0x2000, 3, ct);
                            if (e5Result.Success && e5Result.Data != null && e5Result.Data.Length >= 3)
                            {
                                // Store raw values (no /10) — 1320 means 132.0°C on E5CC display
                                data.E5ccPV = (short)e5Result.Data[0]; // raw: 1320 for 132.0°C
                                data.E5ccSP = (short)e5Result.Data[1]; // raw SP
                                data.E5ccMV = (short)e5Result.Data[2]; // raw MV
                                totalRetries += e5Result.RetryCount;
                            }
                            else
                            {
                                failedTx++;
                                totalRetries += e5Result.RetryCount;
                            }

                            // E5CC Status register
                            var e5StatusResult = _handler.TryReadHoldingRegisters((byte)_config.e5cc_slave, 0x0100, 1, ct);
                            if (e5StatusResult.Success && e5StatusResult.Data != null && e5StatusResult.Data.Length >= 1)
                            {
                                data.E5ccStatus = e5StatusResult.Data[0];
                                totalRetries += e5StatusResult.RetryCount;
                            }
                            else
                            {
                                failedTx++;
                                totalRetries += e5StatusResult.RetryCount;
                            }
                        }

                        // Keepalive heartbeat: read HR 2 from mixing board
                        // Firmware checks msg->address==2 to maintain isModbusConnected flag
                        _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 2, 1, ct);

                        // Record quality metrics
                        data.TotalRetries = totalRetries;
                        data.FailedTransactions = failedTx;

                        // Partial success is still valid data
                        if (failedTx > 0 && failedTx < 5)
                        {
                            // Some transactions failed but we got partial data — still usable
                            data.ErrorMessage = null; // not a full error
                        }
                        else if (failedTx >= 5)
                        {
                            data.ErrorMessage = "All Modbus transactions failed";
                        }
                    }
                    else
                    {
                        data.ErrorMessage = "Modbus connection closed.";
                        failedTx = 5;
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // clean exit on cancellation
                }
                catch (Exception ex)
                {
                    data.ErrorMessage = ex.Message;
                    failedTx = 5;
                }

                // Adaptive interval adjustment
                if (failedTx >= 3)
                {
                    _consecutiveCycleErrors++;
                    if (_consecutiveCycleErrors >= ErrorSlowdownThreshold)
                    {
                        _currentIntervalMs = SlowPollIntervalMs; // back-pressure: slow down
                    }
                }
                else
                {
                    if (_consecutiveCycleErrors > 0)
                    {
                        _consecutiveCycleErrors = 0;
                        _currentIntervalMs = _targetIntervalMs; // restore normal speed
                    }
                }

                // Raise event
                LastData = data;
                DataPolled?.Invoke(this, new PolledDataEventArgs(data));

                // Adaptive sleep: target interval minus actual transaction time
                cycleTimer.Stop();
                int elapsedMs = (int)cycleTimer.ElapsedMilliseconds;
                int sleepMs = Math.Max(10, _currentIntervalMs - elapsedMs);

                // Use WaitHandle.WaitOne for cancellable sleep (instead of Thread.Sleep)
                try
                {
                    ct.WaitHandle.WaitOne(sleepMs);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
    }
}
