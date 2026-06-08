using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bo_Tron_Khi_CS
{
    public enum RecipeState
    {
        Idle,
        PreStabilization,
        Stabilization,
        Exposure,
        Recovery
    }

    public class RecipeProgressEventArgs : EventArgs
    {
        public int ActiveStepIndex { get; }
        public RecipeState State { get; }
        public int RemainingSeconds { get; }
        public string Message { get; }
        public RecipeProgressEventArgs(int stepIdx, RecipeState state, int remSec, string msg)
        {
            ActiveStepIndex = stepIdx;
            State = state;
            RemainingSeconds = remSec;
            Message = msg;
        }
    }

    public class RecipeEngine
    {
        private readonly ModbusHandler _handler;
        private readonly SystemConfig _config;
        private CancellationTokenSource _cts;
        private Task _runTask;

        public bool IsRunning { get; private set; } = false;
        public RecipeState CurrentState { get; private set; } = RecipeState.Idle;
        public int ActiveStepIndex { get; private set; } = -1;

        public event EventHandler<RecipeProgressEventArgs> ProgressUpdated;
        public event EventHandler RecipeCompleted;

        public RecipeEngine(ModbusHandler handler, SystemConfig config)
        {
            _handler = handler;
            _config = config;
        }

        public void Start(List<RecipeStep> steps)
        {
            if (IsRunning) return;
            if (steps == null || steps.Count == 0) return;

            IsRunning = true;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunSequence(steps, _cts.Token), _cts.Token);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try
            {
                _runTask?.Wait(2000);
            }
            catch { }
            IsRunning = false;
            CurrentState = RecipeState.Idle;
            ActiveStepIndex = -1;

            // Safe shutdown of flows
            SafeShutdownFlows();
        }

        // ===================================================
        // SET RELAYS — write HR 20 + HR 21 together (FC 0x10)
        // This is the ONLY way to trigger the relay handler:
        //   ADR_HOLDING_RELAY_VAN_CONTROL (20) handler reads holdingReg[20] and holdingReg[21]
        //   relay1 = 1: Van ON, 0: Van OFF
        //   relay2 = 1: Pump ON, 0: Pump OFF
        // ===================================================
        private void SetRelays(byte ms, CancellationToken ct, ushort relay1, ushort relay2)
        {
            var result = _handler.TryWriteMultipleRegisters(ms, 20, new ushort[] { relay1, relay2 }, ct);
            if (!result.Success)
                Console.WriteLine($"SetRelays({relay1},{relay2}) failed: {result.ErrorMessage}");
        }

        // ===================================================
        // WRITE PURGE — uses conc mode with gas=0 ppm
        // Firmware computes: gas flows=0, carrier=totalFlow
        // ===================================================
        private void WritePurgeMode(byte ms, CancellationToken ct)
        {
            WriteConcMfcValue(ms, ct, 1, 1, 0, 0, 0); // mode=Conc, isRun=1, gas1/2/3=0 ppm
        }


        // ===================================================
        // RECIPE SEQUENCE — passes CancellationToken throughout
        // ===================================================
        private async Task RunSequence(List<RecipeStep> steps, CancellationToken token)
        {
            byte ms = (byte)_config.mixing_slave;
            byte es = (byte)_config.e5cc_slave;

            try
            {
                double lastTempSet = -999.0; // Track previous step's temperature to optimize heating

                for (int stepIdx = 0; stepIdx < steps.Count; stepIdx++)
                {
                    token.ThrowIfCancellationRequested();
                    ActiveStepIndex = stepIdx;
                    RecipeStep step = steps[stepIdx];

                    // ==========================================
                    // 1. STABILIZATION (HEATING) PHASE
                    // ==========================================
                    CurrentState = RecipeState.Stabilization;

                    // Write target temperature to E5CC
                    ushort tempReg = (ushort)step.Temp;
                    _handler.TryWriteMultipleRegisters(es, 0x2103, new ushort[] { tempReg }, token);
                    _handler.TryWriteSingleRegister(es, 0x0000, 0x0100, token); // E5CC RUN

                    bool tempChanged = (stepIdx == 0) || (Math.Abs(step.Temp - lastTempSet) > 0.1);

                    if (tempChanged && _config.stable_time > 0)
                    {
                        double stableTime = _config.stable_time;
                        double gasOnTime = _config.gas_on_time;
                        double preMixSecs = Math.Min(gasOnTime, stableTime);
                        double purgeSecs = stableTime - preMixSecs;

                        double elapsed = 0;
                        bool isPreMixing = false;

                        // Start with Purge: Keep Valve OFF + Pump ON, flows = Purge (Gas ppm = 0)
                        SetRelays(ms, token, 0, 1);
                        WritePurgeMode(ms, token);

                        while (elapsed < stableTime)
                        {
                            token.ThrowIfCancellationRequested();
                            double rem = stableTime - elapsed;

                            if (elapsed >= purgeSecs && preMixSecs > 0 && !isPreMixing)
                            {
                                isPreMixing = true;
                                ApplyStepFlows(step, ms, es, token);
                                SetRelays(ms, token, 0, 1);
                            }

                            string msg = isPreMixing
                                ? $"Step {stepIdx + 1}/{steps.Count} - Stabilizing (Pre-mix) - Rem: {(int)Math.Ceiling(rem)}s"
                                : $"Step {stepIdx + 1}/{steps.Count} - Stabilizing (Purge) - Rem: {(int)Math.Ceiling(rem)}s";

                            ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), msg));
                            await Task.Delay(500, token);
                            elapsed += 0.5;
                        }
                    }

                    // Save last temperature setpoint
                    lastTempSet = step.Temp;

                    // ==========================================
                    // 2. EXPOSURE PHASE
                    // ==========================================
                    CurrentState = RecipeState.Exposure;

                    // Valve ON + Pump ON, target flows remain applied
                    ApplyStepFlows(step, ms, es, token);
                    SetRelays(ms, token, 1, 1);

                    double expTime = step.ExposureTime;
                    double expElapsed = 0;

                    while (expElapsed < expTime)
                    {
                        token.ThrowIfCancellationRequested();
                        double rem = expTime - expElapsed;
                        ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), 
                            $"Step {stepIdx + 1}/{steps.Count} - Exposure - Rem: {(int)Math.Ceiling(rem)}s"));
                        await Task.Delay(500, token);
                        expElapsed += 0.5;
                    }

                    // ==========================================
                    // 3. RECOVERY PHASE
                    // ==========================================
                    CurrentState = RecipeState.Recovery;

                    double recTime = step.RecoveryTime;
                    double recElapsed = 0;

                    bool nextTempSame = (stepIdx + 1 < steps.Count) && (Math.Abs(steps[stepIdx + 1].Temp - step.Temp) <= 0.1);
                    double gasOnTimeVal = _config.gas_on_time;
                    double preMixSecsVal = nextTempSame ? Math.Min(gasOnTimeVal, recTime) : 0;
                    double purgeSecsVal = recTime - preMixSecsVal;

                    bool isPreMixingRec = false;

                    // Start with Purge: Valve OFF + Pump ON, flows = Purge (Gas ppm = 0)
                    SetRelays(ms, token, 0, 1);
                    WritePurgeMode(ms, token);

                    while (recElapsed < recTime)
                    {
                        token.ThrowIfCancellationRequested();
                        double rem = recTime - recElapsed;

                        if (recElapsed >= purgeSecsVal && preMixSecsVal > 0 && !isPreMixingRec)
                        {
                            isPreMixingRec = true;
                            ApplyStepFlows(steps[stepIdx + 1], ms, es, token);
                            SetRelays(ms, token, 0, 1);
                        }

                        string msg = isPreMixingRec
                            ? $"Step {stepIdx + 1}/{steps.Count} - Recovery (Pre-mix Next) - Rem: {(int)Math.Ceiling(rem)}s"
                            : $"Step {stepIdx + 1}/{steps.Count} - Recovery (Purge) - Rem: {(int)Math.Ceiling(rem)}s";

                        ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), msg));
                        await Task.Delay(500, token);
                        recElapsed += 0.5;
                    }
                }

                // Completed
                SafeShutdownFlows();
                IsRunning = false;
                RecipeCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                // Task was canceled
            }
            catch (Exception ex)
            {
                ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(-1, RecipeState.Idle, 0, $"Error: {ex.Message}"));
                SafeShutdownFlows();
                IsRunning = false;
            }
        }

        // ===================================================
        // APPLY STEP — V5: firmware auto-calculates flow from gas ppm
        // App only sends: temp (via HR 202), gas1/2/3 ppm, mode=Conc, isRun=1
        // ===================================================
        private void ApplyStepFlows(RecipeStep step, byte ms, byte es, CancellationToken ct)
        {
            // Set temperature via board (HR 202) — board forwards to E5CC
            ushort tempReg = (ushort)step.Temp;
            _handler.TryWriteSingleRegister(ms, 202, tempReg, ct);

            // Write conc_mfc_value_t: mode=1 (Concentration), isRun=1, gas ppm
            // Firmware auto-calculates flow from gas ppm + concCfg (totalFlow, co1, co2, co3)
            WriteConcMfcValue(ms, ct, 1, 1, step.Gas1Ppm, step.Gas2Ppm, step.Gas3Ppm);
        }

        private void WriteConcMfcValue(byte ms, CancellationToken ct, ushort mode, ushort isRun, double gas1, double gas2, double gas3)
        {
            ushort[] regs = new ushort[20];
            regs[0] = mode;
            regs[1] = isRun;
            ushort[] g1 = ModbusHandler.FloatToRegs((float)gas1);
            regs[2] = g1[0];
            regs[3] = g1[1];
            ushort[] g2 = ModbusHandler.FloatToRegs((float)gas2);
            regs[4] = g2[0];
            regs[5] = g2[1];
            ushort[] g3 = ModbusHandler.FloatToRegs((float)gas3);
            regs[6] = g3[0];
            regs[7] = g3[1];
            // registers 8 to 19 (the 6 flows) can remain 0 since they are not used when mode=1 (concentration mode)
            _handler.TryWriteMultipleRegisters(ms, 30, regs, ct);
        }

        // ===================================================
        // SAFE SHUTDOWN — V5: use STOP_ALL trigger at HR 270
        // ===================================================
        private void SafeShutdownFlows()
        {
            try
            {
                byte ms = (byte)_config.mixing_slave;
                // V5: STOP_ALL trigger at HR 270 — firmware zeros flows + closes valve
                _handler.TryWriteMultipleRegisters(ms, 270, new ushort[] { 1 });

                // Also explicitly close both relays as safety fallback
                _handler.TryWriteMultipleRegisters(ms, 20, new ushort[] { 0, 0 }); // Van OFF, Pump OFF
            }
            catch { }
        }
    }
}
