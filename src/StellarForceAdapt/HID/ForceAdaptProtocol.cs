namespace StellarForceAdapt.HID;

/// <summary>
/// FlyDigi ForceAdapt trigger protocol definitions.
/// Based on reverse engineering of FlyDigi Space Station + SDL hidapi_flydigi.c + Vader4ProReader.
/// </summary>
public static class ForceAdaptProtocol
{
    // FlyDigi vendor ID (actual device reports 0x37D7)
    public const int VendorId = 0x37D7;

    // Known FlyDigi product IDs
    public static readonly int[] KnownProductIds =
    [
        0x2501, // Apex 5 / 八爪鱼5 (confirmed)
        0x2012, // Vader 4 Pro
        0x2021, // Apex 4
        0x2023, // Apex 4 (alt)
        0x2011, // Vader 3 Pro
        0x2010, // Vader 3
    ];

    // HID report sizes (uses CD2 interface which is accessible alongside SpaceStationService)
    public const int OutputReportLength = 65;
    public const int InputReportLength = 65;
    // Alternative report lengths for the locked mi_02 interface
    public const int Mi02OutputReportLength = 32;
    public const int Mi02InputReportLength = 32;

    // Report IDs
    public const byte ReportIdRumble = 0x05;
    public const byte ReportIdForceAdapt = 0x06;

    // Sub-commands for report 0x05
    public const byte SubCmdRumble = 0x0f;

    // ForceAdapt effect types (mapped from trigger.ini Mode values)
    public enum ForceAdaptMode : byte
    {
        /// <summary>No effect / passthrough</summary>
        Off = 0,
        /// <summary>Mechanical resistance / trigger stop</summary>
        Resistance = 1,
        /// <summary>Trigger vibration</summary>
        Vibration = 2,
    }

    /// <summary>
    /// SpaceStation vendor protocol command types (Report 0x03, magic 0x5AA5).
    /// Command framework based on libsdl-org/SDL SDL_hidapi_flydigi.c (FLYDIGI_V2_*).
    /// NOTE (2026-05): The cmd=0xA4/0xA5/0xA6 "BeginConfig/SetEffect/EndConfig" set below
    /// was inferred from older firmware leaks and does NOT match real SpaceStation traffic.
    /// Real SpaceStation USB capture shows it only uses cmd=0x01/0x02/0x04/0xA1/0x51 over
    /// Report 0x03 (single report, URB OUT). Report 0x04 is the device's INTERRUPT IN ACK,
    /// not a command the host sends. The old 0xA4/0xA5/0xA6 path below is retained only
    /// because it coincidentally still lights LT on current firmware; RT does not respond.
    /// The correct ForceAdapt command set is pending re-derivation from USBPcap.
    /// </summary>
    public static class VendorProtocol
    {
        public const byte ReportId = 0x03;
        public static readonly byte[] Magic = [0x5A, 0xA5];

        // V2 protocol command IDs (from SDL_hidapi_flydigi.c, FLYDIGI_V2_* constants)
        public const byte CmdGetInfo     = 0x01; // FLYDIGI_V2_GET_INFO_COMMAND
        public const byte CmdHeartbeat   = 0x10; // FLYDIGI_V2_GET_STATUS_COMMAND (keepalive/status)
        public const byte CmdQuery       = 0x11; // FLYDIGI_V2_SET_STATUS_COMMAND
        public const byte CmdHaptic      = 0x12; // FLYDIGI_V2_HAPTIC_COMMAND
        public const byte CmdAcquire     = 0x1C; // FLYDIGI_V2_ACQUIRE_CONTROLLER_COMMAND

        // Legacy/partial ForceAdapt command group (offset 3). Only LT reliably reacts.
        public const byte CmdConfig      = 0xA4; // Begin config session
        public const byte CmdSetEffect   = 0xA5; // Set ForceAdapt effect
        public const byte CmdEndConfig   = 0xA6; // End config session
        public const byte CmdSaveProfile = 0x51; // Save to device memory

        // Sub-commands (byte at offset 4) — Report 0x03 legacy path
        public const byte SubBeginConfig    = 0x06;
        public const byte SubSetForceAdapt  = 0x17;
        public const byte SubEndConfig      = 0x04;
        public const byte SubSaveProfile    = 0x0A;

        /// <summary>
        /// Build a 32-byte vendor command: [ReportId, Magic(2), Cmd, Sub, Data(27)]
        /// </summary>
        private static byte[] BuildCmd(byte cmd, byte sub, byte[] data)
        {
            var buf = new byte[32];
            buf[0] = ReportId;
            buf[1] = Magic[0];
            buf[2] = Magic[1];
            buf[3] = cmd;
            buf[4] = sub;
            if (data != null && data.Length > 0)
                Array.Copy(data, 0, buf, 5, Math.Min(data.Length, 27));
            return buf;
        }

        /// <summary>
        /// Build the 27-byte data payload for a SetEffect command.
        /// Shared between Report 03 and Report 04 variants.
        /// </summary>
        public static byte[] BuildSetEffectData(byte mode, byte intensity, byte[]? customData)
        {
            var data = new byte[27];
            data[0] = 0x00; data[1] = 0x00; data[2] = 0x32; data[3] = 0x00;
            data[4] = 0x00; data[5] = 0x00;
            data[6] = mode;
            data[7] = 0x00;
            data[8] = 0x0A; data[9] = 0x0A;  // trigger mapping (both)
            data[10] = intensity;
            data[11] = 0x01; data[12] = 0xFF; // range
            data[13] = 0x46; data[14] = 0x00; data[15] = 0x00; // constants

            if (customData != null && customData.Length >= 6)
            {
                Array.Copy(customData, 0, data, 16, 6);
            }
            else
            {
                byte[] defaults = mode switch
                {
                    1 => [0x0A, 0x1E, 0x00, 0x00, 0x00, 0xD5],
                    2 => [0x1E, 0x32, 0x32, 0x11, 0x01, 0x42],
                    3 => [0x32, 0x3C, 0x3C, 0x00, 0x01, 0x5A],
                    _ => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
                };
                Array.Copy(defaults, 0, data, 16, 6);
            }
            return data;
        }

        /// <summary>
        /// Query trigger status (cmd=0x11).
        /// </summary>
        public static byte[] BuildQuery() =>
            BuildCmd(CmdQuery, 0x07, [0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0x14]);

        /// <summary>
        /// Begin configuration session — Report 0x03 (cmd=0xA4, sub=0x06).
        /// </summary>
        public static byte[] BuildBeginConfig() =>
            BuildCmd(CmdConfig, SubBeginConfig, [0x01, 0x09, 0x01, 0x14, 0xC9]);

        /// <summary>
        /// Set ForceAdapt trigger effect — Report 0x03 (cmd=0xA5, sub=0x17).
        /// </summary>
        public static byte[] BuildSetEffect(byte mode, byte intensity = 100, byte[]? customData = null)
        {
            var data = BuildSetEffectData(mode, intensity, customData);
            return BuildCmd(CmdSetEffect, SubSetForceAdapt, data);
        }

        /// <summary>
        /// Set ForceAdapt trigger effect using a pre-built data payload.
        /// Allows callers to modify individual bytes (e.g., trigger mapping).
        /// </summary>
        public static byte[] BuildDirectSetEffect(byte[] data27)
        {
            return BuildCmd(CmdSetEffect, SubSetForceAdapt, data27);
        }

        /// <summary>
        /// End configuration session — Report 0x03 (cmd=0xA6, sub=0x04).
        /// </summary>
        public static byte[] BuildEndConfig(uint sequence = 0xD4007E) =>
            BuildCmd(CmdEndConfig, SubEndConfig, [
                (byte)(sequence & 0xFF),
                (byte)((sequence >> 8) & 0xFF),
                (byte)((sequence >> 16) & 0xFF)]);

        /// <summary>
        /// Save current config to device profile (cmd=0x51, sub=0x0A).
        /// </summary>
        public static byte[] BuildSaveProfile(byte mode, byte[]? modeData = null)
        {
            var data = new byte[27];
            data[0] = 0x01; data[1] = 0x01; data[2] = mode;
            if (modeData != null && modeData.Length > 0)
                Array.Copy(modeData, 0, data, 3, Math.Min(modeData.Length, 6));
            return BuildCmd(CmdSaveProfile, SubSaveProfile, data);
        }

        /// <summary>
        /// Build a complete ForceAdapt sequence for a single effect apply (Report 0x03 only).
        /// Known limitation: only LT reliably responds on current firmware (see class remarks).
        /// </summary>
        public static byte[][] BuildApplySequence(byte mode, byte intensity = 100, byte[]? customData = null)
        {
            return [
                BuildBeginConfig(),
                BuildSetEffect(mode, intensity, customData),
                BuildEndConfig(),
            ];
        }

        /// <summary>
        /// Convert a 32-byte vendor command to 65-byte CD2 format (padding).
        /// </summary>
        public static byte[] To65ByteReport(byte[] vendorCmd32)
        {
            var report = new byte[65];
            Array.Copy(vendorCmd32, 0, report, 0, Math.Min(vendorCmd32.Length, 32));
            return report;
        }

        /// <summary>
        /// Byte-exact replay of SpaceStation's A4/A5/A6 triplets from USBPcap capture.
        /// Purpose: verify whether the device (1) responds RT at all, (2) enforces CRC on A6.
        /// If any slot produces a physical RT reaction, the protocol & CRC are accepted as-is
        /// and we can parameterize [6] / [16..20] while keeping the captured checksum bytes.
        /// </summary>
        public static class CapturedReplay
        {
            // All captured sequences share this BEGIN_CFG (A4 sub=0x06, data[6]).
            public static readonly byte[] BeginData6 =
                [0x01, 0x09, 0x01, 0x14, 0xC9, 0x00];

            // SET_EFFECT data[23] for each slot value seen in the capture.
            // Taken verbatim from spacestation_cmds.txt — do NOT modify bytes.
            public static readonly byte[] Slot1SetEffect23 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x01, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x0A,0x1E,0x00,0x00,0x00, 0xD5, 0x00];
            public static readonly byte[] Slot2SetEffect23 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x02, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x1E,0x32,0x32,0x11,0x01, 0x42, 0x00];
            public static readonly byte[] Slot3SetEffect23 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x03, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x32,0x3C,0x3C,0x00,0x01, 0x5A, 0x00];
            public static readonly byte[] Slot4SetEffect23 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x04, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x50,0xFF,0x01,0x00,0x00, 0x00, 0x00];

            // END_CFG data[4] for each slot (first 3 bytes = CRC, last = 0x00).
            public static readonly byte[] Slot1EndData4 = [0xD1, 0x5F, 0xDA, 0x00];
            public static readonly byte[] Slot2EndData4 = [0xD4, 0x00, 0x7E, 0x00];
            public static readonly byte[] Slot3EndData4 = [0xEF, 0x9F, 0x38, 0x00];
            public static readonly byte[] Slot4EndData4 = [0x4F, 0xC8, 0xC1, 0x00];

            /// <summary>
            /// Pick the (setEffect23, end4) pair for a given slot id (1..4).
            /// </summary>
            public static (byte[] set, byte[] end) GetSlot(int slot) => slot switch
            {
                1 => (Slot1SetEffect23, Slot1EndData4),
                2 => (Slot2SetEffect23, Slot2EndData4),
                3 => (Slot3SetEffect23, Slot3EndData4),
                4 => (Slot4SetEffect23, Slot4EndData4),
                _ => (Slot2SetEffect23, Slot2EndData4),
            };

            /// <summary>
            /// Build the three 32-byte reports needed to replay one captured sequence.
            /// </summary>
            public static byte[][] BuildSequence(int slot)
            {
                var (set23, end4) = GetSlot(slot);
                return [
                    BuildCmd(CmdConfig, SubBeginConfig, BeginData6),
                    BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                    BuildCmd(CmdEndConfig, SubEndConfig, end4),
                ];
            }

            /// <summary>
            /// SET_STATUS prefix that precedes every SpaceStation A4/A5/A6 triplet
            /// in the USBPcap capture (cmd=0x11 sub=0x07 data=ff 00 ff ff ff 14).
            /// Suspected to arm/enable the ForceAdapt channel (especially RT).
            /// </summary>
            public static byte[] BuildSetStatusPrefix() =>
                BuildCmd(CmdQuery, 0x07, [0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0x14]);

            /// <summary>
            /// Full replay with SET_STATUS prefix + optional trigger-mapping override.
            /// Default captured values are data[8]=0x0A (LT map), data[9]=0x0A (RT map).
            /// Setting either to 0x00 should isolate the other trigger if our hypothesis
            /// about SDL hidapi_flydigi.c's trigger mapping bytes is correct.
            /// </summary>
            public static byte[][] BuildSequenceWithPrefix(
                int slot, byte? triggerMap8 = null, byte? triggerMap9 = null)
            {
                var (set23Src, end4) = GetSlot(slot);
                // Copy so we don't mutate the readonly capture data.
                var set23 = (byte[])set23Src.Clone();
                if (triggerMap8.HasValue) set23[8] = triggerMap8.Value;
                if (triggerMap9.HasValue) set23[9] = triggerMap9.Value;

                return [
                    BuildSetStatusPrefix(),
                    BuildCmd(CmdConfig, SubBeginConfig, BeginData6),
                    BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                    BuildCmd(CmdEndConfig, SubEndConfig, end4),
                ];
            }
        }
    }

    // Resistance sub-types
    public enum ResistanceType : byte
    {
        PushBack = 0,    // Trigger pushes back against finger
        LockHalf = 1,    // Locks at halfway point
        LockBottom = 2,  // Locks near bottom
        LockTop = 3,     // Locks near top
        Custom = 4,      // Custom resistance curve
    }

    // Vibration sub-types
    public enum VibrationType : byte
    {
        TopHard = 0,
        TopSoft = 1,
        HalfHard = 2,
        HalfSoft = 3,
        BottomHard = 4,
        BottomSoft = 5,
        Continuous = 6,
        Pulse = 7,
    }

    /// <summary>
    /// Build a rumble command (known working on all FlyDigi controllers with trigger motors).
    /// Report 0x05, sub-command 0x0f.
    /// </summary>
    public static byte[] BuildRumbleCommand(
        byte leftMainRumble = 0,
        byte rightMainRumble = 0,
        byte leftTriggerRumble = 0,
        byte rightTriggerRumble = 0)
    {
        var buf = new byte[OutputReportLength];
        buf[0] = ReportIdRumble;
        buf[1] = SubCmdRumble;
        buf[2] = leftMainRumble;
        buf[3] = rightMainRumble;
        buf[4] = leftTriggerRumble;
        buf[5] = rightTriggerRumble;
        // Rest stays 0
        return buf;
    }

    /// <summary>
    /// Build a ForceAdapt effect command.
    /// Based on analysis of trigger.ini parameters and Space Station USB traffic.
    /// Report 0x06 with effect parameters.
    /// </summary>
    public static byte[] BuildForceAdaptCommand(
        ForceAdaptMode mode,
        byte triggerPosition = 0,   // 0-255 where effect activates
        byte intensity = 0,         // 0-255 effect strength
        byte speed = 0,             // 0-255 effect speed/frequency
        byte flags = 0)             // Flags (left=0x01, right=0x02, both=0x03)
    {
        var buf = new byte[OutputReportLength];
        buf[0] = ReportIdForceAdapt;
        buf[1] = (byte)mode;
        buf[2] = triggerPosition;
        buf[3] = intensity;
        buf[4] = speed;
        buf[5] = flags;
        return buf;
    }

    /// <summary>
    /// Build a command from a trigger.ini style profile entry.
    /// </summary>
    public static byte[] BuildFromTriggerIni(string sectionName, int mode, int param1, int param2, int param3, int param4)
    {
        if (mode == 1) // Resistance mode
        {
            return BuildForceAdaptCommand(
                ForceAdaptMode.Resistance,
                triggerPosition: (byte)Math.Clamp(param1, 0, 255),
                intensity: (byte)Math.Clamp(param2, 0, 255),
                speed: (byte)Math.Clamp(param3, 0, 255),
                flags: (byte)Math.Clamp(param4, 0, 3));
        }
        else if (mode == 2) // Vibration mode
        {
            return BuildForceAdaptCommand(
                ForceAdaptMode.Vibration,
                triggerPosition: (byte)Math.Clamp(param1, 0, 255),
                intensity: (byte)Math.Clamp(param2, 0, 255),
                speed: (byte)Math.Clamp(param3, 0, 255),
                flags: (byte)Math.Clamp(param4, 0, 3));
        }

        // Off / passthrough
        return BuildRumbleCommand();
    }
}
