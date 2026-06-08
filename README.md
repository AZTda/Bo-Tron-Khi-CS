# Gas Mixing SCADA System / Hệ Thống Điều Khiển Trộn Khí SCADA V5

[English](#english) | [Tiếng Việt](#tiếng-việt)

---

## Tiếng Việt

Hệ thống SCADA Điều khiển Trộn Khí V5 là giải pháp phần mềm chuyên nghiệp viết bằng C# (WPF) dùng để giám sát, điều khiển lưu lượng khí đa kênh (MFC) và nhiệt độ buồng đốt thời gian thực qua giao thức công nghiệp Modbus RTU/TCP.

### 🌟 Tính Năng Nổi Bật

1. **Giao Diện SCADA Trực Quan Sinh Động**:
   - Đồ họa vector tự động co giãn (Scalable Vector Graphics), hiển thị luồng hạt khí động (animated particles) di chuyển dọc theo đường ống dựa trên thực tế đóng/mở của các van và lưu lượng khí.
   - Hiệu ứng lò sưởi trực quan thay đổi màu sắc cuộn nhiệt độ (coils) và sóng nhiệt (heatwaves) dựa trên giá trị nhiệt độ PV thực tế.
2. **Chế Độ Điều Khiển Linh Hoạt**:
   - **Chế độ Thủ công (Manual)**: Cho phép bật/tắt bơm, van (Valve/Pump) tức thời, nhập giá trị nhiệt độ và nồng độ khí (ppm/sccm) trực tiếp từ bàn phím (nhấn Enter để truyền lệnh xuống phần cứng).
   - **Chế độ Tự động (Auto Recipe)**: Thực hiện chuỗi quy trình nhiều bước cấu hình trước từ bảng Excel. 
3. **Quy Trình Tiền Trộn Khí Tối Ưu (Merged Pre-mixing)**:
   - Tự động gộp thời gian tiền trộn khí (`gas_on_time`) trực tiếp vào thời gian ổn định nhiệt (`stable_time`) ở bước đầu tiên hoặc khi thay đổi nhiệt độ.
   - Tự động thực hiện tiền trộn khí cho bước tiếp theo trong thời gian hồi phục (`RecoveryTime`) của bước trước đó nếu chạy cùng nhiệt độ, giúp tối ưu hóa thời gian vận hành và tăng độ chính xác của thí nghiệm.
4. **Bộ Đọc/Ghi Modbus An Toàn & Đồng Bộ**:
   - Ghi gộp thanh ghi tối ưu (FC 0x10) để điều khiển đồng thời van và bơm, tránh trễ lệnh hoặc xung đột phần cứng.
   - Công cụ polling thích ứng (Adaptive Polling Engine): Tự động giảm tần suất đọc/ghi khi kết nối chập chờn để tránh treo phần mềm.
5. **Ghi Nhật Ký Dữ Liệu tự động (Data Logging)**:
   - Tự động xuất tệp tin nhật ký định dạng `.csv` lưu trữ nhiệt độ thực tế, lưu lượng thực tế của cả 6 kênh MFC mỗi giây.

### 🛠 Kiến Trúc Phần Mềm

- **`MainWindow.xaml` & `MainWindow.xaml.cs`**: Quản lý giao diện, bộ vẽ đồ họa SCADA động, đồ thị xu hướng (real-time trend chart) và tiếp nhận tương tác người dùng.
- **[RecipeEngine.cs](file:///c:/Users/dietr188/Desktop/Code%20bo%20tron/V5/Bo-Tron-Khi-CS/RecipeEngine.cs)**: Trình điều khiển chuỗi tự động (Auto Mode State Machine).
- **[PollingEngine.cs](file:///c:/Users/dietr188/Desktop/Code%20bo%20tron/V5/Bo-Tron-Khi-CS/PollingEngine.cs)**: Tiến trình chạy nền thu thập trạng thái cảm biến, MFC, và nhiệt độ liên tục mà không gây đơ giao diện (Thread-safe).
- **[ModbusHandler.cs](file:///c:/Users/dietr188/Desktop/Code%20bo%20tron/V5/Bo-Tron-Khi-CS/ModbusHandler.cs)**: Lớp kết nối trung gian thực hiện các lệnh đọc/ghi Modbus RTU/TCP nguyên tử (atomic transactions).
- **[SystemConfig.cs](file:///c:/Users/dietr188/Desktop/Code%20bo%20tron/V5/Bo-Tron-Khi-CS/SystemConfig.cs)**: Quản lý cấu hình hệ thống (cổng COM, tốc độ Baud, giới hạn MFC, cấu hình nồng độ khí) dưới dạng file JSON (`gas_mixer_config.json`).

### 🔌 Bản Đồ Thanh Ghi Modbus (Modbus Register Map)

#### 1. Board Điều Khiển Trộn Khí (Slave Address mặc định: 3)
- **Holding Register 20**: Điều khiển trạng thái Van 1 (0: Đóng, 1: Mở).
- **Holding Register 21**: Điều khiển trạng thái Bơm hút chân không (0: Tắt, 1: Bật).
- **Holding Register 30 - 49 (Batch 20 regs)**: Cấu hình chế độ MFC (`concMfcSetValue_t`):
  - Reg `30`: Mode (0: sccm trực tiếp, 1: tính theo nồng độ ppm).
  - Reg `31`: Trạng thái chạy (0: Dừng, 1: Chạy).
  - Reg `32-33` (Float): Nồng độ Gas 1 SP (ppm hoặc sccm).
  - Reg `34-35` (Float): Nồng độ Gas 2 SP (ppm hoặc sccm).
  - Reg `36-37` (Float): Nồng độ Gas 3 SP (ppm hoặc sccm).
- **Holding Register 201**: Lệnh đảo trạng thái van (Toggle Valve).
- **Holding Register 202**: Setpoint Nhiệt độ chuyển tiếp tới bộ điều khiển nhiệt.
- **Holding Register 270**: Lệnh Dừng Khẩn Cấp (Emergency Stop - tắt toàn bộ khí và van).
- **Input Register 218**: Trạng thái phản hồi thực tế của Van 1 (đọc từ cảm biến đầu ra).

#### 2. Bộ Điều Khiển Nhiệt Độ OMRON E5CC (Slave Address mặc định: 1)
- **Holding Register 0x2103 (8451)**: Đặt giá trị nhiệt độ đích (Setpoint Temperature).
- **Holding Register 0x2000 (8192)**: Đọc giá trị nhiệt độ thực tế (Process Value - PV).
- **Holding Register 0x0000 (0)**: Gửi lệnh chạy/dừng (0x0100: RUN, 0x0101: STOP).

---

## English

The Gas Mixing SCADA System V5 is a professional C# WPF-based desktop application designed for real-time monitoring, multi-channel Mass Flow Controller (MFC) gas mixing, and closed-loop furnace temperature profiling via Modbus RTU/TCP.

### 🌟 Key Features

1. **Vibrant & Interactive SCADA Interface**:
   - Vector graphics dashboard with animated particle flows representing active gas pathways.
   - Dynamic heater coils and rising heat waves that adapt colors based on active furnace temperature PV.
2. **Flexible Control Modes**:
   - **Manual Mode**: Direct control over valves, pumps, temperature setpoints, and gas flow rates (Submit commands safely using Enter).
   - **Auto Recipe Mode**: Imports complex thermal and gas profiles directly from Excel files.
3. **Advanced Gas Pre-mixing Logic**:
   - Merges the pre-mixing phase (`gas_on_time`) directly inside the heating/stabilization period (`stable_time`).
   - For consecutive steps running at the same temperature, the pre-mixing of the next step runs in the background of the current step's recovery period (`RecoveryTime`), eliminating additive process delays.
4. **Resilient Communications Engine**:
   - Atomic multi-register transactions prevent packet collision on the Modbus bus.
   - Adaptive polling back-off prevents software UI freezing under unstable serial connection.
5. **Data Logger**:
   - Exports high-fidelity log reports to `.csv` storing temperatures and flow rates of all 6 MFCs per second.

### 🚀 Get Started

#### Requirements
- .NET 8.0 SDK / .NET 10.0 SDK
- Windows OS (WPF Support)

#### Build & Run
1. Open terminal inside the `Bo-Tron-Khi-CS` directory.
2. Run the build command:
   ```bash
   dotnet build
   ```
3. Run the application:
   ```bash
   dotnet run
   ```
