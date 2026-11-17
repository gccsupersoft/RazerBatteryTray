// RazerBatteryTray.cs  —  雷蛇无线鼠标状态栏电量显示器 (专版)
// 支持三通道自动采集：
//   1. 蓝牙 (BLE) 原生电量属性读取 (适配 BSK V3 PRO 等蓝牙连接模式)
//   2. 雷云 (Razer Synapse 3 / 4) 后台日志无感解析 (零 USB 总线冲突)
//   3. 雷蛇 90-Byte HID Feature Report 防冲突直连查询 (2.4G 接收器 / 有线模式)
//
// 图标：使用雷蛇三头蛇 Logo，按电量百分比从下往上双色阶动态填充。
//
// 编译：
//   powershell -ExecutionPolicy Bypass -File build.ps1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RazerBatteryTray
{
    static class Native
    {
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_ALLCLASSES = 0x04;
        public const int DIGCF_DEVICEINTERFACE = 0x10;

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const int FILE_SHARE_READ = 1;
        public const int FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // Windows Device Property Types (DEVPROPTYPE)
        public const uint DEVPROP_TYPE_SBYTE = 0x00000002;
        public const uint DEVPROP_TYPE_BYTE = 0x00000003;
        public const uint DEVPROP_TYPE_INT16 = 0x00000004;
        public const uint DEVPROP_TYPE_UINT16 = 0x00000005;
        public const uint DEVPROP_TYPE_INT32 = 0x00000006;
        public const uint DEVPROP_TYPE_UINT32 = 0x00000007;
        public const uint DEVPROP_TYPE_GUID = 0x0000000D;
        public const uint DEVPROP_TYPE_BOOLEAN = 0x00000011;
        public const uint DEVPROP_TYPE_STRING = 0x00000012;

        [StructLayout(LayoutKind.Sequential)]
        public struct GUID
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            public byte b0, b1, b2, b3, b4, b5, b6, b7;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public GUID InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;

            public DEVPROPKEY(string guidString, uint propId)
            {
                fmtid = new Guid(guidString);
                pid = propId;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevsW(ref GUID ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref GUID interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDevicePropertyW(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            ref DEVPROPKEY PropertyKey,
            out uint PropertyType,
            IntPtr PropertyBuffer,
            uint PropertyBufferSize,
            out uint RequiredSize,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out GUID guid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr device, IntPtr buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, byte[] capabilities);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string fileName, uint desiredAccess, int shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    class HidInterface
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public string Product;
        public string Transport;
        public ushort UsagePage;
        public ushort Usage;
        public int InLen;
        public int OutLen;
        public int FeatLen;
    }

    class DeviceState
    {
        public string Key;
        public string DisplayName;
        public int Battery = -1;
        public int ChargeState = 1;    // 1 放电中 / 2 已充满 / 3 充电中
        public string Transport;       // 蓝牙 (BLE) / 2.4G 无线 / 有线 USB / 雷云同步
        public string SourceEngine;    // BLE PnP / Synapse Log / Razer HID
        public ushort Vid;
        public ushort Pid;
        public byte TxId;
        public DateTime LastSeen = DateTime.MinValue;
        public bool IsSleeping = false;
    }

    static class RazerDeviceCatalog
    {
        public const ushort RAZER_VID = 0x1532;

        // 常见雷蛇无线鼠标 PID -> 显示名称（优先把 Basilisk V3 Pro 映射为简洁的 BSK V3 PRO）
        static readonly Dictionary<ushort, string> PidNames = BuildPidNames();

        static Dictionary<ushort, string> BuildPidNames()
        {
            var d = new Dictionary<ushort, string>();
            // Basilisk 巴塞利斯蛇系列
            d[0x00AA] = "BSK V3 PRO";
            d[0x00AB] = "BSK V3 PRO";
            d[0x00B9] = "BSK V3 X HS";
            d[0x00BA] = "BSK V3 X HS";
            d[0x0086] = "Basilisk Ultimate";
            d[0x0088] = "Basilisk Ultimate";
            d[0x0083] = "Basilisk X HS";
            d[0x0099] = "Basilisk X HS";
            // Viper 毒蝰系列
            d[0x00C0] = "Viper V3 Pro";
            d[0x00C1] = "Viper V3 Pro";
            d[0x00B6] = "Viper V3 HS";
            d[0x00A5] = "Viper V2 Pro";
            d[0x00A6] = "Viper V2 Pro";
            d[0x007A] = "Viper Ultimate";
            d[0x007B] = "Viper Ultimate";
            // DeathAdder 炼狱蝰蛇系列
            d[0x00B2] = "DA V3 Pro";
            d[0x00B3] = "DA V3 Pro";
            d[0x007C] = "DA V2 Pro";
            d[0x007D] = "DA V2 Pro";
            d[0x0094] = "DA V2 X HS";
            // Cobra 眼镜蛇 / Naga 那伽梵蛇 / Orochi 八岐大蛇
            d[0x00AF] = "Cobra Pro";
            d[0x00B0] = "Cobra Pro";
            d[0x00A7] = "Naga V2 Pro";
            d[0x00A8] = "Naga V2 Pro";
            d[0x00B4] = "Naga V2 HS";
            d[0x0096] = "Naga Pro";
            d[0x0097] = "Naga Pro";
            d[0x0092] = "Orochi V2";
            // 通用高轮询率接收器 / 充电底座
            d[0x00A4] = "Razer HyperPolling";
            d[0x00A3] = "Razer Mouse Dock Pro";
            return d;
        }

        // Wired PID，用于判断当前是否插线充电
        static readonly HashSet<ushort> WiredPids = new HashSet<ushort>
        {
            0x00AA, 0x0086, 0x00C0, 0x00A5, 0x007A, 0x00B2, 0x007C, 0x00AF, 0x00A7, 0x0096
        };

        public static bool IsWiredPid(ushort pid)
        {
            return WiredPids.Contains(pid);
        }

        public static string ResolveFriendlyName(ushort pid, string rawProduct)
        {
            string mapped;
            if (PidNames.TryGetValue(pid, out mapped))
                return mapped;

            if (!string.IsNullOrEmpty(rawProduct))
            {
                string p = rawProduct.Trim();
                if (p.IndexOf("Basilisk V3 Pro", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "BSK V3 PRO";
                if (p.StartsWith("Razer ", StringComparison.OrdinalIgnoreCase) && p.Length > 6)
                    return p.Substring(6).Trim();
                return p;
            }

            return string.Format("Razer Mouse ({0:X4})", pid);
        }

        public static string NormalizeBluetoothName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "BSK V3 PRO";
            string n = name.Trim();
            if (n.IndexOf("Basilisk V3 Pro", StringComparison.OrdinalIgnoreCase) >= 0)
                return "BSK V3 PRO";
            return n;
        }

        public static bool IsRazerBluetoothIdentity(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string u = text.ToUpperInvariant();
            if (u.Contains("BSK V3") || u.Contains("BSK ") || u.Contains("BASILISK")) return true;
            if (u.Contains("RAZER") || u.Contains("VIPER") || u.Contains("DEATHADDER")) return true;
            if (u.Contains("DA V3") || u.Contains("DA V2") || u.Contains("COBRA PRO")) return true;
            if (u.Contains("NAGA") || u.Contains("OROCHI") || u.Contains("PRO CLICK")) return true;
            if (u.Contains("1532")) return true; // Razer VID in Bluetooth DevNode ID
            return false;
        }
    }

    // ------------------------------------------------------------------ 通道 A：蓝牙 BLE 电量读取
    static class BluetoothBatteryReader
    {
        static Native.DEVPROPKEY KeyBattery = new Native.DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static Native.DEVPROPKEY KeyFriendlyName = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static Native.DEVPROPKEY KeyDeviceDesc = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        static Native.DEVPROPKEY KeyInstanceId = new Native.DEVPROPKEY("78C34FC8-104A-4ACA-9EA4-524D52996E57", 256);
        static Native.DEVPROPKEY KeyContainerId = new Native.DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        static Native.DEVPROPKEY KeyIsConnected = new Native.DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);

        class ContainerBucket
        {
            public Guid ContainerId;
            public string BestName;
            public int Battery = -1;
            public bool MatchedRazer = false;
            public bool HasConnectedFlag = false;
            public bool IsConnected = true;
        }

        public static bool TryReadBluetoothBattery(out string deviceName, out int battery, out bool isConnected)
        {
            deviceName = null;
            battery = -1;
            isConnected = true;

            IntPtr hDevInfo = Native.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
            if (hDevInfo == Native.INVALID_HANDLE_VALUE || hDevInfo == IntPtr.Zero)
                return false;

            var buckets = new Dictionary<Guid, ContainerBucket>();

            try
            {
                uint idx = 0;
                while (true)
                {
                    var devData = new Native.SP_DEVINFO_DATA();
                    devData.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                    if (!Native.SetupDiEnumDeviceInfo(hDevInfo, idx, ref devData))
                        break;
                    idx++;

                    string instId = GetStringProperty(hDevInfo, ref devData, ref KeyInstanceId);
                    if (string.IsNullOrEmpty(instId)) continue;

                    string upperInst = instId.ToUpperInvariant();
                    // 只关注蓝牙相关节点 (BTHLE / BTHENUM / BTHLEDEVICE / 0000180F Battery Service)
                    if (!upperInst.Contains("BTH") && !upperInst.Contains("180F"))
                        continue;

                    Guid cid = GetGuidProperty(hDevInfo, ref devData, ref KeyContainerId);
                    if (cid == Guid.Empty) continue;

                    ContainerBucket bucket;
                    if (!buckets.TryGetValue(cid, out bucket))
                    {
                        bucket = new ContainerBucket();
                        bucket.ContainerId = cid;
                        buckets[cid] = bucket;
                    }

                    string fname = GetStringProperty(hDevInfo, ref devData, ref KeyFriendlyName);
                    string desc = GetStringProperty(hDevInfo, ref devData, ref KeyDeviceDesc);

                    if (RazerDeviceCatalog.IsRazerBluetoothIdentity(fname) ||
                        RazerDeviceCatalog.IsRazerBluetoothIdentity(desc) ||
                        RazerDeviceCatalog.IsRazerBluetoothIdentity(instId))
                    {
                        bucket.MatchedRazer = true;
                        if (!string.IsNullOrEmpty(fname) && !IsGenericGattName(fname))
                            bucket.BestName = fname;
                        else if (string.IsNullOrEmpty(bucket.BestName) && !string.IsNullOrEmpty(desc) && !IsGenericGattName(desc))
                            bucket.BestName = desc;
                    }
                    else if (string.IsNullOrEmpty(bucket.BestName) && !string.IsNullOrEmpty(fname) && !IsGenericGattName(fname))
                    {
                        bucket.BestName = fname;
                    }

                    int lv = GetBatteryProperty(hDevInfo, ref devData);
                    if (lv >= 0 && lv <= 100)
                        bucket.Battery = lv;

                    bool conn;
                    if (TryGetBoolProperty(hDevInfo, ref devData, ref KeyIsConnected, out conn))
                    {
                        if (!bucket.HasConnectedFlag)
                        {
                            bucket.HasConnectedFlag = true;
                            bucket.IsConnected = conn;
                        }
                        else if (conn)
                        {
                            bucket.IsConnected = true;
                        }
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            // 优先返回已连接且匹配雷蛇标识的蓝牙设备
            ContainerBucket best = null;
            foreach (var kv in buckets)
            {
                ContainerBucket b = kv.Value;
                if (!b.MatchedRazer || b.Battery < 0) continue;
                if (best == null)
                    best = b;
                else if (b.IsConnected && !best.IsConnected)
                    best = b;
            }

            if (best != null)
            {
                deviceName = RazerDeviceCatalog.NormalizeBluetoothName(best.BestName);
                battery = best.Battery;
                isConnected = best.IsConnected;
                return true;
            }

            return false;
        }

        static bool IsGenericGattName(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            string u = s.ToUpperInvariant();
            return u.Contains("GENERIC ATTRIBUTE") || u.Contains("BATTERY SERVICE") ||
                   u.Contains("DEVICE INFORMATION") || u.Contains("BLUETOOTH LE") ||
                   u.Contains("HID-COMPLIANT") || u.Contains("符合 HID");
        }

        static string GetStringProperty(IntPtr hDevInfo, ref Native.SP_DEVINFO_DATA devData, ref Native.DEVPROPKEY key)
        {
            uint propType;
            uint reqSize = 0;
            Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, IntPtr.Zero, 0, out reqSize, 0);
            if (reqSize == 0 || reqSize > 2048) return null;

            IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
            try
            {
                if (!Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, buf, reqSize, out reqSize, 0))
                    return null;
                if (propType != Native.DEVPROP_TYPE_STRING) return null;
                string s = Marshal.PtrToStringUni(buf);
                return s != null ? s.TrimEnd('\0') : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static Guid GetGuidProperty(IntPtr hDevInfo, ref Native.SP_DEVINFO_DATA devData, ref Native.DEVPROPKEY key)
        {
            uint propType;
            uint reqSize = 0;
            Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, IntPtr.Zero, 0, out reqSize, 0);
            if (reqSize < 16) return Guid.Empty;

            IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
            try
            {
                if (!Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, buf, reqSize, out reqSize, 0))
                    return Guid.Empty;
                if (propType != Native.DEVPROP_TYPE_GUID) return Guid.Empty;
                byte[] b = new byte[16];
                Marshal.Copy(buf, b, 0, 16);
                return new Guid(b);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static int GetBatteryProperty(IntPtr hDevInfo, ref Native.SP_DEVINFO_DATA devData)
        {
            uint propType;
            uint reqSize = 0;
            Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref KeyBattery, out propType, IntPtr.Zero, 0, out reqSize, 0);
            if (reqSize == 0 || reqSize > 16) return -1;

            IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
            try
            {
                if (!Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref KeyBattery, out propType, buf, reqSize, out reqSize, 0))
                    return -1;
                if (propType == Native.DEVPROP_TYPE_BYTE || propType == Native.DEVPROP_TYPE_SBYTE || reqSize == 1)
                    return Marshal.ReadByte(buf);
                if (propType == Native.DEVPROP_TYPE_INT16 || propType == Native.DEVPROP_TYPE_UINT16 || reqSize == 2)
                    return Marshal.ReadInt16(buf);
                if (propType == Native.DEVPROP_TYPE_INT32 || propType == Native.DEVPROP_TYPE_UINT32 || reqSize >= 4)
                    return Marshal.ReadInt32(buf);
                return -1;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static bool TryGetBoolProperty(IntPtr hDevInfo, ref Native.SP_DEVINFO_DATA devData, ref Native.DEVPROPKEY key, out bool val)
        {
            val = false;
            uint propType;
            uint reqSize = 0;
            Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, IntPtr.Zero, 0, out reqSize, 0);
            if (reqSize == 0 || reqSize > 8) return false;

            IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
            try
            {
                if (!Native.SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, buf, reqSize, out reqSize, 0))
                    return false;
                if (propType != Native.DEVPROP_TYPE_BOOLEAN) return false;
                val = Marshal.ReadByte(buf) != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    // ------------------------------------------------------------------ 通道 B：雷云 (Synapse 3 / 4) 日志协同解析
    static class SynapseLogWatcher
    {
        static readonly Regex RxPercentage = new Regex(
            @"(?:""percentage""|""batteryPercentage""|""batteryLevel""|BatteryPercentage|BatteryLevel|battery_level)\s*[:=]\s*(\d{1,3})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex RxCharging = new Regex(
            @"(?:""isCharging""|""charging""|""chargingStatus""|IsCharging|ChargingState|charging_state)\s*[:=]\s*""?(true|false|1|0|charging|discharging)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex RxModel = new Regex(
            @"(Razer\s+Basilisk\s+V3\s+Pro|Basilisk\s+V3\s+Pro|BSK\s+V3\s+PRO|Razer\s+Viper\s+V[23]\s+Pro|Razer\s+DeathAdder\s+V[23]\s+Pro|Razer\s+Cobra\s+Pro|Razer\s+Naga\s+V2\s+Pro)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsSynapseRunning()
        {
            try
            {
                string[] names = new string[]
                {
                    "RazerAppEngine",
                    "Razer Synapse 3",
                    "Razer Synapse Service"
                };
                for (int i = 0; i < names.Length; i++)
                {
                    Process[] ps = Process.GetProcessesByName(names[i]);
                    if (ps != null && ps.Length > 0) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool TryReadLatestFromLogs(out string modelName, out int battery, out int chargeState, out DateTime logWriteTime)
        {
            modelName = null;
            battery = -1;
            chargeState = 1;
            logWriteTime = DateTime.MinValue;

            List<string> candidates = GetCandidateLogFiles();
            for (int i = 0; i < candidates.Count; i++)
            {
                string path = candidates[i];
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length == 0) continue;
                    // 若日志文件超过 6 小时未更新，说明不是当前活跃会话日志
                    if ((DateTime.Now - fi.LastWriteTime).TotalHours > 6.0) continue;

                    string tail = ReadFileTailShared(path, 192 * 1024);
                    if (string.IsNullOrEmpty(tail)) continue;

                    string[] lines = tail.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int foundBat = -1;
                    int foundCharge = 1;
                    bool hasCharge = false;
                    string foundModel = null;

                    for (int idx = lines.Length - 1; idx >= 0; idx--)
                    {
                        string line = lines[idx];
                        if (foundModel == null)
                        {
                            Match mm = RxModel.Match(line);
                            if (mm.Success)
                                foundModel = RazerDeviceCatalog.NormalizeBluetoothName(mm.Groups[1].Value);
                        }

                        if (!hasCharge)
                        {
                            Match mc = RxCharging.Match(line);
                            if (mc.Success)
                            {
                                string cv = mc.Groups[1].Value.ToLowerInvariant();
                                foundCharge = (cv == "true" || cv == "1" || cv == "charging") ? 3 : 1;
                                hasCharge = true;
                            }
                        }

                        if (foundBat < 0)
                        {
                            // 仅匹配带 battery / power / charge 上下文或标准 percentage 字段的行
                            string lower = line.ToLowerInvariant();
                            if (lower.Contains("battery") || lower.Contains("power") || lower.Contains("percentage"))
                            {
                                Match mb = RxPercentage.Match(line);
                                if (mb.Success)
                                {
                                    int v;
                                    if (int.TryParse(mb.Groups[1].Value, out v) && v >= 0 && v <= 100)
                                    {
                                        foundBat = v;
                                    }
                                }
                            }
                        }

                        if (foundBat >= 0 && hasCharge && foundModel != null)
                            break;
                    }

                    if (foundBat >= 0)
                    {
                        battery = foundBat;
                        chargeState = foundCharge;
                        modelName = string.IsNullOrEmpty(foundModel) ? "BSK V3 PRO" : foundModel;
                        logWriteTime = fi.LastWriteTime;
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        static List<string> GetCandidateLogFiles()
        {
            var list = new List<string>();
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            // Synapse 4 (RazerAppEngine)
            string s4Dir = Path.Combine(localApp, @"Razer\RazerAppEngine\User Data\Logs");
            AddIfExists(list, Path.Combine(s4Dir, "background-manager.log"));
            AddIfExists(list, Path.Combine(s4Dir, "main.log"));

            // Synapse 3
            string s3Local = Path.Combine(localApp, @"Razer\Synapse3\Log");
            AddIfExists(list, Path.Combine(s3Local, "Razer Synapse 3.log"));
            AddRecentLogsInDir(list, s3Local);

            string s3Prog = Path.Combine(progData, @"Razer\Synapse3\Log");
            AddIfExists(list, Path.Combine(s3Prog, @"Service\Razer Synapse Service.log"));
            AddRecentLogsInDir(list, s3Prog);

            return list;
        }

        static void AddIfExists(List<string> list, string p)
        {
            try
            {
                if (File.Exists(p) && !list.Contains(p))
                    list.Add(p);
            }
            catch { }
        }

        static void AddRecentLogsInDir(List<string> list, string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string[] files = Directory.GetFiles(dir, "*.log", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    if (!list.Contains(files[i]))
                        list.Add(files[i]);
                }
            }
            catch { }
        }

        static string ReadFileTailShared(string path, int maxBytes)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                if (len <= 0) return string.Empty;
                int toRead = (int)Math.Min(len, maxBytes);
                fs.Seek(len - toRead, SeekOrigin.Begin);
                byte[] buf = new byte[toRead];
                int got = fs.Read(buf, 0, toRead);
                if (got <= 0) return string.Empty;
                return Encoding.UTF8.GetString(buf, 0, got);
            }
        }
    }

    // ------------------------------------------------------------------ 通道 C：Razer 90-Byte HID Feature Report 引擎
    static class RazerHidEngine
    {
        static readonly byte[] CandidateTxIds = new byte[] { 0x1F, 0x3F, 0xFF };

        public static List<HidInterface> EnumerateRazerControlInterfaces()
        {
            var list = new List<HidInterface>();
            Native.GUID guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return list;

            try
            {
                int idx = 0;
                while (true)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, idx, ref did))
                        break;
                    idx++;

                    int need;
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;

                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, 0, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, need, out need, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        // desiredAccess = 0：免读写流权限打开句柄，绕过 Windows mouclass 独占锁，且不干扰雷云
                        IntPtr h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == Native.INVALID_HANDLE_VALUE) continue;

                        var dev = new HidInterface();
                        dev.Path = path;
                        try
                        {
                            var attr = new Native.HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                            if (Native.HidD_GetAttributes(h, ref attr))
                            {
                                dev.Vid = attr.VendorID;
                                dev.Pid = attr.ProductID;
                            }

                            if (dev.Vid != RazerDeviceCatalog.RAZER_VID)
                                continue;

                            IntPtr sbuf = Marshal.AllocHGlobal(512);
                            try
                            {
                                if (Native.HidD_GetProductString(h, sbuf, 512))
                                    dev.Product = Marshal.PtrToStringUni(sbuf);
                            }
                            finally { Marshal.FreeHGlobal(sbuf); }

                            IntPtr pp;
                            if (Native.HidD_GetPreparsedData(h, out pp) && pp != IntPtr.Zero)
                            {
                                try
                                {
                                    byte[] caps = new byte[256];
                                    if (Native.HidP_GetCaps(pp, caps) == 0x00110000)
                                    {
                                        dev.Usage = BitConverter.ToUInt16(caps, 0);
                                        dev.UsagePage = BitConverter.ToUInt16(caps, 2);
                                        dev.InLen = BitConverter.ToUInt16(caps, 4);
                                        dev.OutLen = BitConverter.ToUInt16(caps, 6);
                                        dev.FeatLen = BitConverter.ToUInt16(caps, 8);
                                    }
                                }
                                finally { Native.HidD_FreePreparsedData(pp); }
                            }
                        }
                        finally { Native.CloseHandle(h); }

                        // 雷蛇标准 90 字节协议接口在 Windows 下 FeatLen == 91 (1字节 ReportID + 90字节帧)
                        if (dev.FeatLen >= 90)
                        {
                            dev.Transport = RazerDeviceCatalog.IsWiredPid(dev.Pid) ? "有线 USB" : "2.4G 无线接收器";
                            list.Add(dev);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }

            return list;
        }

        // 计算 Razer 90 字节报文 XOR 校验和 (Windows 91 字节缓冲区的索引 [2..87]，存入 [88])
        static byte ComputeCrc(byte[] report91)
        {
            byte crc = 0;
            for (int i = 2; i <= 87 && i < report91.Length; i++)
                crc ^= report91[i];
            return crc;
        }

        static byte[] BuildRazerQuery(int featLen, byte txId, byte cmdClass, byte cmdId, byte dataSize)
        {
            int len = Math.Max(91, featLen);
            byte[] pkt = new byte[len];
            pkt[0] = 0x00;      // Report ID
            pkt[1] = 0x00;      // Status = New Command
            pkt[2] = txId;      // Transaction ID (0x1F / 0x3F / 0xFF)
            pkt[3] = 0x00;      // Remaining packets high
            pkt[4] = 0x00;      // Remaining packets low
            pkt[5] = 0x00;      // Protocol type
            pkt[6] = dataSize;  // Data size
            pkt[7] = cmdClass;  // Command class (0x07 = Power/Battery)
            pkt[8] = cmdId;     // Command ID (0x80 = Get Battery, 0x84 = Get Charging)
            pkt[88] = ComputeCrc(pkt);
            return pkt;
        }

        // 防冲突发送并读取 Razer Feature Report
        static bool ExecuteTransaction(IntPtr h, int featLen, byte txId, byte cmdClass, byte cmdId, byte dataSize, out byte[] resp, out byte status)
        {
            int len = Math.Max(91, featLen);
            resp = new byte[len];
            status = 0x00;

            // 1. 总线忙闲探测：若雷云正在等待固件响应 (Status == 0x01 Busy)，礼让退避
            byte[] probe = new byte[len];
            if (Native.HidD_GetFeature(h, probe, len))
            {
                if (probe[1] == 0x01)
                {
                    Thread.Sleep(250);
                }
            }

            // 2. 发送 Feature Report 查询请求
            byte[] req = BuildRazerQuery(len, txId, cmdClass, cmdId, dataSize);
            if (!Native.HidD_SetFeature(h, req, len))
                return false;

            // 3. 轮询读取回包 (最多重试 3 次，每次间隔 65ms)
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(65);
                Array.Clear(resp, 0, len);
                if (!Native.HidD_GetFeature(h, resp, len))
                    continue;

                status = resp[1];
                if (status == 0x01) // Busy，继续等待
                    continue;

                // 校验回包的 Command Class 与 Command ID 是否与本请求一致（防止读到雷云其他指令回包）
                if (resp[7] != cmdClass || resp[8] != cmdId)
                    return false;

                if (status == 0x02) // Success
                {
                    byte expectedCrc = ComputeCrc(resp);
                    if (resp[88] == expectedCrc || resp[88] == 0)
                        return true;
                }

                // 0x04 = Timeout (鼠标处于休眠态), 0x03 = Failure
                return false;
            }

            return false;
        }

        public static bool QueryMouseBattery(HidInterface dev, ref byte preferredTxId, out int batteryPercent, out int chargeState, out bool isSleeping)
        {
            batteryPercent = -1;
            chargeState = 1;
            isSleeping = false;

            IntPtr h = Native.CreateFileW(dev.Path, 0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
                return false;

            try
            {
                var txOrder = new List<byte>();
                if (preferredTxId != 0) txOrder.Add(preferredTxId);
                for (int i = 0; i < CandidateTxIds.Length; i++)
                {
                    if (!txOrder.Contains(CandidateTxIds[i]))
                        txOrder.Add(CandidateTxIds[i]);
                }

                for (int i = 0; i < txOrder.Count; i++)
                {
                    byte tx = txOrder[i];
                    byte[] resp;
                    byte status;

                    // 查询电量：Class 0x07, ID 0x80
                    if (ExecuteTransaction(h, dev.FeatLen, tx, 0x07, 0x80, 0x02, out resp, out status))
                    {
                        byte rawBat = resp[10];
                        // 部分固件把 1 字节返回放在 resp[9]（当 resp[10]==0 且 resp[9]>0 时兼容）
                        if (rawBat == 0 && resp[9] > 0) rawBat = resp[9];

                        int pct = (int)Math.Round(rawBat * 100.0 / 255.0);
                        if (pct < 0) pct = 0;
                        if (pct > 100) pct = 100;

                        // 查询充电状态：Class 0x07, ID 0x84
                        byte[] cResp;
                        byte cStatus;
                        bool charging = false;
                        if (ExecuteTransaction(h, dev.FeatLen, tx, 0x07, 0x84, 0x02, out cResp, out cStatus))
                        {
                            charging = (cResp[10] != 0 || cResp[9] != 0);
                        }
                        else if (RazerDeviceCatalog.IsWiredPid(dev.Pid))
                        {
                            charging = true;
                        }

                        preferredTxId = tx;
                        batteryPercent = pct;
                        chargeState = charging ? (pct >= 100 ? 2 : 3) : 1;

                        // 当无线鼠标休眠时，部分接收器会返回 Status=0x02 但 rawBat=0 且未充电
                        if (pct == 0 && !charging && !RazerDeviceCatalog.IsWiredPid(dev.Pid))
                        {
                            isSleeping = true;
                        }

                        return true;
                    }
                    else if (status == 0x04)
                    {
                        // 0x04 明确代表无线接收器在线但鼠标本体正在休眠
                        isSleeping = true;
                    }
                }
            }
            finally
            {
                Native.CloseHandle(h);
            }

            return false;
        }
    }

    static class Program
    {
        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string CFG_KEY = @"Software\RazerBatteryTray";
        const string RUN_NAME = "RazerBatteryTray";

        static NotifyIcon tray;
        static Icon currentIcon;
        static string logPath = null;
        static Bitmap srcIcon = null;
        static int iconTopY = 0;
        static int iconBottomY = 255;

        static MenuItem autoItem;
        static MenuItem modeAutoItem;
        static MenuItem modeBleHidItem;
        static MenuItem modeSynapseOnlyItem;
        static MenuItem hours32Item;
        static MenuItem hours90Item;
        static MenuItem hours150Item;

        // 0 = 自动三通道协同 (推荐), 1 = 仅硬件直连 (BLE + HID), 2 = 仅蓝牙 + 雷云日志
        static volatile int dataSourceMode = 0;
        // 满电续航基准小时数（默认 32 小时：在 85% 电量下 85% * 32 = 27.2 ≈ 27 小时，与图 2 完全一致）
        static volatile int fullBatteryHours = 32;

        static int lastDrawnLevel = -999;
        static bool lastDrawnOffline = true;
        static bool lastDrawnCharging = false;

        // 轮询与离线参数（雷蛇主动轮询周期 15s，超过 90s 无任何响应或处于休眠态则标记休眠）
        const int PollIntervalActiveMs = 15000;
        const int OfflineAfterMs = 95000;
        static System.Windows.Forms.Timer statusTimer;

        static readonly object sync = new object();
        static readonly object traySync = new object();
        static DeviceState activeState = null;
        static volatile bool running = true;
        static Thread supervisor;
        static AutoResetEvent wakePollEvent = new AutoResetEvent(false);

        static void Log(string s)
        {
            if (logPath == null) return;
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            logPath = Environment.GetEnvironmentVariable("RAZER_TRAY_LOG");
            foreach (string a in args)
            {
                if (a.StartsWith("--log=")) logPath = a.Substring(6);
            }
            if (logPath == null)
            {
                try
                {
                    string dir = Path.GetDirectoryName(Application.ExecutablePath);
                    logPath = Path.Combine(dir, "tray.log");
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
                        File.Delete(logPath);
                }
                catch { logPath = null; }
            }
            Log("=== RazerBatteryTray start ===");

            LoadUserConfig();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadIconSource();

            tray = new NotifyIcon();
            tray.Text = "雷蛇鼠标电量：搜索设备中…";
            tray.Icon = DrawIcon(-1, true, false);
            tray.Visible = true;

            var menu = new ContextMenu();

            menu.MenuItems.Add("显示设备详情", delegate { ShowDetails(); });
            menu.MenuItems.Add("立即刷新电量", delegate { TriggerImmediatePoll(); });
            menu.MenuItems.Add("-");

            // 数据源模式子菜单
            var modeMenu = new MenuItem("数据源模式");
            modeAutoItem = new MenuItem("自动协同：蓝牙 + 雷云日志 + HID (推荐)", delegate { SetDataSourceMode(0); });
            modeBleHidItem = new MenuItem("仅硬件直连：蓝牙 + 2.4G/有线 HID", delegate { SetDataSourceMode(1); });
            modeSynapseOnlyItem = new MenuItem("零总线占用：仅蓝牙 + 雷云日志", delegate { SetDataSourceMode(2); });
            modeMenu.MenuItems.Add(modeAutoItem);
            modeMenu.MenuItems.Add(modeBleHidItem);
            modeMenu.MenuItems.Add(modeSynapseOnlyItem);
            menu.MenuItems.Add(modeMenu);

            // 续航估算基准子菜单
            var hoursMenu = new MenuItem("续航时间估算基准");
            hours32Item = new MenuItem("32 小时 (开启 Chroma 灯效默认，85%≈27h)", delegate { SetFullBatteryHours(32); });
            hours90Item = new MenuItem("90 小时 (2.4G HyperSpeed 关灯续航)", delegate { SetFullBatteryHours(90); });
            hours150Item = new MenuItem("150 小时 (蓝牙 BLE 超长续航)", delegate { SetFullBatteryHours(150); });
            hoursMenu.MenuItems.Add(hours32Item);
            hoursMenu.MenuItems.Add(hours90Item);
            hoursMenu.MenuItems.Add(hours150Item);
            menu.MenuItems.Add(hoursMenu);

            UpdateMenuChecks();

            menu.MenuItems.Add("-");
            autoItem = new MenuItem("开机自动启动");
            autoItem.Checked = IsAutoStartEnabled();
            autoItem.Click += delegate { ToggleAutoStart(); };
            menu.MenuItems.Add(autoItem);

            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate { Quit(); });

            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { ShowDetails(); };

            supervisor = new Thread(PollSupervisorLoop);
            supervisor.IsBackground = true;
            supervisor.Start();

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 2000;
            statusTimer.Tick += delegate { TickStatus(); };
            statusTimer.Start();

            if (Environment.GetEnvironmentVariable("RAZER_TEST_POPUP") == "1")
            {
                var testTimer = new System.Windows.Forms.Timer();
                testTimer.Interval = 2500;
                testTimer.Tick += delegate
                {
                    testTimer.Stop();
                    try { ShowDetails(); Log("test popup shown"); }
                    catch (Exception ex) { Log("test popup FAILED: " + ex); }
                };
                testTimer.Start();
            }

            Application.Run();
        }

        // ---------------------------------------------------------- 用户配置持久化
        static void LoadUserConfig()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CFG_KEY, false))
                {
                    if (k != null)
                    {
                        object m = k.GetValue("DataSourceMode");
                        if (m != null)
                        {
                            int mv;
                            if (int.TryParse(m.ToString(), out mv) && mv >= 0 && mv <= 2)
                                dataSourceMode = mv;
                        }
                        object h = k.GetValue("FullBatteryHours");
                        if (h != null)
                        {
                            int hv;
                            if (int.TryParse(h.ToString(), out hv) && hv >= 5 && hv <= 500)
                                fullBatteryHours = hv;
                        }
                    }
                }
            }
            catch { }

            string envHours = Environment.GetEnvironmentVariable("RAZER_FULL_HOURS");
            if (!string.IsNullOrEmpty(envHours))
            {
                int eh;
                if (int.TryParse(envHours, out eh) && eh >= 5 && eh <= 500)
                    fullBatteryHours = eh;
            }
        }

        static void SaveUserConfig()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CFG_KEY))
                {
                    if (k != null)
                    {
                        k.SetValue("DataSourceMode", dataSourceMode, RegistryValueKind.DWord);
                        k.SetValue("FullBatteryHours", fullBatteryHours, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        static void SetDataSourceMode(int mode)
        {
            dataSourceMode = mode;
            SaveUserConfig();
            UpdateMenuChecks();
            TriggerImmediatePoll();
        }

        static void SetFullBatteryHours(int hours)
        {
            fullBatteryHours = hours;
            SaveUserConfig();
            UpdateMenuChecks();
            TickStatus();
        }

        static void UpdateMenuChecks()
        {
            if (modeAutoItem != null) modeAutoItem.Checked = (dataSourceMode == 0);
            if (modeBleHidItem != null) modeBleHidItem.Checked = (dataSourceMode == 1);
            if (modeSynapseOnlyItem != null) modeSynapseOnlyItem.Checked = (dataSourceMode == 2);

            if (hours32Item != null) hours32Item.Checked = (fullBatteryHours == 32);
            if (hours90Item != null) hours90Item.Checked = (fullBatteryHours == 90);
            if (hours150Item != null) hours150Item.Checked = (fullBatteryHours == 150);
        }

        static int EstimateRemainingHours(int batteryPercent)
        {
            if (batteryPercent <= 0) return 0;
            int h = (int)Math.Round(batteryPercent * (double)fullBatteryHours / 100.0);
            return Math.Max(1, h);
        }

        // ---------------------------------------------------------- 开机自启
        static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RUN_NAME);
                    if (v == null) return false;
                    string cur = v.ToString().Trim('"');
                    return string.Equals(cur, Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        static void ToggleAutoStart()
        {
            try
            {
                bool want = !IsAutoStartEnabled();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null)
                    {
                        MessageBox.Show("无法打开启动项注册表键。", "Razer 电量托盘",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (want)
                        k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                    else
                        k.DeleteValue(RUN_NAME, false);
                }
                if (autoItem != null) autoItem.Checked = IsAutoStartEnabled();
                Log("autostart -> " + want);
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机自启动失败：" + ex.Message, "Razer 电量托盘",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------------------------------------------------- 雷蛇三头蛇图标动态渲染
        static void LoadIconSource()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("MouseIcon.png"))
                {
                    if (s != null) srcIcon = new Bitmap(s);
                }
                if (srcIcon != null)
                {
                    FindVerticalBounds(srcIcon, out iconTopY, out iconBottomY);
                }
                Log("icon source: " + (srcIcon == null ? "MISSING" : srcIcon.Width + "x" + srcIcon.Height));
            }
            catch (Exception ex) { Log("icon load error: " + ex.Message); }
        }

        static void FindVerticalBounds(Bitmap bmp, out int topY, out int bottomY)
        {
            topY = 0;
            bottomY = bmp.Height - 1;
            int w = bmp.Width;
            int h = bmp.Height;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (bmp.GetPixel(x, y).A >= 25)
                    {
                        topY = y;
                        y = h;
                        break;
                    }
                }
            }

            for (int y = h - 1; y >= topY; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    if (bmp.GetPixel(x, y).A >= 25)
                    {
                        bottomY = y;
                        y = -1;
                        break;
                    }
                }
            }
        }

        // 按电量百分比从下往上对雷蛇三头蛇 Logo 进行双色阶实心液位填充
        // - 已填充区域：外圈高亮雷蛇荧光绿 (#50EB32) + 内芯饱满雷蛇绿 (#2DB919)，低电自动变琥珀黄/警报红
        // - 未填充区域 / 休眠离线：高对比度银灰外圈 (#7D848C) + 暗钢灰内芯 (#41464C)
        static Icon DrawIcon(int level, bool offline, bool charging)
        {
            Size sz = SystemInformation.SmallIconSize;
            if (sz.Width < 16) sz = new Size(16, 16);
            int w = sz.Width, h = sz.Height;

            using (Bitmap scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.Clear(Color.Transparent);
                    if (srcIcon != null)
                        g.DrawImage(srcIcon, new Rectangle(0, 0, w, h));
                }

                int sTop, sBottom;
                FindVerticalBounds(scaled, out sTop, out sBottom);
                int effectiveHeight = Math.Max(1, sBottom - sTop);
                int clamped = Math.Max(0, Math.Min(100, level));
                double fillTop = sBottom - effectiveHeight * (clamped / 100.0);

                bool grey = offline || level < 0;

                Color fillStroke, fillBody;
                if (level <= 15)
                {
                    fillStroke = Color.FromArgb(255, 60, 60);
                    fillBody = Color.FromArgb(220, 25, 25);
                }
                else if (level <= 40)
                {
                    fillStroke = Color.FromArgb(255, 200, 20);
                    fillBody = Color.FromArgb(230, 155, 0);
                }
                else
                {
                    // 雷蛇标志性荧光绿双色阶
                    fillStroke = Color.FromArgb(80, 235, 50);
                    fillBody = Color.FromArgb(45, 185, 25);
                }

                Color emptyStroke = Color.FromArgb(125, 132, 140);
                Color emptyBody = Color.FromArgb(65, 70, 76);

                using (Bitmap outp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color c = scaled.GetPixel(x, y);
                            if (c.A < 25) continue;

                            // 原图中外圈是亮绿描边 (G 值高)，蛇身内芯是深黑 (RGB 值低)
                            double lum = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
                            double t = Math.Min(1.0, lum * 1.3);

                            bool isFilledPixel = (!grey) && (y >= fillTop);
                            Color sc = isFilledPixel ? fillStroke : emptyStroke;
                            Color bc = isFilledPixel ? fillBody : emptyBody;

                            int nr = (int)(bc.R * (1.0 - t) + sc.R * t);
                            int ng = (int)(bc.G * (1.0 - t) + sc.G * t);
                            int nb = (int)(bc.B * (1.0 - t) + sc.B * t);

                            if (nr < 0) nr = 0; else if (nr > 255) nr = 255;
                            if (ng < 0) ng = 0; else if (ng > 255) ng = 255;
                            if (nb < 0) nb = 0; else if (nb > 255) nb = 255;

                            outp.SetPixel(x, y, Color.FromArgb(c.A, nr, ng, nb));
                        }
                    }

                    // 充电中状态：在右下角叠加绘制高对比度黄色闪电标识
                    if (charging && !grey)
                    {
                        using (Graphics gOut = Graphics.FromImage(outp))
                        {
                            gOut.SmoothingMode = SmoothingMode.AntiAlias;
                            float fx = w / 16.0f;
                            float fy = h / 16.0f;
                            PointF[] bolt = new PointF[]
                            {
                                new PointF(13.2f * fx, 8.2f * fy),
                                new PointF(10.2f * fx, 12.0f * fy),
                                new PointF(12.2f * fx, 12.0f * fy),
                                new PointF(11.2f * fx, 15.5f * fy),
                                new PointF(15.2f * fx, 11.2f * fy),
                                new PointF(13.0f * fx, 11.2f * fy)
                            };
                            using (Brush bFill = new SolidBrush(Color.FromArgb(255, 230, 20)))
                            using (Pen pEdge = new Pen(Color.FromArgb(20, 24, 20), Math.Max(1.0f, fx * 0.7f)))
                            {
                                gOut.DrawPolygon(pEdge, bolt);
                                gOut.FillPolygon(bFill, bolt);
                            }
                        }
                    }

                    IntPtr hIcon = outp.GetHicon();
                    try
                    {
                        if (logPath != null)
                        {
                            try { outp.Save(logPath + ".icon.png", ImageFormat.Png); }
                            catch { }
                        }
                        using (Icon tmp = Icon.FromHandle(hIcon))
                            return (Icon)tmp.Clone();
                    }
                    finally
                    {
                        Native.DestroyIcon(hIcon);
                    }
                }
            }
        }

        // ---------------------------------------------------------- 菜单动作与详情浮层
        static void Quit()
        {
            running = false;
            wakePollEvent.Set();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Exit();
        }

        static void TriggerImmediatePoll()
        {
            wakePollEvent.Set();
        }

        static string ChargeText(int c)
        {
            switch (c)
            {
                case 1: return "放电中（未充电）";
                case 2: return "已充满 (100%)";
                case 3: return "正在充电 ⚡";
                default: return "未知";
            }
        }

        static void ShowDetails()
        {
            string msg;
            lock (sync)
            {
                if (activeState == null || activeState.Battery < 0)
                {
                    msg = "尚未检测到雷蛇无线鼠标电量数据。\n\n"
                        + "支持模式：\n"
                        + "· 蓝牙连接 (如 BSK V3 PRO)\n"
                        + "· 2.4G HyperSpeed / HyperPolling 无线接收器\n"
                        + "· 有线 USB 连接 / 雷云 (Synapse 3/4) 后台协同";
                }
                else
                {
                    DeviceState d = activeState;
                    bool off = d.IsSleeping || ((DateTime.Now - d.LastSeen).TotalMilliseconds > OfflineAfterMs);
                    int remHours = EstimateRemainingHours(d.Battery);

                    var sb = new StringBuilder();
                    sb.AppendLine(string.Format("● {0}", string.IsNullOrEmpty(d.DisplayName) ? "BSK V3 PRO" : d.DisplayName));
                    sb.AppendLine("────────────────────────────────────");
                    if (d.ChargeState == 3)
                        sb.AppendLine(string.Format("  当前电量  : {0}% (正在充电 ⚡)", d.Battery));
                    else
                        sb.AppendLine(string.Format("  当前电量  : {0}%，约可再使用 {1} 小时", d.Battery, remHours));

                    sb.AppendLine("  充电状态  : " + ChargeText(d.ChargeState));
                    sb.AppendLine("  连接方式  : " + (string.IsNullOrEmpty(d.Transport) ? "未知" : d.Transport));
                    sb.AppendLine("  采集通道  : " + (string.IsNullOrEmpty(d.SourceEngine) ? "自动" : d.SourceEngine));
                    if (d.Vid != 0 || d.Pid != 0)
                        sb.AppendLine(string.Format("  硬件 ID   : {0:X4}:{1:X4}", d.Vid, d.Pid));
                    sb.AppendLine(string.Format("  最后同步  : {0:HH:mm:ss}", d.LastSeen));
                    sb.AppendLine("  运行状态  : " + (off ? "休眠中 / 已断开 (保留上次电量)" : "在线活跃"));
                    msg = sb.ToString().TrimEnd();
                }
            }
            ShowInfoPopup(msg);
        }

        static InfoPopup popup;

        static void ShowInfoPopup(string text)
        {
            if (popup != null)
            {
                try { popup.Close(); popup.Dispose(); } catch { }
                popup = null;
            }
            popup = new InfoPopup(text);
            popup.FormClosed += delegate { popup = null; };
            popup.ShowNear(Cursor.Position);
        }

        // ---------------------------------------------------------- 三通道轮询调度主循环
        static void PollSupervisorLoop()
        {
            Dictionary<string, byte> txCache = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            while (running)
            {
                try
                {
                    bool updated = false;

                    // 1. 通道 A：优先检测蓝牙 BLE 连接 (如 BSK V3 PRO)
                    string bleName;
                    int bleBat;
                    bool bleConnected;
                    if (BluetoothBatteryReader.TryReadBluetoothBattery(out bleName, out bleBat, out bleConnected))
                    {
                        if (bleConnected && bleBat >= 0)
                        {
                            RecordBatteryState(
                                "BLE:" + bleName,
                                bleName,
                                bleBat,
                                1,
                                "蓝牙 (BLE)",
                                "Windows BLE 电池服务",
                                RazerDeviceCatalog.RAZER_VID,
                                0,
                                false);
                            updated = true;
                            Log(string.Format("BLE battery: {0} -> {1}%", bleName, bleBat));
                        }
                    }

                    // 2. 通道 B：若开启了雷云 (Synapse 3 / 4) 且允许日志协同，无感读取雷云最新电量
                    bool synapseRunning = SynapseLogWatcher.IsSynapseRunning();
                    if (!updated && dataSourceMode != 1 && synapseRunning)
                    {
                        string logModel;
                        int logBat;
                        int logCharge;
                        DateTime logTime;
                        if (SynapseLogWatcher.TryReadLatestFromLogs(out logModel, out logBat, out logCharge, out logTime))
                        {
                            // 若日志写入时间在最近 15 分钟内，优先采信雷云日志，避免总线冲突
                            double ageMinutes = (DateTime.Now - logTime).TotalMinutes;
                            if (ageMinutes <= 15.0 || dataSourceMode == 2)
                            {
                                RecordBatteryState(
                                    "SYNAPSE:" + logModel,
                                    logModel,
                                    logBat,
                                    logCharge,
                                    "2.4G / 雷云同步",
                                    "Razer Synapse 协同日志",
                                    RazerDeviceCatalog.RAZER_VID,
                                    0,
                                    false);
                                updated = true;
                                Log(string.Format("Synapse log battery: {0} -> {1}% (charge={2})", logModel, logBat, logCharge));
                            }
                        }
                    }

                    // 3. 通道 C：原生 2.4G / 有线 HID Feature Report 直连查询 (未开雷云 或 雷云日志未命中时兜底)
                    if (!updated && dataSourceMode != 2)
                    {
                        List<HidInterface> hidDevs = RazerHidEngine.EnumerateRazerControlInterfaces();
                        for (int i = 0; i < hidDevs.Count; i++)
                        {
                            HidInterface dev = hidDevs[i];
                            byte txId = 0;
                            txCache.TryGetValue(dev.Path, out txId);

                            int hidBat;
                            int hidCharge;
                            bool sleeping;
                            if (RazerHidEngine.QueryMouseBattery(dev, ref txId, out hidBat, out hidCharge, out sleeping))
                            {
                                txCache[dev.Path] = txId;
                                string friendly = RazerDeviceCatalog.ResolveFriendlyName(dev.Pid, dev.Product);
                                RecordBatteryState(
                                    string.Format("HID:{0:X4}:{1:X4}", dev.Vid, dev.Pid),
                                    friendly,
                                    hidBat,
                                    hidCharge,
                                    dev.Transport,
                                    string.Format("Razer HID (Tx 0x{0:X2})", txId),
                                    dev.Vid,
                                    dev.Pid,
                                    sleeping);
                                updated = true;
                                Log(string.Format("HID battery: {0} [{1:X4}:{2:X4}] -> {3}% charge={4} sleep={5}",
                                    friendly, dev.Vid, dev.Pid, hidBat, hidCharge, sleeping));
                                break;
                            }
                            else if (sleeping)
                            {
                                MarkExistingSleeping();
                            }
                        }
                    }

                    // 如果通道 A 读到了已配对但当前未激活连接的蓝牙缓存电量，且 2.4G/HID 也没连上，则作为离线最后已知电量展示
                    if (!updated && bleBat >= 0 && !string.IsNullOrEmpty(bleName))
                    {
                        lock (sync)
                        {
                            if (activeState == null)
                            {
                                activeState = new DeviceState();
                                activeState.Key = "BLE:" + bleName;
                                activeState.DisplayName = bleName;
                                activeState.Battery = bleBat;
                                activeState.ChargeState = 1;
                                activeState.Transport = "蓝牙 (BLE)";
                                activeState.SourceEngine = "Windows BLE 缓存";
                                activeState.Vid = RazerDeviceCatalog.RAZER_VID;
                                activeState.LastSeen = DateTime.Now.AddMilliseconds(-OfflineAfterMs - 1000);
                                activeState.IsSleeping = true;
                            }
                        }
                        UpdateTrayFromUiThread();
                    }
                }
                catch (Exception ex)
                {
                    Log("PollSupervisor error: " + ex.Message);
                }

                wakePollEvent.WaitOne(PollIntervalActiveMs);
            }
        }

        static void RecordBatteryState(string key, string displayName, int battery, int chargeState,
            string transport, string engine, ushort vid, ushort pid, bool isSleeping)
        {
            DateTime now = DateTime.Now;
            lock (sync)
            {
                if (activeState == null)
                    activeState = new DeviceState();

                // 防抖：若无线鼠标进入休眠返回 0% 且之前有正常电量 (>5%)，保留上次有效电量并标记为休眠
                if (isSleeping && battery == 0 && activeState.Battery > 5)
                {
                    activeState.IsSleeping = true;
                }
                else
                {
                    activeState.Battery = battery;
                    activeState.ChargeState = chargeState;
                    activeState.LastSeen = now;
                    activeState.IsSleeping = false;
                }

                activeState.Key = key;
                activeState.DisplayName = string.IsNullOrEmpty(displayName) ? "BSK V3 PRO" : displayName;
                activeState.Transport = transport;
                activeState.SourceEngine = engine;
                activeState.Vid = vid;
                activeState.Pid = pid;
            }
            UpdateTrayFromUiThread();
        }

        static void MarkExistingSleeping()
        {
            lock (sync)
            {
                if (activeState != null)
                    activeState.IsSleeping = true;
            }
            UpdateTrayFromUiThread();
        }

        static void UpdateTrayFromUiThread()
        {
            try
            {
                TickStatus();
            }
            catch { }
        }

        static void TickStatus()
        {
            int level = -1;
            int charge = 1;
            string name = "BSK V3 PRO";
            DateTime lastSeen = DateTime.MinValue;
            bool sleeping = false;

            lock (sync)
            {
                if (activeState != null)
                {
                    level = activeState.Battery;
                    charge = activeState.ChargeState;
                    if (!string.IsNullOrEmpty(activeState.DisplayName))
                        name = activeState.DisplayName;
                    lastSeen = activeState.LastSeen;
                    sleeping = activeState.IsSleeping;
                }
            }

            bool offline = (level < 0) || sleeping ||
                           (lastSeen == DateTime.MinValue) ||
                           ((DateTime.Now - lastSeen).TotalMilliseconds > OfflineAfterMs);

            bool isCharging = (charge == 3);
            RefreshTray(name, level, isCharging, offline);
        }

        static void RefreshTray(string name, int level, bool isCharging, bool offline)
        {
            lock (traySync)
            {
                if (tray == null) return;

                if (level != lastDrawnLevel || offline != lastDrawnOffline || isCharging != lastDrawnCharging)
                {
                    Log(string.Format("tray icon update: name={0} level={1}% charging={2} offline={3}",
                        name, level, isCharging, offline));
                    Icon old = currentIcon;
                    currentIcon = DrawIcon(level, offline, isCharging);
                    tray.Icon = currentIcon;
                    lastDrawnLevel = level;
                    lastDrawnOffline = offline;
                    lastDrawnCharging = isCharging;
                    if (old != null) { try { old.Dispose(); } catch { } }
                }

                string tip;
                if (level < 0)
                {
                    tip = "雷蛇鼠标：未检测到设备";
                }
                else
                {
                    int remHours = EstimateRemainingHours(level);
                    if (isCharging)
                    {
                        tip = string.Format("{0}: {1}%，正在充电 ⚡", name, level);
                    }
                    else if (offline)
                    {
                        tip = string.Format("{0}: {1}% (休眠中，约可再使用 {2} 小时)", name, level, remHours);
                    }
                    else
                    {
                        // 严格对齐图 2 悬停提示文案格式：BSK V3 PRO: 85%，约可再使用 27 小时
                        tip = string.Format("{0}: {1}%，约可再使用 {2} 小时", name, level, remHours);
                    }
                }

                if (tip.Length > 63) tip = tip.Substring(0, 63);
                try { tray.Text = tip; } catch { }
            }
        }
    }

    // 雷蛇暗黑绿电竞风信息浮层：不抢焦点、不阻塞，鼠标移开 4 秒自动消失
    class InfoPopup : Form
    {
        System.Windows.Forms.Timer lifeTimer;
        int countdown;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                return cp;
            }
        }

        public InfoPopup(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 22, 18); // 雷蛇深邃暗黑绿底色

            Label lbl = new Label();
            lbl.Dock = DockStyle.Fill;
            lbl.Text = text;
            lbl.ForeColor = Color.FromArgb(232, 242, 232);
            lbl.Font = new Font("Microsoft YaHei UI", 9.2F, FontStyle.Regular, GraphicsUnit.Point);
            lbl.TextAlign = ContentAlignment.TopLeft;
            lbl.BackColor = Color.Transparent;
            Controls.Add(lbl);

            Padding = new Padding(18, 14, 18, 14);
            Size ts = TextRenderer.MeasureText(text, lbl.Font,
                new Size(560, 2000), TextFormatFlags.Left | TextFormatFlags.Top);
            ClientSize = new Size(ts.Width + 38, ts.Height + 28);

            Paint += delegate (object sender, PaintEventArgs e)
            {
                using (Pen borderPen = new Pen(Color.FromArgb(68, 214, 44), 1.5f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
                }
            };

            lifeTimer = new System.Windows.Forms.Timer();
            lifeTimer.Interval = 1000;
            lifeTimer.Tick += delegate
            {
                countdown--;
                if (countdown <= 0) Close();
            };

            EventHandler enter = delegate { countdown = 9999; };
            EventHandler leave = delegate { countdown = 2; };
            MouseEnter += enter;
            MouseLeave += leave;
            lbl.MouseEnter += enter;
            lbl.MouseLeave += leave;
            Click += delegate { Close(); };
            lbl.Click += delegate { Close(); };
        }

        public void ShowNear(Point anchor)
        {
            Show();
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X - Width / 2;
            int y = anchor.Y - Height - 16;
            if (y < wa.Top) y = anchor.Y + 20;
            if (x < wa.Left) x = wa.Left + 4;
            if (x + Width > wa.Right) x = wa.Right - Width - 4;
            Location = new Point(x, y);
            countdown = 4;
            lifeTimer.Start();
        }
    }
}
