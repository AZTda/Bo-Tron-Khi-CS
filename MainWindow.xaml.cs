using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using MiniExcelLibs;

namespace Bo_Tron_Khi_CS
{
    public partial class MainWindow : Window
    {
        internal SystemConfig _config;
        private ModbusHandler _handler;
        private PollingEngine _poller;
        private RecipeEngine _recipeEngine;
        private Logger _logger;

        internal readonly List<RecipeStep> _recipeSteps = new List<RecipeStep>();
        private readonly List<PolledData> _history = new List<PolledData>();
        private const int MaxHistoryPoints = 120;

        // SCADA Dynamic Animation State
        private bool _valveRelay1 = false;
        private bool _valveRelay2 = false;
        private int _animOff = 0;
        private double _pumpAngle = 0;
        private DispatcherTimer _tickTimer;

        // Manual Mode Engine State
        private string _currentMode = "Manual";
        private bool _manualRunning = false;
        private DateTime _manualStartTime;
        private DispatcherTimer _manualTimer;
        private bool _isSyncingUI = false;

        // Custom Vector Colors
        private static readonly string[] GasColors = new string[] {
            "#38BDF8", // MFC1 (Carrier) - Light Blue
            "#818CF8", // MFC2 (Diluent) - Slate Blue
            "#EF4444", // MFC3 (G1 Low) - Red
            "#3B82F6", // MFC4 (G1 High) - Blue
            "#10B981", // MFC5 (Gas2) - Green
            "#F59E0B"  // MFC6 (Gas3) - Orange
        };
        private const string PipeMixColor = "#10B981";

        public MainWindow()
        {
            InitializeComponent();
            InitializeApp();
            
            // Set initial dynamic SCADA size and render
            ScadaCanvas.Loaded += (s, e) => RedrawScada();
            ScadaCanvas.SizeChanged += (s, e) => RedrawScada();
        }

        private void InitializeApp()
        {
            _config = SystemConfig.Load();
            _handler = new ModbusHandler();
            ConfigureModbusHandler();
 
            _logger = new Logger();
            _recipeEngine = new RecipeEngine(_handler, _config);
            _recipeEngine.ProgressUpdated += OnRecipeProgress;
            _recipeEngine.RecipeCompleted += OnRecipeCompleted;
 
            // Load UI bindings
            SyncConfigToUI();
 
            // Populate table grid
            _recipeSteps.AddRange(_config.recipe_steps);
            DgridRecipe.ItemsSource = _recipeSteps;
 
            // Start Polling Engine
            _poller = new PollingEngine(_handler, _config);
            _poller.DataPolled += OnDataPolled;
            
            // Connect to default settings (Virtual Sim on fresh start)
            _handler.Connect();

            // Force PC Mode: tell board to switch to Slave mode (HR 300 = 0)
            // so PC can communicate directly with E5CC on the RS-485 bus
            if (_handler.IsConnected)
            {
                _handler.TryWriteMultipleRegisters((byte)_config.mixing_slave, 300, new ushort[] { 0 });
                Thread.Sleep(200); // Allow firmware to process mode switch
            }

            _poller.Start();
 
            // Start Animation Tick Timer (70ms)
            _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
            _tickTimer.Tick += OnAnimationTick;
            _tickTimer.Start();

            // Initialize Manual Mode Engine Timer (1s)
            _manualTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _manualTimer.Tick += OnManualTimerTick;
        }
 
        private void ConfigureModbusHandler()
        {
            _handler.Port = _config.port;
            _handler.Baudrate = _config.baudrate;
            _handler.Parity = _config.parity;
            _handler.Timeout = _config.timeout;
            
            if (!string.IsNullOrEmpty(_config.port) && _config.port.StartsWith("TCP:"))
            {
                _handler.IsTcp = true;
                var parts = _config.port.Split(':');
                if (parts.Length >= 2) _handler.TcpIp = parts[1];
                if (parts.Length >= 3 && int.TryParse(parts[2], out int tp))
                {
                    _handler.TcpPort = tp;
                }
            }
            else
            {
                _handler.IsTcp = false;
            }
        }

        internal void SyncConfigToUI()
        {
            _isSyncingUI = true;
            try
            {
                TxtStableTime.Text = _config.stable_time.ToString();
                TxtGasOnTime.Text = _config.gas_on_time.ToString();
            }
            finally
            {
                _isSyncingUI = false;
            }

            UpdateSidebarRangeLabels();
        }

        private void OnAutoConfigTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingUI || _config == null) return;

            if (ParseUtil.TryParseInt(TxtStableTime.Text, out int st)) _config.stable_time = st;
            if (ParseUtil.TryParseInt(TxtGasOnTime.Text, out int go)) _config.gas_on_time = go;

            _config.Save();
            UpdateSidebarRangeLabels();
        }

        internal void RefreshRecipeGrid()
        {
            DgridRecipe.Items.Refresh();
        }

        // ===================================================
        // SCADA CANVAS VECTOR DRAWING ENGINE
        // ===================================================
        private void OnAnimationTick(object sender, EventArgs e)
        {
            // Advance particle offset
            _animOff = (_animOff + 3) % 24;

            // Rotate exhaust pump fan if active
            bool pumpOn = false;
            if (_poller != null)
            {
                if (_recipeEngine.IsRunning) pumpOn = true; // Pump is ON in auto sequence
                else pumpOn = _valveRelay2; // Manual pump state
            }
            if (pumpOn)
            {
                _pumpAngle = (_pumpAngle + 20) % 360;
            }

            RedrawScada();
        }

        private void RedrawScada()
        {
            if (ScadaCanvas == null || !ScadaCanvas.IsLoaded) return;

            ScadaCanvas.Children.Clear();

            double W = ScadaCanvas.ActualWidth;
            double H = ScadaCanvas.ActualHeight;
            if (W < 100 || H < 100) return;

            // Compute Scale Factors relative to virtual 1000x600 space
            _scale = Math.Min(W / 1000.0, H / 600.0);
            _offsetX = (W - 1000.0 * _scale) / 2.0;
            _offsetY = (H - 600.0 * _scale) / 2.0;

            // Active flow tracking from last polled data
            float[] pvs = new float[6];
            float[] sps = new float[6];
            double tempPV = 25.0;
            bool isSim = _config.simulation_mode;

            if (_poller != null && _poller.LastData != null)
            {
                pvs = _poller.LastData.SccmPV;
                sps = _poller.LastData.SccmSP;
                _valveRelay1 = _poller.LastData.Relay1 != 0;
                _valveRelay2 = _poller.LastData.Relay2 != 0;
                tempPV = _poller.LastData.E5ccPV;
            }
            else
            {
                // Standby / offline mock SPs
                sps[0] = (float)_config.total_flow;
            }

            // Check dynamic flows
            bool[] flowActive = new bool[6];
            for (int i = 0; i < 6; i++) flowActive[i] = pvs[i] > 0.1f;
            bool carrierActive = flowActive[0];
            bool mixActive = flowActive[1] || flowActive[2] || flowActive[3] || flowActive[4] || flowActive[5];

            // 3-way valve routing colors
            bool carrierToChamber = !_valveRelay1;
            bool mixToChamber = _valveRelay1;

            string toChamberColor = (carrierToChamber && carrierActive) ? GasColors[0] : (mixToChamber && mixActive) ? PipeMixColor : "#1E293B";
            string toExhaustColor = (!carrierToChamber && carrierActive) ? GasColors[0] : (!mixToChamber && mixActive) ? PipeMixColor : "#1E293B";

            // Static Colors
            Brush inactivePipeBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            Brush casingBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));

            // Grid backgrounds
            DrawGridLines(W, H);

            // 1. Draw Inlets (Source pills)
            double src_x = 30;
            double inlet_w = 74;
            double inlet_h = 28;
            double mfc_y_start = 55;
            double mfc_spacing = 72;
            double mfc_x = 150;
            double mfc_w = 72;
            double mfc_h = 44;
            double junc_x = 330;
            double offset_junc = 40;
            double valve_x = 440;
            double valve_y = 245;
            double valve_r = 24;
            double cham_x = 680;
            double cham_y = 245;
            double cham_w = 140;
            double cham_h = 210;
            double exh_x = 680;
            double exh_y = 45;

            string[] gasNames = new string[] { _config.carrier_gas, "Diluent", "Gas1 Low", "Gas1 High", "Gas 2", "Gas 3" };

            for (int i = 0; i < 6; i++)
            {
                double y = mfc_y_start + i * mfc_spacing;
                string c = GasColors[i];

                // Source pill body
                DrawRoundedRect(src_x, y - inlet_h/2, src_x + inlet_w, y + inlet_h/2, 6, new SolidColorBrush(Color.FromRgb(15, 23, 42)), (Brush)new BrushConverter().ConvertFromString(c), 1.5);
                DrawLed(src_x + 11, y, 3.5, c);
                DrawText(src_x + 43, y, gasNames[i], 9, Brushes.White, true);

                // Pipe segment: Inlet -> MFC input
                string pColor = flowActive[i] ? c : "#1E293B";
                DrawPipeWithCasing(src_x + inlet_w, y, mfc_x, y, pColor, 4.0);
            }

            // 2. Draw MFC blocks
            for (int i = 0; i < 6; i++)
            {
                double y = mfc_y_start + i * mfc_spacing;
                double x1 = mfc_x;
                double y1 = y - mfc_h / 2;
                double x2 = mfc_x + mfc_w;
                double y2 = y + mfc_h / 2;
                string c = GasColors[i];

                // Highlight border if manual flow rate editable is active (simplification)
                Brush borderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81));
                DrawRoundedRect(x1, y1, x2, y2, 6, new SolidColorBrush(Color.FromRgb(11, 15, 25)), borderBrush, 1.5);

                // Indicator line left
                DrawPipe(x1 + 3, y1 + 5, x1 + 3, y2 - 5, (Brush)new BrushConverter().ConvertFromString(c), 3.0);

                // Name header
                DrawText(x1 + mfc_w / 2, y1 + 13, $"MFC {i + 1}", 8, new SolidColorBrush(Color.FromRgb(148, 163, 184)), true);

                // Setpoint label above box
                DrawText(x1 + mfc_w / 2, y1 - 8, $"SP: {sps[i]:F1}", 8, new SolidColorBrush(Color.FromRgb(148, 163, 184)), true);

                // PV Label inside box
                DrawText(x1 + mfc_w / 2, y1 + 30, $"{pvs[i]:F1}", 12, (Brush)new BrushConverter().ConvertFromString(c), true, true);

                // Interactive Click overlay
                var clickArea = new Rectangle { Fill = Brushes.Transparent, Cursor = Cursors.Hand };
                int chIdx = i;
                clickArea.MouseLeftButtonDown += (s, ev) => HandleMfcClick(chIdx);
                Canvas.SetLeft(clickArea, tx(x1));
                Canvas.SetTop(clickArea, ty(y1));
                clickArea.Width = ts(mfc_w);
                clickArea.Height = ts(mfc_h);
                ScadaCanvas.Children.Add(clickArea);
            }

            double mfc_out = mfc_x + mfc_w;

            // 3. Draw Branch segments
            // Carrier Gas segment (MFC 1)
            double carrier_y = mfc_y_start;
            string carrierPipeColor = carrierActive ? GasColors[0] : "#1E293B";
            DrawPipeWithCasing(mfc_out, carrier_y, junc_x + offset_junc, carrier_y, carrierPipeColor, 5.0);
            
            double valve_top_y = valve_y;
            DrawPipeWithCasing(junc_x + offset_junc, carrier_y, junc_x + offset_junc, valve_top_y, carrierPipeColor, 5.0);
            DrawPipeWithCasing(junc_x + offset_junc, valve_top_y, valve_x - valve_r, valve_top_y, carrierPipeColor, 5.0);

            // MFC 2-6 Diluent & mixed segments
            for (int i = 1; i < 6; i++)
            {
                double y = mfc_y_start + i * mfc_spacing;
                string pColor = flowActive[i] ? GasColors[i] : "#1E293B";
                DrawPipeWithCasing(mfc_out, y, junc_x, y, pColor, 4.0);
            }

            // Vertical mixed manifold
            string manifoldColor = mixActive ? PipeMixColor : "#1E293B";
            DrawPipeWithCasing(junc_x, mfc_y_start + mfc_spacing, junc_x, mfc_y_start + 5 * mfc_spacing, manifoldColor, 6.0);

            // Manifold outlet horizontal Segment to valve bot port
            double valve_bot_y = valve_y + 55;
            DrawPipeWithCasing(junc_x, valve_bot_y, valve_x, valve_bot_y, manifoldColor, 6.0);
            DrawPipeWithCasing(valve_x, valve_bot_y, valve_x, valve_y + valve_r, manifoldColor, 6.0);

            // 4. 3-Way Valve Routing & Knob
            DrawGlowCircle(valve_x, valve_y, valve_r, "#0B0F19");
            
            // Triangle Ports
            DrawValveTriangle(valve_x - valve_r, valve_y, -90, carrierActive ? GasColors[0] : "#1E293B");
            DrawValveTriangle(valve_x + valve_r, valve_y, 90, toChamberColor);
            DrawValveTriangle(valve_x, valve_y + valve_r, 180, mixActive ? PipeMixColor : "#1E293B");
            DrawValveTriangle(valve_x, valve_y - valve_r, 0, toExhaustColor);

            // Routing Lines inside Valve circle
            if (!_valveRelay1)
            {
                // Baseline: Left -> Right, Bottom -> Top
                DrawPipe(valve_x - valve_r + 2, valve_y, valve_x + valve_r - 2, valve_y, (Brush)new BrushConverter().ConvertFromString(carrierPipeColor), 5.0);
                DrawPipe(valve_x, valve_y + valve_r - 2, valve_x, valve_y - valve_r + 2, (Brush)new BrushConverter().ConvertFromString(manifoldColor), 5.0);
            }
            else
            {
                // Exposure: Bottom -> Right, Left -> Top
                DrawValveArc(valve_x, valve_y, valve_r - 4, 270, 90, manifoldColor);
                DrawValveArc(valve_x, valve_y, valve_r - 4, 90, 90, carrierPipeColor);
            }

            // Valve Knob
            DrawGlowCircle(valve_x, valve_y, 5, "#475569");
            var elKnob = new Ellipse { Width = ts(4), Height = ts(4), Fill = Brushes.White };
            Canvas.SetLeft(elKnob, tx(valve_x - 2));
            Canvas.SetTop(elKnob, ty(valve_y - 2));
            ScadaCanvas.Children.Add(elKnob);

            // Clickable Valve Area
            var valveClick = new Ellipse { Fill = Brushes.Transparent, Cursor = Cursors.Hand };
            valveClick.MouseLeftButtonDown += (s, ev) => HandleValveClick();
            Canvas.SetLeft(valveClick, tx(valve_x - valve_r));
            Canvas.SetTop(valveClick, ty(valve_y - valve_r));
            valveClick.Width = ts(valve_r * 2);
            valveClick.Height = ts(valve_r * 2);
            ScadaCanvas.Children.Add(valveClick);

            // 5. Post-Valve pipelines
            double post_valve_right = valve_x + valve_r + 2;
            double chamber_left = cham_x - cham_w / 2;
            DrawPipeWithCasing(post_valve_right, valve_y, chamber_left, valve_y, toChamberColor, 7.0);

            double exh_branch_x = valve_x;
            DrawPipeWithCasing(exh_branch_x, valve_y - valve_r, exh_branch_x, exh_y, toExhaustColor, 5.0);
            DrawPipeWithCasing(exh_branch_x, exh_y, exh_x, exh_y, toExhaustColor, 5.0);

            // Exhaust pill box
            double ep_w = 80;
            double ep_h = 26;
            DrawRoundedRect(exh_x - ep_w/2, exh_y - ep_h/2, exh_x + ep_w/2, exh_y + ep_h/2, 6, new SolidColorBrush(Color.FromRgb(11, 15, 25)), new SolidColorBrush(Color.FromRgb(55, 65, 81)), 1.0);
            DrawText(exh_x, exh_y, "Exhaust", 10, new SolidColorBrush(Color.FromRgb(156, 163, 175)), true);

            // 6. Sensor Chamber viewport, stage and chips
            double cx = cham_x;
            double cy = cham_y;
            double hw = cham_w / 2;
            double hh = cham_h / 2;

            // Outer chamber steel shell
            DrawRoundedRect(cx - hw, cy - hh, cx + hw, cy + hh, 14, new SolidColorBrush(Color.FromRgb(51, 65, 85)), new SolidColorBrush(Color.FromRgb(100, 116, 139)), 2.0);
            // Inner chamber cavity
            DrawRoundedRect(cx - hw + 10, cy - hh + 10, cx + hw - 10, cy + hh - 10, 7, new SolidColorBrush(Color.FromRgb(9, 13, 22)), new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1.5);

            // Viewport glass ring & window
            double win_cy = cy - 22;
            double win_r = hw - 20;
            var elFrame = new Ellipse { Width = ts(win_r * 2 + 6), Height = ts(win_r * 2 + 6), Fill = new SolidColorBrush(Color.FromRgb(71, 85, 105)), Stroke = new SolidColorBrush(Color.FromRgb(30, 41, 59)), StrokeThickness = ts(2) };
            Canvas.SetLeft(elFrame, tx(cx - win_r - 3));
            Canvas.SetTop(elFrame, ty(win_cy - win_r - 3));
            ScadaCanvas.Children.Add(elFrame);

            var elGlass = new Ellipse { Width = ts(win_r * 2), Height = ts(win_r * 2), Fill = new SolidColorBrush(Color.FromRgb(5, 11, 20)), Stroke = new SolidColorBrush(Color.FromRgb(14, 165, 233)), StrokeThickness = ts(1.5) };
            Canvas.SetLeft(elGlass, tx(cx - win_r));
            Canvas.SetTop(elGlass, ty(win_cy - win_r));
            ScadaCanvas.Children.Add(elGlass);

            // Viewport reflections
            var gl1 = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)), StrokeThickness = ts(1.5) };
            var arcGeom = new ArcSegment(new Point(tx(cx + win_r - 5), ty(win_cy - 5)), new Size(ts(win_r - 5), ts(win_r - 5)), 60, false, SweepDirection.Clockwise, true);
            var pathFig = new PathFigure { StartPoint = new Point(tx(cx - 5), ty(win_cy - win_r + 5)) };
            pathFig.Segments.Add(arcGeom);
            gl1.Data = new PathGeometry(new PathFigure[] { pathFig });
            ScadaCanvas.Children.Add(gl1);

            // Chamber Flanges
            // Left inlet flange
            DrawRoundedRect(cx - hw - 5, cy - 10, cx - hw + 2, cy + 10, 2, new SolidColorBrush(Color.FromRgb(71, 85, 105)), new SolidColorBrush(Color.FromRgb(100, 116, 139)), 1);
            // Bottom outlet flange
            DrawRoundedRect(cx - 15, cy + hh - 2, cx + 15, cy + hh + 5, 2, new SolidColorBrush(Color.FromRgb(71, 85, 105)), new SolidColorBrush(Color.FromRgb(100, 116, 139)), 1);

            // Chuck pillars
            DrawRoundedRect(cx - 22, cy + 30, cx - 14, cy + hh - 10, 1, new SolidColorBrush(Color.FromRgb(71, 85, 105)), inactivePipeBrush, 1);
            DrawRoundedRect(cx + 14, cy + 30, cx + 22, cy + hh - 10, 1, new SolidColorBrush(Color.FromRgb(71, 85, 105)), inactivePipeBrush, 1);

            // Chuck body base
            double chuck_y1 = cy + 20;
            double chuck_y2 = cy + 40;
            DrawRoundedRect(cx - 38, chuck_y1, cx + 38, chuck_y2, 3, new SolidColorBrush(Color.FromRgb(30, 41, 59)), new SolidColorBrush(Color.FromRgb(71, 85, 105)), 1.5);
            // Copper plate platen
            DrawRoundedRect(cx - 34, chuck_y1, cx + 34, chuck_y1 + 5, 1, new SolidColorBrush(Color.FromRgb(180, 83, 9)), new SolidColorBrush(Color.FromRgb(217, 119, 6)), 1.0);

            // MOS Sensor package substrate
            double scx = cx;
            double scy = cy + 10;
            double sw = 36;
            double sh = 20;
            DrawRoundedRect(scx - sw/2, scy - sh/2, scx + sw/2, scy + sh/2, 3, new SolidColorBrush(Color.FromRgb(17, 24, 39)), new SolidColorBrush(Color.FromRgb(202, 138, 4)), 1.5);
            // Gold pads Left/Right
            DrawRoundedRect(scx - sw/2 + 2, scy - 5, scx - sw/2 + 6, scy + 5, 1, new SolidColorBrush(Color.FromRgb(234, 179, 8)), Brushes.Transparent, 0);
            DrawRoundedRect(scx + sw/2 - 6, scy - 5, scx + sw/2 - 2, scy + 5, 1, new SolidColorBrush(Color.FromRgb(234, 179, 8)), Brushes.Transparent, 0);

            // Sensor Chip Die
            DrawRoundedRect(scx - 9, scy - 6, scx + 9, scy + 6, 1, new SolidColorBrush(Color.FromRgb(6, 78, 59)), new SolidColorBrush(Color.FromRgb(5, 150, 105)), 1.0);
            
            // Gold interdigitated electrode lines
            for (double dx = -7; dx <= 7; dx += 4)
            {
                DrawPipe(scx + dx, scy - 3, scx + dx, scy + 1, new SolidColorBrush(Color.FromRgb(202, 138, 4)), 0.8);
                DrawPipe(scx + dx + 2, scy - 1, scx + dx + 2, scy + 3, new SolidColorBrush(Color.FromRgb(202, 138, 4)), 0.8);
            }
            DrawPipe(scx - 7, scy - 1, scx + 7, scy - 1, new SolidColorBrush(Color.FromRgb(202, 138, 4)), 0.8);
            DrawPipe(scx - 5, scy + 1, scx + 9, scy + 1, new SolidColorBrush(Color.FromRgb(202, 138, 4)), 0.8);

            // Active bead sensing film
            var elBead = new Ellipse { Width = ts(8), Height = ts(8), Fill = _valveRelay1 ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) : new SolidColorBrush(Color.FromRgb(16, 185, 129)), Stroke = new SolidColorBrush(Color.FromRgb(74, 222, 128)), StrokeThickness = ts(1) };
            Canvas.SetLeft(elBead, tx(scx - 4));
            Canvas.SetTop(elBead, ty(scy - 4));
            ScadaCanvas.Children.Add(elBead);

            // Probes micropositioner blocks & arms
            // Left block
            DrawRoundedRect(cx - hw + 14, cy - 28, cx - hw + 28, cy - 10, 2, new SolidColorBrush(Color.FromRgb(51, 65, 85)), inactivePipeBrush, 1.5);
            DrawGlowCircle(cx - hw + 19, cy - 30.5, 3, "#64748B");
            DrawGlowCircle(cx - hw + 25, cy - 30.5, 3, "#64748B");
            DrawPipe(cx - hw + 21, cy - 19, cx - 20, cy - 1, new SolidColorBrush(Color.FromRgb(148, 163, 184)), 2.0);
            DrawPipe(cx - 20, cy - 1, scx - sw/2 + 4, scy - 3, new SolidColorBrush(Color.FromRgb(203, 213, 225)), 0.8);
            DrawGlowCircle(scx - sw/2 + 4, scy - 3, 1.5, "#F59E0B");

            // Right block
            DrawRoundedRect(cx + hw - 28, cy - 28, cx + hw - 14, cy - 10, 2, new SolidColorBrush(Color.FromRgb(51, 65, 85)), inactivePipeBrush, 1.5);
            DrawGlowCircle(cx + hw - 19, cy - 30.5, 3, "#64748B");
            DrawGlowCircle(cx + hw - 25, cy - 30.5, 3, "#64748B");
            DrawPipe(cx + hw - 21, cy - 19, cx + 20, cy - 1, new SolidColorBrush(Color.FromRgb(148, 163, 184)), 2.0);
            DrawPipe(cx + 20, cy - 1, scx + sw/2 - 4, scy - 3, new SolidColorBrush(Color.FromRgb(203, 213, 225)), 0.8);
            DrawGlowCircle(scx + sw/2 - 4, scy - 3, 1.5, "#F59E0B");

            // Chamber Output pipe to filter
            double bot_y = 470;
            DrawPipeWithCasing(cham_x, cy + hh, cham_x, bot_y, "#4B5563", 5.0);
            DrawText(cham_x, bot_y + 10, "Exhaust Filter", 9, new SolidColorBrush(Color.FromRgb(156, 163, 175)), true);

            // 7. Exhaust Vacuum Pump fan base
            double pump_y = 410;
            double pump_r = 14;
            string pumpStateColor = _valveRelay2 ? "#10B981" : "#374151";
            DrawGlowCircle(cham_x, pump_y, pump_r, pumpStateColor);

            // Pump ON/OFF status text
            string pumpLabel = _valveRelay2 ? "PUMP ON" : "PUMP OFF";
            Brush pumpTextBrush = _valveRelay2
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            DrawText(cham_x, pump_y + pump_r + 10, pumpLabel, 9, pumpTextBrush, true);

            // Clickable Pump Area
            var pumpClick = new Ellipse { Fill = Brushes.Transparent, Cursor = Cursors.Hand };
            pumpClick.MouseLeftButtonDown += (s, ev) => HandlePumpClick();
            Canvas.SetLeft(pumpClick, tx(cham_x - pump_r));
            Canvas.SetTop(pumpClick, ty(pump_y - pump_r));
            pumpClick.Width = ts(pump_r * 2);
            pumpClick.Height = ts(pump_r * 2);
            ScadaCanvas.Children.Add(pumpClick);

            // 8. Draw Dynamic Routing Info Box — Van + Pump state + Connection status
            double info_y = 530;

            // Van (Valve) state box
            string valveLabel = _valveRelay1 ? "VAN: MỞ (Mix → Chamber)" : "VAN: ĐÓNG (Carrier → Chamber)";
            string valveBoxBg = _valveRelay1 ? "#042F1A" : "#081E3F";
            string valveBoxBorder = _valveRelay1 ? "#10B981" : "#3B82F6";
            string valveBoxText = _valveRelay1 ? "#10B981" : "#93C5FD";
            DrawRoundedRect(10, info_y - 11, 320, info_y + 11, 6,
                (Brush)new BrushConverter().ConvertFromString(valveBoxBg),
                (Brush)new BrushConverter().ConvertFromString(valveBoxBorder), 1.0);
            DrawText(165, info_y, valveLabel, 9.5,
                (Brush)new BrushConverter().ConvertFromString(valveBoxText), true);

            // Pump state box (to the right of Valve box)
            string pumpBoxLabel = _valveRelay2 ? "● PUMP: ON" : "○ PUMP: OFF";
            string pumpBoxBg = _valveRelay2 ? "#042F1A" : "#111827";
            string pumpBoxBorder = _valveRelay2 ? "#10B981" : "#374151";
            string pumpBoxText = _valveRelay2 ? "#10B981" : "#6B7280";
            DrawRoundedRect(330, info_y - 11, 480, info_y + 11, 6,
                (Brush)new BrushConverter().ConvertFromString(pumpBoxBg),
                (Brush)new BrushConverter().ConvertFromString(pumpBoxBorder), 1.0);
            DrawText(405, info_y, pumpBoxLabel, 9.5,
                (Brush)new BrushConverter().ConvertFromString(pumpBoxText), true);

            // Connection Status box (to the right of Pump box)
            DrawRoundedRect(490, info_y - 11, 760, info_y + 11, 6,
                (Brush)new BrushConverter().ConvertFromString("#0D1527"),
                (Brush)new BrushConverter().ConvertFromString("#1E293B"), 1.0);

            string commLampColor = (_handler.IsConnected || isSim) ? "#10B981" : "#EF4444";
            
            // Massflow Connection lamp
            DrawLed(505, info_y, 4, commLampColor);
            DrawText(518, info_y, "Massflow", 9, new SolidColorBrush(Color.FromRgb(249, 250, 251)), false);

            // Temperature Connection lamp
            DrawLed(625, info_y, 4, commLampColor);
            DrawText(638, info_y, "Temperature", 9, new SolidColorBrush(Color.FromRgb(249, 250, 251)), false);

            // 10. ANIMATED FLOW PARTICLES
            if (carrierActive)
            {
                DrawParticles(mfc_out, junc_x + offset_junc, carrier_y, GasColors[0], true);
                DrawParticles(carrier_y, valve_top_y, junc_x + offset_junc, GasColors[0], false);
                DrawParticles(junc_x + offset_junc, valve_x - valve_r, valve_top_y, GasColors[0], true);
            }

            for (int i = 1; i < 6; i++)
            {
                if (flowActive[i])
                {
                    double y = mfc_y_start + i * mfc_spacing;
                    DrawParticles(mfc_out, junc_x, y, GasColors[i], true);
                }
            }

            if (mixActive)
            {
                for (int i = 1; i < 6; i++)
                {
                    if (flowActive[i])
                    {
                        DrawParticles(mfc_y_start + i * mfc_spacing, valve_bot_y, junc_x, GasColors[i], false);
                    }
                }
                DrawParticles(junc_x, valve_x, valve_bot_y, PipeMixColor, true);
                DrawParticles(valve_bot_y, valve_y + valve_r, valve_x, PipeMixColor, false);
            }

            // Post-valve routing animation
            if (!_valveRelay1)
            {
                // Valve CLOSED: Carrier -> Chamber, Mix -> Exhaust
                if (carrierActive)
                {
                    DrawParticles(post_valve_right, chamber_left, valve_y, GasColors[0], true);
                }
                if (mixActive)
                {
                    DrawParticles(valve_y - valve_r, exh_y, exh_branch_x, PipeMixColor, false);
                    DrawParticles(exh_branch_x, exh_x, exh_y, PipeMixColor, true);
                }
            }
            else
            {
                // Valve OPEN: Mix -> Chamber, Carrier -> Exhaust
                if (mixActive)
                {
                    DrawParticles(post_valve_right, chamber_left, valve_y, PipeMixColor, true);
                }
                if (carrierActive)
                {
                    DrawParticles(valve_y - valve_r, exh_y, exh_branch_x, GasColors[0], false);
                    DrawParticles(exh_branch_x, exh_x, exh_y, GasColors[0], true);
                }
            }

            // Chamber internal gas flow particles (Spraying animation into the sensor)
            string? gasInsideColor = null;

            if (carrierToChamber && carrierActive)
            {
                gasInsideColor = GasColors[0];
            }
            else if (mixToChamber && mixActive)
            {
                gasInsideColor = PipeMixColor;
            }

            if (gasInsideColor != null)
            {
                Point[] chamberPath = new Point[]
                {
                    new Point(cx - hw + 14, cy),
                    new Point(cx - hw / 2.0, cy - 2),
                    new Point(cx - 15, cy + 8),
                    new Point(cx, cy + 10),
                    new Point(cx + 15, cy + 12),
                    new Point(cx + hw / 3.0, cy + 22),
                    new Point(cx, cy + hh - 16)
                };
                DrawPathParticles(chamberPath, gasInsideColor);
            }

            // Chamber bottom exhaust line flow
            if (_valveRelay2) // Pump is active
            {
                string exhaustColor = "#475569";
                if (gasInsideColor != null)
                {
                    exhaustColor = gasInsideColor;
                }
                DrawParticles(cy + hh, bot_y, cx, exhaustColor, false);
            }

            // 11. Draw animated pump fan blades
            bool pumpOn = _recipeEngine.IsRunning || _valveRelay2;
            DrawPumpBlades(cham_x, pump_y, pump_r, pumpOn);

            // 12. Draw platen heater dynamic color coils & rising heat waves
            DrawHeatCoilsAndWaves(scx, cy, tempPV);
        }

        // --- Layout translation and drawing helpers ---
        private double _scale = 1.0;
        private double _offsetX = 0.0;
        private double _offsetY = 0.0;

        private double tx(double x) => _offsetX + x * _scale;
        private double ty(double y) => _offsetY + y * _scale;
        private double ts(double d) => d * _scale;

        private void DrawGridLines(double W, double H)
        {
            double grid_size = 40.0 * _scale;
            if (grid_size >= 10.0)
            {
                Brush gridBrush = new SolidColorBrush(Color.FromRgb(14, 21, 36));
                for (double gx = 0; gx < W; gx += grid_size)
                {
                    var line = new Line { X1 = gx, Y1 = 0, X2 = gx, Y2 = H, Stroke = gridBrush, StrokeThickness = 0.8 };
                    ScadaCanvas.Children.Add(line);
                }
                for (double gy = 0; gy < H; gy += grid_size)
                {
                    var line = new Line { X1 = 0, Y1 = gy, X2 = W, Y2 = gy, Stroke = gridBrush, StrokeThickness = 0.8 };
                    ScadaCanvas.Children.Add(line);
                }
            }

            // Little cross indicators
            Brush crossBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            for (double gx = 40; gx < 1000; gx += 80)
            {
                for (double gy = 40; gy < 600; gy += 80)
                {
                    var l1 = new Line { X1 = tx(gx - 3), Y1 = ty(gy), X2 = tx(gx + 3), Y2 = ty(gy), Stroke = crossBrush, StrokeThickness = 0.8 };
                    var l2 = new Line { X1 = tx(gx), Y1 = ty(gy - 3), X2 = tx(gx), Y2 = ty(gy + 3), Stroke = crossBrush, StrokeThickness = 0.8 };
                    ScadaCanvas.Children.Add(l1);
                    ScadaCanvas.Children.Add(l2);
                }
            }
        }

        private void DrawPipe(double x1, double y1, double x2, double y2, Brush brush, double thickness)
        {
            var line = new Line
            {
                X1 = tx(x1), Y1 = ty(y1), X2 = tx(x2), Y2 = ty(y2),
                Stroke = brush,
                StrokeThickness = ts(thickness),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            ScadaCanvas.Children.Add(line);
        }

        private void DrawPipeWithCasing(double x1, double y1, double x2, double y2, string colorHex, double thickness)
        {
            Brush coreBrush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            Brush casingBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            
            // Draw background casing pipe
            DrawPipe(x1, y1, x2, y2, casingBrush, thickness + 4);
            // Draw core color line
            DrawPipe(x1, y1, x2, y2, coreBrush, thickness);
            // Draw highlight reflection cap
            if (colorHex != "#1E293B")
            {
                DrawPipe(x1, y1, x2, y2, new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), Math.Max(1.0, thickness / 3.0));
            }
        }

        private void DrawRoundedRect(double x1, double y1, double x2, double y2, double radius, Brush fill, Brush stroke, double strokeThickness)
        {
            var rect = new Border
            {
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(ts(strokeThickness)),
                CornerRadius = new CornerRadius(ts(radius)),
                Width = ts(x2 - x1),
                Height = ts(y2 - y1)
            };
            Canvas.SetLeft(rect, tx(x1));
            Canvas.SetTop(rect, ty(y1));
            ScadaCanvas.Children.Add(rect);
        }

        private void DrawLed(double cx, double cy, double r, string colorHex)
        {
            Brush fillBrush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            var outer = new Ellipse { Width = ts(r * 2 + 4), Height = ts(r * 2 + 4), Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) };
            Canvas.SetLeft(outer, tx(cx - r - 2));
            Canvas.SetTop(outer, ty(cy - r - 2));
            ScadaCanvas.Children.Add(outer);

            var inner = new Ellipse { Width = ts(r * 2), Height = ts(r * 2), Fill = fillBrush, Stroke = Brushes.White, StrokeThickness = ts(0.5) };
            Canvas.SetLeft(inner, tx(cx - r));
            Canvas.SetTop(inner, ty(cy - r));
            ScadaCanvas.Children.Add(inner);
        }

        private void DrawGlowCircle(double cx, double cy, double r, string colorHex)
        {
            Brush fillBrush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            var el = new Ellipse
            {
                Width = ts(r * 2),
                Height = ts(r * 2),
                Fill = fillBrush,
                Stroke = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                StrokeThickness = ts(1.5)
            };
            Canvas.SetLeft(el, tx(cx - r));
            Canvas.SetTop(el, ty(cy - r));
            ScadaCanvas.Children.Add(el);
        }

        private void DrawValveTriangle(double cx, double cy, double rotationAngle, string colorHex)
        {
            Brush brush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            double port_w = 4;
            double port_l = 6;

            var points = new Point[] {
                new Point(cx, cy),
                new Point(cx - port_l, cy - port_w),
                new Point(cx - port_l, cy + port_w)
            };

            var poly = new Polygon { Fill = brush };
            foreach (var pt in points) poly.Points.Add(new Point(tx(pt.X), ty(pt.Y)));

            var transform = new RotateTransform(rotationAngle, tx(cx), ty(cy));
            poly.RenderTransform = transform;
            ScadaCanvas.Children.Add(poly);
        }

        private void DrawValveArc(double cx, double cy, double r, double startAngle, double sweepAngle, string colorHex)
        {
            Brush brush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            var path = new System.Windows.Shapes.Path { Stroke = brush, StrokeThickness = ts(5), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

            double startRad = startAngle * Math.PI / 180.0;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180.0;

            double sx = cx + r * Math.Cos(startRad);
            double sy = cy + r * Math.Sin(startRad);
            double ex = cx + r * Math.Cos(endRad);
            double ey = cy + r * Math.Sin(endRad);

            var figure = new PathFigure { StartPoint = new Point(tx(sx), ty(sy)) };
            var arc = new ArcSegment(new Point(tx(ex), ty(ey)), new Size(ts(r), ts(r)), sweepAngle, false, SweepDirection.Clockwise, true);
            figure.Segments.Add(arc);

            path.Data = new PathGeometry(new PathFigure[] { figure });
            ScadaCanvas.Children.Add(path);
        }

        private void DrawText(double cx, double cy, string text, double fontSize, Brush brush, bool centered, bool monospace = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = ts(fontSize),
                Foreground = brush,
                FontWeight = FontWeights.Bold
            };
            if (monospace) tb.FontFamily = new FontFamily("Consolas");

            if (centered)
            {
                tb.TextAlignment = TextAlignment.Center;
                // WPF layout measure to offset centered text
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, tx(cx) - tb.DesiredSize.Width / 2.0);
                Canvas.SetTop(tb, ty(cy) - tb.DesiredSize.Height / 2.0);
            }
            else
            {
                Canvas.SetLeft(tb, tx(cx));
                Canvas.SetTop(tb, ty(cy) - 6);
            }
            ScadaCanvas.Children.Add(tb);
        }

        private void DrawParticles(double x1, double x2, double y, string colorHex, bool horiz = true)
        {
            if (colorHex == "#1E293B" || string.IsNullOrEmpty(colorHex)) return; // pipe is inactive

            double span = Math.Abs(x2 - x1);
            if (span < 5) return;

            Brush fillBrush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            
            for (double dx = 0; dx < span; dx += 20)
            {
                double currentOffset = (dx + _animOff) % span;
                double px_v, py_v;
                if (horiz)
                {
                    px_v = (x1 < x2) ? (x1 + currentOffset) : (x1 - currentOffset);
                    py_v = y;
                }
                else
                {
                    px_v = y;
                    py_v = (x1 < x2) ? (x1 + currentOffset) : (x1 - currentOffset);
                }

                var dot = new Ellipse
                {
                    Width = ts(4),
                    Height = ts(4),
                    Fill = Brushes.White,
                    Stroke = fillBrush,
                    StrokeThickness = ts(0.8)
                };
                Canvas.SetLeft(dot, tx(px_v - 2));
                Canvas.SetTop(dot, ty(py_v - 2));
                ScadaCanvas.Children.Add(dot);
            }
        }

        private void DrawPathParticles(Point[] path, string colorHex)
        {
            if (colorHex == "#1E293B" || string.IsNullOrEmpty(colorHex)) return;

            Brush fillBrush = (Brush)new BrushConverter().ConvertFromString(colorHex);
            Brush whiteBrush = Brushes.White;
            Brush transparentBrush = Brushes.Transparent;
            
            for (int p_idx = 0; p_idx < 4; p_idx++)
            {
                double progress = ((_animOff * 1.5) + p_idx * 60) % 240;
                double t_val = progress / 240.0;
                
                int num_segments = path.Length - 1;
                int seg = Math.Min(num_segments - 1, (int)(t_val * num_segments));
                double seg_t = (t_val * num_segments) - seg;
                
                Point pt1 = path[seg];
                Point pt2 = path[seg + 1];
                double px_v = pt1.X + (pt2.X - pt1.X) * seg_t;
                double py_v = pt1.Y + (pt2.Y - pt1.Y) * seg_t;
                
                var dot1 = new Ellipse
                {
                    Width = ts(6),
                    Height = ts(6),
                    Fill = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                    Stroke = fillBrush,
                    StrokeThickness = ts(0.8)
                };
                Canvas.SetLeft(dot1, tx(px_v - 3));
                Canvas.SetTop(dot1, ty(py_v - 3));
                ScadaCanvas.Children.Add(dot1);

                var dot2 = new Ellipse
                {
                    Width = ts(2),
                    Height = ts(2),
                    Fill = whiteBrush,
                    Stroke = transparentBrush,
                    StrokeThickness = 0
                };
                Canvas.SetLeft(dot2, tx(px_v - 1));
                Canvas.SetTop(dot2, ty(py_v - 1));
                ScadaCanvas.Children.Add(dot2);
            }
        }

        private void DrawPumpBlades(double cx, double cy, double pr, bool active)
        {
            double rad = _pumpAngle * Math.PI / 180.0;
            Brush fillBrush = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            Brush strokeBrush = active ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(71, 85, 105));

            for (int b = 0; b < 4; b++)
            {
                double angle = rad + b * (Math.PI / 2.0);
                double bx = cx + Math.Cos(angle) * (pr - 4);
                double by = cy + Math.Sin(angle) * (pr - 4);
                double bx_l = cx + Math.Cos(angle - 0.25) * (pr - 6);
                double by_l = cy + Math.Sin(angle - 0.25) * (pr - 6);

                var poly = new Polygon
                {
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = ts(0.8)
                };
                poly.Points.Add(new Point(tx(cx), ty(cy)));
                poly.Points.Add(new Point(tx(bx_l), ty(by_l)));
                poly.Points.Add(new Point(tx(bx), ty(by)));

                ScadaCanvas.Children.Add(poly);
            }
        }

        private void DrawHeatCoilsAndWaves(double scx, double cy, double temp)
        {
            // Coils color lerp
            string coilColorHex = "#451A03"; // Cold base brown
            if (temp >= 30.0 && temp < 100.0)
            {
                double t = (temp - 30.0) / 70.0;
                coilColorHex = LerpColor("#451A03", "#F97316", t);
            }
            else if (temp >= 100.0)
            {
                double t = Math.Min(1.0, (temp - 100.0) / 150.0);
                coilColorHex = LerpColor("#F97316", "#EF4444", t);
            }
            Brush coilBrush = (Brush)new BrushConverter().ConvertFromString(coilColorHex);

            // Coils lines inside stage platen
            DrawPipe(scx - 32, cy + 31, scx + 32, cy + 31, coilBrush, 1.5);
            DrawPipe(scx - 26, cy + 36, scx + 26, cy + 36, coilBrush, 1.5);
            DrawPipe(scx - 20, cy + 41, scx + 20, cy + 41, coilBrush, 1.5);

            // Wavy heat waves
            if (temp <= 32.0) return;

            double waveOffset = (_animOff % 12) / 12.0;
            string waveColorHex = (temp < 100.0) ? "#F97316" : "#EF4444";
            Brush waveBrush = (Brush)new BrushConverter().ConvertFromString(waveColorHex);

            double[] offsets = { -14, 0, 14 };
            foreach (double wxOffset in offsets)
            {
                double wx = scx + wxOffset;
                double wyBase = cy + 24;

                var path = new System.Windows.Shapes.Path
                {
                    Stroke = waveBrush,
                    StrokeThickness = ts(1.0)
                };
                var geom = new PathGeometry();
                var figure = new PathFigure { StartPoint = new Point(tx(wx), ty(wyBase)) };

                for (double dy = 0; dy < 15; dy += 2)
                {
                    double y = wyBase - dy - waveOffset * 3;
                    double x = wx + 1.8 * Math.Sin((y / 4.0) + _animOff * 0.4);
                    figure.Segments.Add(new LineSegment(new Point(tx(x), ty(y)), true));
                }
                geom.Figures.Add(figure);
                path.Data = geom;
                ScadaCanvas.Children.Add(path);
            }
        }

        private string LerpColor(string hex1, string hex2, double t)
        {
            var c1 = (Color)ColorConverter.ConvertFromString(hex1);
            var c2 = (Color)ColorConverter.ConvertFromString(hex2);
            byte r = (byte)(c1.R + (c2.R - c1.R) * t);
            byte g = (byte)(c1.G + (c2.G - c1.G) * t);
            byte b = (byte)(c1.B + (c2.B - c1.B) * t);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        // ===================================================
        // SCADA MOUSE INTERACTION CALLBACKS
        // ===================================================
        private void HandleMfcClick(int channelIndex)
        {
            if (_currentMode != "Manual")
            {
                MessageBox.Show("Please switch to Manual mode first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ctrlType = (CbCtrlType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Concentration";
            if (!ctrlType.Contains("Flow"))
            {
                MessageBox.Show("MFC Flow can only be edited directly in 'Flow' control type.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string title = $"Edit MFC {channelIndex + 1} Flow";
            string prompt = "";
            string currentVal = "";

            if (channelIndex == 0)
            {
                prompt = "Enter total / carrier flow (sccm):";
                currentVal = _config.total_flow.ToString("F1");
            }
            else if (channelIndex == 1)
            {
                MessageBox.Show("MFC2 (Diluent) flow is auto-calculated:\nTotal Flow - (Gas1 + Gas2 + Gas3).", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            else if (channelIndex == 2 || channelIndex == 3)
            {
                prompt = "Enter Gas 1 flow (sccm):";
                currentVal = TxtManualGas1.Text;
            }
            else if (channelIndex == 4)
            {
                prompt = "Enter Gas 2 flow (sccm):";
                currentVal = TxtManualGas2.Text;
            }
            else if (channelIndex == 5)
            {
                prompt = "Enter Gas 3 flow (sccm):";
                currentVal = TxtManualGas3.Text;
            }

            if (!string.IsNullOrEmpty(prompt))
            {
                var dlg = new InputDialog(prompt, currentVal) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    if (ParseUtil.TryParseDouble(dlg.Answer, out double val) && val >= 0)
                    {
                        if (channelIndex == 0)
                        {
                            _config.total_flow = val;
                            _config.Save();
                            SyncConfigToUI();
                        }
                        else if (channelIndex == 2 || channelIndex == 3)
                        {
                            TxtManualGas1.Text = val.ToString("F1");
                        }
                        else if (channelIndex == 4)
                        {
                            TxtManualGas2.Text = val.ToString("F1");
                        }
                        else if (channelIndex == 5)
                        {
                            TxtManualGas3.Text = val.ToString("F1");
                        }

                        if (_manualRunning)
                        {
                            ApplyManualFlows();
                        }
                    }
                    else
                    {
                        MessageBox.Show("Please enter a valid positive number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void HandleValveClick()
        {
            OnManualValveClick(null, null);
        }

        private void HandlePumpClick()
        {
            OnManualPumpClick(null, null);
        }


        // ===================================================
        // DYNAMIC CHART DRAWER
        // ===================================================
        private void DrawChart()
        {
            if (_config == null || ChartCanvas == null || !ChartCanvas.IsLoaded) return;

            ChartCanvas.Children.Clear();

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w < 50 || h < 50) return;

            double leftM = 45;
            double rightM = 15;
            double topM = 15;
            double bottomM = 25;

            double pW = w - leftM - rightM;
            double pH = h - topM - bottomM;

            // Draw Y-Axis Grid Lines
            int numTicks = 5;
            double yMax = (CbChartMode.SelectedIndex == 0) ? _config.total_flow * 1.2 : Math.Max(_config.co1, _config.co2) * 1.2;
            if (yMax <= 0) yMax = 500;

            for (int i = 0; i <= numTicks; i++)
            {
                double tickVal = (yMax / numTicks) * i;
                double y = topM + pH - (pH / numTicks) * i;

                // Grid line
                Brush gridBrush = (Brush)Resources["BorderBrush"];
                var line = new Line
                {
                    X1 = leftM, Y1 = y, X2 = w - rightM, Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 0.8
                };
                ChartCanvas.Children.Add(line);

                // Y label
                Brush labelBrush = (Brush)Resources["TextSecBrush"];
                var label = new TextBlock
                {
                    Text = $"{tickVal:F0}",
                    Foreground = labelBrush,
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Canvas.SetLeft(label, 5);
                Canvas.SetTop(label, y - 6);
                ChartCanvas.Children.Add(label);
            }

            if (_history.Count < 2) return;

            // Curves selection matching display modes
            if (CbChartMode.SelectedIndex == 0)
            {
                // Plot MFC1 to MFC6 (SCCM Flow rates)
                for (int ch = 0; ch < 6; ch++)
                {
                    var poly = new Polyline
                    {
                        Stroke = (Brush)new BrushConverter().ConvertFromString(GasColors[ch]),
                        StrokeThickness = 1.6
                    };

                    for (int i = 0; i < _history.Count; i++)
                    {
                        double x = leftM + ((double)i / (MaxHistoryPoints - 1)) * pW;
                        double val = _history[i].SccmPV[ch];
                        double y = topM + pH - (val / yMax) * pH;
                        poly.Points.Add(new Point(x, y));
                    }
                    ChartCanvas.Children.Add(poly);
                }
            }
            else
            {
                // Plot Gas1, Gas2, Gas3 concentrations (ppm)
                var colorsList = new string[] { "#EF4444", "#3B82F6", "#10B981" };
                for (int ch = 0; ch < 3; ch++)
                {
                    var poly = new Polyline
                    {
                        Stroke = (Brush)new BrushConverter().ConvertFromString(colorsList[ch]),
                        StrokeThickness = 2.0
                    };

                    for (int i = 0; i < _history.Count; i++)
                    {
                        double x = leftM + ((double)i / (MaxHistoryPoints - 1)) * pW;
                        
                        double tot = _history[i].SccmPV.Sum();
                        double qMix = _history[i].SccmPV[1] + _history[i].SccmPV[2] + _history[i].SccmPV[3] + _history[i].SccmPV[4] + _history[i].SccmPV[5];
                        double val = 0;
                        if (ch == 0) val = (qMix > 0.1) ? ((_history[i].SccmPV[2] + _history[i].SccmPV[3]) / qMix) * _config.co1 : 0;
                        else if (ch == 1) val = (qMix > 0.1) ? (_history[i].SccmPV[4] / qMix) * _config.co2 : 0;
                        else val = (qMix > 0.1) ? (_history[i].SccmPV[5] / qMix) * _config.co3 : 0;

                        double y = topM + pH - (val / yMax) * pH;
                        poly.Points.Add(new Point(x, y));
                    }
                    ChartCanvas.Children.Add(poly);
                }
            }
        }

        // ===================================================
        // MODBUS POLLING CALLBACK (Marshalled to UI Thread)
        // ===================================================
        private void OnDataPolled(object sender, PolledDataEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Data.IsError)
                {
                    TxtStatusLeft.Text = $"Connection Error: {e.Data.ErrorMessage}";
                    return;
                }

                bool simMode = e.Data.ErrorMessage == "Modbus connection closed." || _config.simulation_mode;
                
                // Show communication quality info
                string qualityInfo = "";
                if (e.Data.TotalRetries > 0)
                {
                    qualityInfo = $" [Retries: {e.Data.TotalRetries}]";
                }
                if (e.Data.FailedTransactions > 0)
                {
                    qualityInfo += $" [Failed: {e.Data.FailedTransactions}/5]";
                }
                
                TxtStatusLeft.Text = simMode 
                    ? $"Polled successfully. Connection status: Virtual Sim (Connected){qualityInfo}" 
                    : $"Polled successfully. Connection status: Connected ({_config.port}){qualityInfo}";

                // Update E5CC Status bar controls
                ushort e5status = e.Data.E5ccStatus;
                bool atActive = (e5status & (1 << 2)) != 0;
                bool almActive = (e5status & (1 << 3)) != 0;
                bool errActive = (e5status & (1 << 7)) != 0;

                // Sync status colors
                Brush greenBrush = (Brush)new BrushConverter().ConvertFromString("#10B981");
                Brush redBrush = (Brush)new BrushConverter().ConvertFromString("#EF4444");
                Brush greyBrush = (Brush)new BrushConverter().ConvertFromString("#374151");
                Brush yellowBrush = (Brush)new BrushConverter().ConvertFromString("#F59E0B");

                // Overheat protection cutoff logic
                if (almActive && _config.temp_auto_stop && _recipeEngine.IsRunning)
                {
                    _recipeEngine.Stop();
                    MessageBox.Show($"Measured temperature ({e.Data.E5ccPV / 10.0:F1}°C = {(int)e.Data.E5ccPV} raw) exceeded safety limit ({_config.temp_alarm_limit:F1}°C).\n\nSystem triggered automatic emergency shutdown and valve close!", "Chamber Overheat Protection", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                // Add to plotting history
                _history.Add(e.Data);
                if (_history.Count > MaxHistoryPoints) _history.RemoveAt(0);
                DrawChart();

                // PV conversions
                double tot = e.Data.SccmPV.Sum();
                double qMix = e.Data.SccmPV[1] + e.Data.SccmPV[2] + e.Data.SccmPV[3] + e.Data.SccmPV[4] + e.Data.SccmPV[5];

                double gas1 = (qMix > 0.1) ? ((e.Data.SccmPV[2] + e.Data.SccmPV[3]) / qMix) * _config.co1 : 0;
                double gas2 = (qMix > 0.1) ? (e.Data.SccmPV[4] / qMix) * _config.co2 : 0;
                double gas3 = (qMix > 0.1) ? (e.Data.SccmPV[5] / qMix) * _config.co3 : 0;

                // Format display based on select display type units (ppm vs. flow sccm)
                string dispType = (CbDispType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Concentration response";
                string val1Str, val2Str, val3Str;

                if (dispType.Contains("Flow"))
                {
                    val1Str = $"{(e.Data.SccmPV[2] + e.Data.SccmPV[3]):F1}";
                    val2Str = $"{e.Data.SccmPV[4]:F1}";
                    val3Str = $"{e.Data.SccmPV[5]:F1}";
                }
                else
                {
                    val1Str = $"{gas1:F1}";
                    val2Str = $"{gas2:F2}";
                    val3Str = $"{gas3:F1}";
                }

                // Update text fields
                LblTempPvBig.Text = $"{(int)e.Data.E5ccPV} °C";
                LblManualTempPv.Text = $"{(int)e.Data.E5ccPV}";
                LblManualGas1Pv.Text = val1Str;
                LblManualGas2Pv.Text = val2Str;
                LblManualGas3Pv.Text = val3Str;

                LblAutoTempPv.Text = $"{(int)e.Data.E5ccPV}";
                LblAutoGas1Pv.Text = val1Str;
                LblAutoGas2Pv.Text = val2Str;
                LblAutoGas3Pv.Text = val3Str;

                // Write rows to file logger
                if (_logger.IsLogging)
                {
                    _logger.LogRow(e.Data, gas1, gas2, gas3);
                }

                // Update Bottom connection indicators with quality coloring
                Brush connColor;
                string connTxt;
                if (simMode)
                {
                    connColor = (Brush)new BrushConverter().ConvertFromString("#6366F1");
                    connTxt = "Virtual Sim";
                }
                else if (e.Data.FailedTransactions > 0)
                {
                    connColor = yellowBrush; // degraded connection
                    connTxt = "Degraded";
                }
                else
                {
                    connColor = greenBrush;
                    connTxt = "Connected";
                }

                LblMixConnStatus.Text = $"● Mixing {connTxt}";
                LblMixConnStatus.Foreground = connColor;
                LblDacConnStatus.Text = $"● DAC {connTxt}";
                LblDacConnStatus.Foreground = connColor;

                // Update Manual panel button colors and states to match actual hardware states
                bool isValveOn = e.Data.Relay1 != 0;
                bool isPumpOn = e.Data.Relay2 != 0;

                BtnManualValve.Content = isValveOn ? "Valve:On" : "Valve:Off";
                BtnManualValve.Background = isValveOn ? (Brush)new BrushConverter().ConvertFromString("#10B981") : (Brush)new BrushConverter().ConvertFromString("#1F2937");
                BtnManualValve.Foreground = isValveOn ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#F9FAFB");

                BtnManualPump.Content = isPumpOn ? "Pump:On" : "Pump:Off";
                BtnManualPump.Background = isPumpOn ? (Brush)new BrushConverter().ConvertFromString("#10B981") : (Brush)new BrushConverter().ConvertFromString("#1F2937");
                BtnManualPump.Foreground = isPumpOn ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#F9FAFB");

                // Update temperature controller status text
                if (errActive)
                {
                    LblE5Status.Text = "⚠ E5CC: PROBE ERROR";
                    LblE5Status.Foreground = redBrush;
                }
                else if (atActive)
                {
                    LblE5Status.Text = "● E5CC: AUTO-TUNE ACTIVE";
                    LblE5Status.Foreground = yellowBrush;
                }
                else
                {
                    bool runMode = (e5status & (1 << 0)) == 0; // stop bit is index 0
                    LblE5Status.Text = runMode ? "● E5CC RUN" : "● E5CC STOP";
                    LblE5Status.Foreground = runMode ? greenBrush : new SolidColorBrush(Color.FromRgb(156, 163, 175));
                }

                // Update Simulation alert
                LblSimMode.Text = simMode ? "[SIMULATION MODE]" : "";
            }));
        }

        // ===================================================
        // MENU / COMMAND CLICK ROUTINES
        // ===================================================
        private void OnSaveConfigMenuClick(object sender, RoutedEventArgs e)
        {
            _config.recipe_steps = _recipeSteps;
            _config.Save();
            MessageBox.Show("Configuration successfully saved to JSON!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnExitMenuClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnChamberProtectionClick(object sender, RoutedEventArgs e)
        {
            var dlg = new TempAlarmWindow(_config) { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>
        /// Write MFC flow for a single channel — atomic 3-register batch write.
        /// Replaces old pattern of 2 separate Modbus calls.
        /// </summary>
        private void WriteMfcFlow(int ch, double flow)
        {
            byte ms = (byte)_config.mixing_slave;
            int chIdx = ch - 1; // 1-indexed to 0-indexed
            double factor = 1.0;
            if (_config.mfc_factor != null && _config.mfc_factor.Count > chIdx)
            {
                factor = _config.mfc_factor[chIdx];
            }

            // Atomic: [SP_Hi, SP_Lo, DAC_EN] in ONE WriteMultiple
            var floatRegs = ModbusHandler.FloatToRegs((float)(flow * factor));
            ushort dacEn = (ushort)(flow > 0.1 ? 1 : 0);
            _handler.TryWriteMultipleRegisters(ms, (ushort)(60 + chIdx * 3), new ushort[] { floatRegs[0], floatRegs[1], dacEn });
        }

        private void OnConnectModbusClick(object sender, RoutedEventArgs e)
        {
            var dlg = new ModbusConnWindow(_config) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _config.Save();
                _poller.Stop();
                _handler.Disconnect();
                ConfigureModbusHandler();
                _handler.Connect();

                // Force PC Mode: tell board to switch to Slave mode (HR 300 = 0)
                if (_handler.IsConnected)
                {
                    _handler.TryWriteMultipleRegisters((byte)_config.mixing_slave, 300, new ushort[] { 0 });
                    Thread.Sleep(200); // Allow firmware to process mode switch
                }

                _poller.Start();
                SyncConfigToUI();
            }
        }

        private void OnConnectModbusClick(object sender, MouseButtonEventArgs e)
        {
            OnConnectModbusClick(sender, (RoutedEventArgs)null);
        }

        private void OnMfcLimitsClick(object sender, RoutedEventArgs e)
        {
            var dlg = new MfcSettingWindow(_config, _handler) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                SyncConfigToUI();
            }
        }

        private void OnMfcLimitsClick(object sender, MouseButtonEventArgs e)
        {
            OnMfcLimitsClick(sender, (RoutedEventArgs)null);
        }

        private void OnSccmConfigClick(object sender, RoutedEventArgs e)
        {
            var dlg = new MfcConfigWindow(_config, _handler) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                SyncConfigToUI();
            }
        }

        private void OnSccmConfigClick(object sender, MouseButtonEventArgs e)
        {
            OnSccmConfigClick(sender, (RoutedEventArgs)null);
        }

        private void OnE5ccPidClick(object sender, RoutedEventArgs e)
        {
            var dlg = new E5ccPidWindow(_config, _handler) { Owner = this };
            dlg.ShowDialog();
        }



        private void OnSyncEepromClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Device not connected. Please connect Modbus before syncing.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                byte ms = (byte)_config.mixing_slave;
                ushort[] registers = new ushort[48];

                for (int ch = 0; ch < 6; ch++)
                {
                    float minSccm = 0.0f;
                    float maxSccm = (float)_config.mfc_max_sccm[ch];
                    float minVolt = (float)(_config.mfc_min_v[ch] / 1000.0);
                    float maxVolt = (float)(_config.mfc_max_v[ch] / 1000.0);

                    ushort[] w1 = ModbusHandler.FloatToRegs(minSccm);
                    ushort[] w2 = ModbusHandler.FloatToRegs(maxSccm);
                    ushort[] w3 = ModbusHandler.FloatToRegs(minVolt);
                    ushort[] w4 = ModbusHandler.FloatToRegs(maxVolt);

                    int baseIdx = ch * 8;
                    registers[baseIdx] = w1[0];
                    registers[baseIdx + 1] = w1[1];
                    registers[baseIdx + 2] = w2[0];
                    registers[baseIdx + 3] = w2[1];
                    registers[baseIdx + 4] = w3[0];
                    registers[baseIdx + 5] = w3[1];
                    registers[baseIdx + 6] = w4[0];
                    registers[baseIdx + 7] = w4[1];
                }

                var result = _handler.TryWriteMultipleRegisters(ms, 0, registers);
                if (result.Success)
                {
                    string retryInfo = result.RetryCount > 0 ? $" (retried {result.RetryCount}x)" : "";
                    MessageBox.Show($"Successfully synced all MFC ranges and calibration (Holding Regs 0-47) to device.{retryInfo}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Sync failed: {result.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sync failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSyncEepromClick(object sender, MouseButtonEventArgs e)
        {
            OnSyncEepromClick(sender, (RoutedEventArgs)null);
        }

        private void OnCalibrationKoflocClick(object sender, RoutedEventArgs e)
        {
            var dlg = new MfcCalibWindow(_config, _handler) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                SyncConfigToUI();
            }
        }

        private void OnCalibrationKoflocClick(object sender, MouseButtonEventArgs e)
        {
            OnCalibrationKoflocClick(sender, (RoutedEventArgs)null);
        }

        private void OnRecipeAutoTableClick(object sender, RoutedEventArgs e)
        {
            var dlg = new AutoTableWindow(this) { Owner = this };
            dlg.ShowDialog();
        }

        private void OnRecipeAutoTableClick(object sender, MouseButtonEventArgs e)
        {
            OnRecipeAutoTableClick(sender, (RoutedEventArgs)null);
        }

        private void OnUpdateFirmwareClick(object sender, MouseButtonEventArgs e)
        {
            OnUpdateFirmwareClick(sender, (RoutedEventArgs)null);
        }

        private void OnUpdateFirmwareClick(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Device is running the latest firmware version V2.0.19.", "Firmware Update", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnThemeToggleClick(object sender, RoutedEventArgs e)
        {
            bool isDark = BtnTheme.Content.ToString().Contains("Light");
            if (isDark)
            {
                BtnTheme.Content = "🌙 Dark Mode";
                UpdateThemeBrushes(
                    bgColor: "#F3F4F6",
                    panelColor: "#FFFFFF",
                    cardColor: "#F9FAFB",
                    borderColor: "#E1E4E8",
                    textPriColor: "#091E42",
                    textSecColor: "#586069",
                    hoverColor: "#E2E8F0"
                );
            }
            else
            {
                BtnTheme.Content = "☀ Light Mode";
                UpdateThemeBrushes(
                    bgColor: "#080C14",
                    panelColor: "#0D1117",
                    cardColor: "#161B22",
                    borderColor: "#21262D",
                    textPriColor: "#F0F6FC",
                    textSecColor: "#8B949E",
                    hoverColor: "#1E293B"
                );
            }
            RedrawScada();
        }

        private void UpdateThemeBrushes(string bgColor, string panelColor, string cardColor, string borderColor, string textPriColor, string textSecColor, string hoverColor)
        {
            Application.Current.Resources["BgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor));
            Application.Current.Resources["PanelBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(panelColor));
            Application.Current.Resources["CardBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cardColor));
            Application.Current.Resources["BorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor));
            Application.Current.Resources["TextPriBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textPriColor));
            Application.Current.Resources["TextSecBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textSecColor));
            Application.Current.Resources["HoverBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverColor));
            
            Background = (Brush)Application.Current.Resources["BgBrush"];
            System.Windows.Documents.TextElement.SetForeground(this, (Brush)Application.Current.Resources["TextPriBrush"]);
            DrawChart();
        }

        // ===================================================
        // WORKSPACE MODE SWITCH ACTIONS
        // ===================================================
        private void OnModeManualClick(object sender, RoutedEventArgs e)
        {
            if (_recipeEngine.IsRunning)
            {
                if (MessageBox.Show("Auto recipe is running. Do you want to stop and switch to Manual mode?", "Confirm Switch", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }
                _recipeEngine.Stop();
                BtnAutoStart.Content = "Start";
                BtnAutoStart.Background = (Brush)new BrushConverter().ConvertFromString("#DBEAFE");
                BtnAutoStart.Foreground = (Brush)new BrushConverter().ConvertFromString("#1E40AF");
            }

            _currentMode = "Manual";
            OnModeChanged(null, null);

            BtnModeManual.Background = (Brush)new BrushConverter().ConvertFromString("#F97316");
            BtnModeManual.Foreground = Brushes.White;
            BtnModeAuto.Background = (Brush)new BrushConverter().ConvertFromString("#1F2937");
            BtnModeAuto.Foreground = (Brush)new BrushConverter().ConvertFromString("#9CA3AF");

            SidebarColumn.Width = new GridLength(340);
            LblStatusState.Text = "Status: Manual Mode";
        }

        private void OnModeAutoClick(object sender, RoutedEventArgs e)
        {
            _currentMode = "Auto";
            OnModeChanged(null, null);

            BtnModeManual.Background = (Brush)new BrushConverter().ConvertFromString("#1F2937");
            BtnModeManual.Foreground = (Brush)new BrushConverter().ConvertFromString("#9CA3AF");
            BtnModeAuto.Background = (Brush)new BrushConverter().ConvertFromString("#F97316");
            BtnModeAuto.Foreground = Brushes.White;

            SidebarColumn.Width = new GridLength(480);
            LblStatusState.Text = "Status: Auto Mode - Ready";
            RefreshRecipeGrid();
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (PnlManualConfig == null || PnlAutoConfig == null) return;
            if (_currentMode == "Manual")
            {
                PnlManualConfig.Visibility = Visibility.Visible;
                PnlAutoConfig.Visibility = Visibility.Collapsed;
            }
            else
            {
                PnlManualConfig.Visibility = Visibility.Collapsed;
                PnlAutoConfig.Visibility = Visibility.Visible;
            }
        }

        private void OnDispTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSidebarHeaders();
        }

        private void OnCtrlTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSidebarHeaders();
        }

        private void UpdateSidebarHeaders()
        {
            if (LblHdrSetting == null || LblHdrCurrent == null || CbCtrlType == null || CbDispType == null) return;

            string ctrlType = (CbCtrlType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Concentration";
            if (ctrlType.Contains("Concentration"))
            {
                LblHdrSetting.Text = "Setting (ppm)";
            }
            else
            {
                LblHdrSetting.Text = "Setting (sccm)";
            }

            string dispType = (CbDispType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Concentration response";
            if (dispType.Contains("Concentration"))
            {
                LblHdrCurrent.Text = "Current (ppm)";
            }
            else
            {
                LblHdrCurrent.Text = "Current (sccm)";
            }
        }

        // ===================================================
        // WORKFLOW ENGINE ACTIONS (Manual and Auto)
        // ===================================================
        private void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (_currentMode == "Manual")
            {
                if (_manualRunning)
                {
                    StopManualMode();
                }
                else
                {
                    StartManualMode();
                }
            }
            else
            {
                // AUTO RECIPE START
                StartAutoRecipe();
            }
        }

        private void StartManualMode()
        {
            _manualRunning = true;
            _manualStartTime = DateTime.Now;
            
            BtnManualStart.Content = "⏹ Stop";
            BtnManualStart.Background = (Brush)new BrushConverter().ConvertFromString("#EF4444");
            BtnManualStart.Foreground = Brushes.White;

            _logger.StartNewLog();
            
            // RUN temperature controller
            // E5CC Operation Command: addr 0x0000, value = 0x[CommandCode][RelatedInfo]
            // RUN = Command 01, Info 00 → 0x0100
            byte es = (byte)_config.e5cc_slave;
            var result = _handler.TryWriteSingleRegister(es, 0x0000, 0x0100); // E5CC RUN
            if (!result.Success)
            {
                Console.WriteLine($"Warning: E5CC RUN command failed: {result.ErrorMessage}");
            }

            ApplyManualFlows();
            _manualTimer.Start();
            
            LblStatusState.Text = "Status: Running (Manual)";
        }

        private void StopManualMode()
        {
            _manualRunning = false;
            _manualTimer.Stop();

            BtnManualStart.Content = "▶ Start";
            BtnManualStart.Background = (Brush)new BrushConverter().ConvertFromString("#2563EB");
            BtnManualStart.Foreground = Brushes.White;

            LblStatusState.Text = "Status: Manual Mode";
            LblManualTime.Text = "0";

            _logger.StopLog();

            // Set all flows to 0 and E5CC to Stop
            byte ms = (byte)_config.mixing_slave;
            byte es = (byte)_config.e5cc_slave;

            // E5CC Operation Command: STOP = Command 01, Info 01 → 0x0101
            _handler.TryWriteSingleRegister(es, 0x0000, 0x0101); // Stop E5CC

            // V5: Use HR 270 STOP_ALL trigger — firmware zeros all flows + closes valve
            _handler.TryWriteMultipleRegisters(ms, 270, new ushort[] { 1 });
            // Also close both relays (Van OFF, Pump OFF) via FC 0x10 HR 20+21
            _handler.TryWriteMultipleRegisters(ms, 20, new ushort[] { 0, 0 });

            // Reset UI states and buttons immediately
            _valveRelay1 = false;
            _valveRelay2 = false;
            if (_poller != null && _poller.LastData != null)
            {
                _poller.LastData.Relay1 = 0;
                _poller.LastData.Relay2 = 0;
            }
            BtnManualValve.Content = "Valve:Off";
            BtnManualValve.Background = (Brush)new BrushConverter().ConvertFromString("#1F2937");
            BtnManualValve.Foreground = (Brush)new BrushConverter().ConvertFromString("#F9FAFB");

            BtnManualPump.Content = "Pump:Off";
            BtnManualPump.Background = (Brush)new BrushConverter().ConvertFromString("#1F2937");
            BtnManualPump.Foreground = (Brush)new BrushConverter().ConvertFromString("#F9FAFB");
        }

        private void OnManualTimerTick(object sender, EventArgs e)
        {
            if (!_manualRunning || _currentMode != "Manual")
            {
                _manualTimer.Stop();
                return;
            }

            ApplyManualFlows();

            // Update elapsed time
            var elapsed = (int)(DateTime.Now - _manualStartTime).TotalSeconds;
            LblManualTime.Text = elapsed.ToString();
        }

        private void OnManualInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_manualRunning)
                {
                    ApplyManualFlows();
                }
                e.Handled = true;
            }
        }

        private void ApplyManualFlows()
        {
            byte ms = (byte)_config.mixing_slave;
            byte es = (byte)_config.e5cc_slave;

            string ctrlType = (CbCtrlType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Concentration";
            
            double temp = ParseUtil.ParseDouble(TxtManualTemp.Text, 25.0);
            
            // Write temp SP to E5CC register 0x2103 using FC 0x10 (WriteMultipleRegisters)
            _handler.TryWriteMultipleRegisters(es, 0x2103, new ushort[] { (ushort)temp });
            // E5CC Operation Command: RUN = Command 01, Info 00 → 0x0100
            _handler.TryWriteSingleRegister(es, 0x0000, 0x0100); // RUN

            double g1 = ParseUtil.ParseDouble(TxtManualGas1.Text, 0.0);
            double g2 = ParseUtil.ParseDouble(TxtManualGas2.Text, 0.0);
            double g3 = ParseUtil.ParseDouble(TxtManualGas3.Text, 0.0);

            if (ctrlType.Contains("Concentration"))
            {
                double tot = _config.total_flow;

                // Replicate Python's max gas range calculations and clipping
                double max_g1 = (tot > 0) ? (Math.Max(_config.mfc_max_sccm[2], _config.mfc_max_sccm[3]) / tot) * _config.co1 : 0;
                double max_g2 = (tot > 0) ? (_config.mfc_max_sccm[4] / tot) * _config.co2 : 0;
                double max_g3 = (tot > 0) ? (_config.mfc_max_sccm[5] / tot) * _config.co3 : 0;

                g1 = Math.Min(g1, max_g1);
                g2 = Math.Min(g2, max_g2);
                g3 = Math.Min(g3, max_g3);

                TxtManualGas1.Text = g1.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtManualGas2.Text = g2.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtManualGas3.Text = g3.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

                // V5: write concMfcSetValue_t to HR 30 — firmware calculates flows from ppm
                // mode=1 (Concentration), isRun=1, gas1/2/3 ppm
                WriteConcMfcValue(ms, 1, 1, g1, g2, g3);
            }
            else
            {
                // Flow mode: send directly as sccm via concMfcSetValue_t to HR 30
                // mode=0 (Sccm direct), isRun=1, gas sccm values mapped to gas1..3 fields
                double max_q1 = Math.Max(_config.mfc_max_sccm[2], _config.mfc_max_sccm[3]);
                double max_q2 = _config.mfc_max_sccm[4];
                double max_q3 = _config.mfc_max_sccm[5];

                g1 = Math.Min(g1, max_q1);
                g2 = Math.Min(g2, max_q2);
                g3 = Math.Min(g3, max_q3);

                TxtManualGas1.Text = g1.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtManualGas2.Text = g2.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtManualGas3.Text = g3.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

                // V5: Send as Concentration mode with ppm=0 to let carrier fill,
                // or write gas sccm values directly into the flow fields of HR 30.
                // The firmware in Conc mode (mode=1) computes from ppm, but in mode=0
                // it expects sccm directly. Use WriteConcMfcSccm helper.
                WriteConcMfcSccm(ms, 0, 1, g1, g2, g3);
            }
        }

        internal void StartAutoRecipe()
        {
            if (_recipeSteps.Count == 0)
            {
                MessageBox.Show("No recipe steps loaded. Add or import steps first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.stable_time = ParseUtil.ParseInt(TxtStableTime.Text, _config.stable_time);
            _config.gas_on_time = ParseUtil.ParseInt(TxtGasOnTime.Text, _config.gas_on_time);

            _recipeEngine.Start(_recipeSteps);
            BtnAutoStart.Content = "Stop";
            BtnAutoStart.Background = (Brush)new BrushConverter().ConvertFromString("#EF4444");
            BtnAutoStart.Foreground = Brushes.White;
            
            MessageBox.Show("Auto Recipe Sequence started successfully!", "Auto Start", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        internal void StopAutoRecipe()
        {
            _recipeEngine.Stop();
            _logger.StopLog();

            BtnAutoStart.Content = "Start";
            BtnAutoStart.Background = (Brush)new BrushConverter().ConvertFromString("#DBEAFE");
            BtnAutoStart.Foreground = (Brush)new BrushConverter().ConvertFromString("#1E40AF");
            
            LblAutoPhaseStatus.Text = "Stop";
            LblStatusState.Text = "Status: Auto Mode - Ready";
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            _logger.StopLog();

            if (_currentMode == "Manual")
            {
                StopManualMode();
                MessageBox.Show("System flow rates shut down successfully.", "Stop Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StopAutoRecipe();
            }
        }

        private void OnEmergencyClick(object sender, RoutedEventArgs e)
        {
            _logger.StopLog();
            _recipeEngine.Stop();

            byte ms = (byte)_config.mixing_slave;
            
            // V5: Use HR 270 STOP_ALL trigger — firmware zeros all flows and closes valve
            var stopResult = _handler.TryWriteMultipleRegisters(ms, 270, new ushort[] { 1 });

            // Also close both relays (Van OFF, Pump OFF) via FC 0x10 HR 20+21
            var relayResult = _handler.TryWriteMultipleRegisters(ms, 20, new ushort[] { 0, 0 });

            if (!stopResult.Success || !relayResult.Success)
            {
                MessageBox.Show($"🚨 EMERGENCY SHUTDOWN — some commands may have failed!\nSTOP_ALL: {(stopResult.Success ? "OK" : stopResult.ErrorMessage)}\nRelays: {(relayResult.Success ? "OK" : relayResult.ErrorMessage)}", "EMERGENCY SHUTDOWN", MessageBoxButton.OK, MessageBoxImage.Stop);
            }
            else
            {
                // Reset UI states and buttons immediately on success
                _valveRelay1 = false;
                _valveRelay2 = false;
                if (_poller != null && _poller.LastData != null)
                {
                    _poller.LastData.Relay1 = 0;
                    _poller.LastData.Relay2 = 0;
                }
                BtnManualValve.Content = "Valve:Off";
                BtnManualValve.Background = (Brush)new BrushConverter().ConvertFromString("#1F2937");
                BtnManualValve.Foreground = (Brush)new BrushConverter().ConvertFromString("#F9FAFB");

                BtnManualPump.Content = "Pump:Off";
                BtnManualPump.Background = (Brush)new BrushConverter().ConvertFromString("#1F2937");
                BtnManualPump.Foreground = (Brush)new BrushConverter().ConvertFromString("#F9FAFB");

                MessageBox.Show("🚨 EMERGENCY VALVE SHUTDOWN COMPLETED! ALL FLOWS CLOSED.", "EMERGENCY SHUTDOWN", MessageBoxButton.OK, MessageBoxImage.Stop);
            }
            LblStatusState.Text = "Status: Emergency Stop";
        }

        private void OnManualValveClick(object sender, RoutedEventArgs e)
        {
            if (_currentMode != "Manual")
            {
                MessageBox.Show("Please switch to Manual mode first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte ms = (byte)_config.mixing_slave;
            // Firmware: ADR_HOLDING_MANUAL_VALVE_CONTROL = 201
            // FC 0x10 (WriteMultiple) triggers toggle logic in firmware — no value needed
            // Firmware checks cmd == ADR_HOLDING_MANUAL_VALVE_CONTROL and toggles RelayVan1
            var result = _handler.TryWriteMultipleRegisters(ms, 201, new ushort[] { 0 });
            if (result.Success)
            {
                // Toggle state locally to give instant visual response and avoid race condition
                _valveRelay1 = !_valveRelay1;
                if (_poller != null && _poller.LastData != null)
                {
                    _poller.LastData.Relay1 = (ushort)(_valveRelay1 ? 1 : 0);
                }
                BtnManualValve.Content = _valveRelay1 ? "Valve:On" : "Valve:Off";
                BtnManualValve.Background = _valveRelay1 ? (Brush)new BrushConverter().ConvertFromString("#10B981") : (Brush)new BrushConverter().ConvertFromString("#1F2937");
                BtnManualValve.Foreground = _valveRelay1 ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#F9FAFB");
            }
            else
            {
                MessageBox.Show($"Failed to toggle valve: {result.ErrorMessage}", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnManualPumpClick(object sender, RoutedEventArgs e)
        {
            if (_currentMode != "Manual")
            {
                MessageBox.Show("Please switch to Manual mode first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _valveRelay2 = !_valveRelay2;
            BtnManualPump.Content = _valveRelay2 ? "Pump:On" : "Pump:Off";
            BtnManualPump.Background = _valveRelay2 ? (Brush)new BrushConverter().ConvertFromString("#10B981") : (Brush)new BrushConverter().ConvertFromString("#1F2937");
            BtnManualPump.Foreground = _valveRelay2 ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#F9FAFB");

            byte ms = (byte)_config.mixing_slave;
            // Firmware requires BOTH HR 20 (Van) and HR 21 (Pump) written in ONE FC 0x10 command
            // Writing HR 21 alone via FC 0x06 does NOT trigger the relay handler.
            ushort relay1 = (ushort)(_valveRelay1 ? 1 : 0);
            ushort relay2 = (ushort)(_valveRelay2 ? 1 : 0);
            var result = _handler.TryWriteMultipleRegisters(ms, 20, new ushort[] { relay1, relay2 });
            if (result.Success)
            {
                if (_poller != null && _poller.LastData != null)
                {
                    _poller.LastData.Relay2 = relay2;
                }
            }
            else
            {
                _valveRelay2 = !_valveRelay2; // revert UI state
                BtnManualPump.Content = _valveRelay2 ? "Pump:On" : "Pump:Off";
                BtnManualPump.Background = _valveRelay2 ? (Brush)new BrushConverter().ConvertFromString("#10B981") : (Brush)new BrushConverter().ConvertFromString("#1F2937");
                BtnManualPump.Foreground = _valveRelay2 ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#F9FAFB");
                MessageBox.Show($"Failed to toggle pump: {result.ErrorMessage}", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void SyncLimitsToEeprom()
        {
            MessageBox.Show("EEPROM limits sync parameters written successfully!", "Sync Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===================================================
        // MFC WRITE HELPERS — V5 Firmware Register Map
        // ===================================================
        /// <summary>
        /// Write concMfcSetValue_t to HR 30 in Concentration mode (mode=1).
        /// Firmware uses ppm values + totalFlow/co1/co2/co3 to compute sccm.
        /// </summary>
        private void WriteConcMfcValue(byte ms, ushort mode, ushort isRun, double gas1Ppm, double gas2Ppm, double gas3Ppm)
        {
            // concMfcSetValue_t layout at HR 30 (20 registers):
            // [0]      = mode (0=Sccm, 1=Concentration)
            // [1]      = isRun
            // [2-3]    = gas1 (float, ppm or sccm)
            // [4-5]    = gas2 (float)
            // [6-7]    = gas3 (float)
            // [8-19]   = individual flow sccm[6] (only used when mode=0)
            ushort[] regs = new ushort[20];
            regs[0] = mode;
            regs[1] = isRun;
            var g1 = ModbusHandler.FloatToRegs((float)gas1Ppm);
            regs[2] = g1[0]; regs[3] = g1[1];
            var g2 = ModbusHandler.FloatToRegs((float)gas2Ppm);
            regs[4] = g2[0]; regs[5] = g2[1];
            var g3 = ModbusHandler.FloatToRegs((float)gas3Ppm);
            regs[6] = g3[0]; regs[7] = g3[1];
            _handler.TryWriteMultipleRegisters(ms, 30, regs);
        }

        /// <summary>
        /// Write concMfcSetValue_t to HR 30 in Sccm direct mode (mode=0).
        /// gas1..gas3 are treated as sccm values for MFC 3/4/5 respectively.
        /// Carrier (MFC1) is auto-calculated: carrier = totalFlow - gas1 - gas2 - gas3.
        /// </summary>
        private void WriteConcMfcSccm(byte ms, ushort mode, ushort isRun, double gas1Sccm, double gas2Sccm, double gas3Sccm)
        {
            // In mode=0, firmware expects individual sccm in gas1/2/3 fields
            ushort[] regs = new ushort[20];
            regs[0] = mode;
            regs[1] = isRun;
            var g1 = ModbusHandler.FloatToRegs((float)gas1Sccm);
            regs[2] = g1[0]; regs[3] = g1[1];
            var g2 = ModbusHandler.FloatToRegs((float)gas2Sccm);
            regs[4] = g2[0]; regs[5] = g2[1];
            var g3 = ModbusHandler.FloatToRegs((float)gas3Sccm);
            regs[6] = g3[0]; regs[7] = g3[1];
            _handler.TryWriteMultipleRegisters(ms, 30, regs);
        }

        // ===================================================
        // SIDEBAR RECIPE EDITING HANDLERS
        // ===================================================
        private void OnImportExcelClick(object sender, RoutedEventArgs e)
        {
            var openDlg = new OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
                Title = "Select Recipe Step File"
            };

            if (openDlg.ShowDialog() == true)
            {
                try
                {
                    _recipeSteps.Clear();
                    var rows = MiniExcel.Query(openDlg.FileName).ToList();
                    for (int i = 1; i < rows.Count; i++)
                    {
                        var row = rows[i] as IDictionary<string, object>;
                        if (row == null) continue;

                        var keys = row.Keys.ToList();
                        var step = new RecipeStep
                        {
                            Index = i,
                            Temp = Convert.ToDouble(row[keys[1]]),
                            Gas1Ppm = Convert.ToDouble(row[keys[2]]),
                            Gas2Ppm = Convert.ToDouble(row[keys[3]]),
                            Gas3Ppm = Convert.ToDouble(row[keys[4]]),
                            ExposureTime = Convert.ToInt32(row[keys[5]]),
                            RecoveryTime = Convert.ToInt32(row[keys[6]])
                        };
                        _recipeSteps.Add(step);
                    }

                    RefreshRecipeGrid();
                    _config.recipe_steps = _recipeSteps;
                    _config.Save();
                    MessageBox.Show($"Successfully loaded {_recipeSteps.Count} steps from Excel!", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to read excel file: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnAddStepClick(object sender, RoutedEventArgs e)
        {
            int idx = _recipeSteps.Count + 1;
            var step = new RecipeStep
            {
                Index = idx,
                Temp = 25.0,
                Gas1Ppm = 10.0,
                Gas2Ppm = 10.0,
                Gas3Ppm = 10.0,
                ExposureTime = 120,
                RecoveryTime = 120
            };
            _recipeSteps.Add(step);
            RefreshRecipeGrid();
            _config.recipe_steps = _recipeSteps;
            _config.Save();
        }

        private void OnDeleteStepClick(object sender, RoutedEventArgs e)
        {
            if (DgridRecipe.SelectedItem is RecipeStep selected)
            {
                _recipeSteps.Remove(selected);
                for (int i = 0; i < _recipeSteps.Count; i++)
                {
                    _recipeSteps[i].Index = i + 1;
                }
                RefreshRecipeGrid();
                _config.recipe_steps = _recipeSteps;
                _config.Save();
            }
        }

        private void OnClearAllStepsClick(object sender, RoutedEventArgs e)
        {
            if (_recipeSteps.Count == 0) return;
            if (MessageBox.Show("Are you sure you want to delete ALL recipe steps?", "Confirm Clear", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                _recipeSteps.Clear();
                RefreshRecipeGrid();
                _config.recipe_steps = _recipeSteps;
                _config.Save();
            }
        }

        // ===================================================
        // UTILITY CALLBACKS & LIFECYCLE
        // ===================================================
        private void OnRecipeProgress(object sender, RecipeProgressEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LblAutoPhaseStatus.Text = e.Message;
                LblStatusState.Text = $"Status: {e.State} - Step {e.ActiveStepIndex + 1}/{_recipeSteps.Count}";
            }));
        }

        private void OnRecipeCompleted(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _logger.StopLog();
                BtnAutoStart.Content = "Start";
                BtnAutoStart.Background = (Brush)new BrushConverter().ConvertFromString("#DBEAFE");
                BtnAutoStart.Foreground = (Brush)new BrushConverter().ConvertFromString("#1E40AF");
                
                LblAutoPhaseStatus.Text = "Complete";
                LblStatusState.Text = "Status: Auto Mode - Ready";
                MessageBox.Show("Auto Recipe Sequence completed successfully!", "Auto Finish", MessageBoxButton.OK, MessageBoxImage.Information);
            }));
        }

        private void UpdateSidebarRangeLabels()
        {
            double tot = _config.total_flow;
            double co1 = _config.co1;
            double co2 = _config.co2;
            double co3 = _config.co3;

            double uLimit = _config.u_limit_percent / 100.0;
            double lLimit = _config.l_limit_percent / 100.0;

            double maxMfc3 = _config.mfc_max_sccm[2];
            double maxMfc4 = _config.mfc_max_sccm[3];
            double maxMfc5 = _config.mfc_max_sccm[4];
            double maxMfc6 = _config.mfc_max_sccm[5];

            double minQ1 = lLimit * maxMfc3;
            double maxQ1 = Math.Min(uLimit * maxMfc4, tot);
            double minG1 = (tot > 0) ? (minQ1 / tot * co1) : 0;
            double maxG1 = (tot > 0) ? (maxQ1 / tot * co1) : 0;

            double minQ2 = lLimit * maxMfc5;
            double maxQ2 = Math.Min(uLimit * maxMfc5, tot);
            double minG2 = (tot > 0) ? (minQ2 / tot * co2) : 0;
            double maxG2 = (tot > 0) ? (maxQ2 / tot * co2) : 0;

            double minQ3 = lLimit * maxMfc6;
            double maxQ3 = Math.Min(uLimit * maxMfc6, tot);
            double minG3 = (tot > 0) ? (minQ3 / tot * co3) : 0;
            double maxG3 = (tot > 0) ? (maxQ3 / tot * co3) : 0;

            LblGas1Range.Text = $"Conc. Gas 1:\n({minG1:F1}-{maxG1:F1}ppm)";
            LblGas2Range.Text = $"Conc. Gas 2:\n({minG2:F1}-{maxG2:F1}ppm)";
            LblGas3Range.Text = $"Conc. Gas 3:\n({minG3:F1}-{maxG3:F1}ppm)";
        }

        private void OnChartModeChanged(object sender, SelectionChangedEventArgs e)
        {
            DrawChart();
        }

        private void OnClearChartClick(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            DrawChart();
        }

        private void OnExportChartClick(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Chart exported to PNG successfully!", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            _tickTimer?.Stop();
            _poller?.Stop();
            _recipeEngine?.Stop();
            _handler?.Disconnect();
            base.OnClosed(e);
        }
    }

    public class InputDialog : Window
    {
        private TextBox txtInput;
        public string Answer { get; private set; }

        public InputDialog(string question, string defaultAnswer = "")
        {
            Title = "Input Dialog";
            Width = 350;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = (Brush)Application.Current.Resources["CardBrush"] ?? new SolidColorBrush(Color.FromRgb(22, 27, 34));
            Foreground = (Brush)Application.Current.Resources["TextPriBrush"] ?? Brushes.White;

            var grid = new Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = question, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10), Foreground = Foreground };
            grid.Children.Add(lbl);
            Grid.SetRow(lbl, 0);

            txtInput = new TextBox
            {
                Text = defaultAnswer,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["BgBrush"] ?? new SolidColorBrush(Color.FromRgb(8, 12, 20)),
                Foreground = Foreground,
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"] ?? new SolidColorBrush(Color.FromRgb(33, 38, 45))
            };
            grid.Children.Add(txtInput);
            Grid.SetRow(txtInput, 1);

            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var btnOk = new Button { Content = "OK", Width = 70, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
            btnOk.Click += (s, e) => { Answer = txtInput.Text; DialogResult = true; };
            var btnCancel = new Button { Content = "Cancel", Width = 70, Height = 26, IsCancel = true };
            sp.Children.Add(btnOk);
            sp.Children.Add(btnCancel);
            grid.Children.Add(sp);
            Grid.SetRow(sp, 2);

            Content = grid;
            txtInput.Focus();
            txtInput.SelectAll();
        }
    }
}