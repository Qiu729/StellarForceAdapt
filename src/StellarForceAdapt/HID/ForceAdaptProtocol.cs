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

    // ForceAdapt effect types — mapped to SpaceStation UI modes (V2 capture, 2026-05).
    // Byte values correspond directly to A5 data[6] in the vendor protocol.
    public enum ForceAdaptMode : byte
    {
        /// <summary>常规 — No effect / triggers behave like an ordinary analog axis.</summary>
        Off = 0,
        /// <summary>赛车 — Linear damping (params: start-pos, damping-strength).</summary>
        Racing = 1,
        /// <summary>机枪 — Position + vibration burst (params: start-pos, start-strength, strength, frequency).</summary>
        Machinegun = 2,
        /// <summary>狙击 — Breakthrough-style resistance (params: start-pos, break-force, break-travel).</summary>
        Sniper = 3,
        /// <summary>扳机锁 — Lock trigger at start-position (params: start-pos).</summary>
        TriggerLock = 4,
        /// <summary>震动 — Full-travel vibration (params: coefficient, travel, mute-band, frequency).</summary>
        Vibration = 5,

        // Backwards-compatibility aliases for pre-V2 callers. Semantics shifted
        // once the real protocol mode-table was reverse-engineered, so mapping
        // is best-effort only: old "Resistance" is closest to TriggerLock.
        Resistance = TriggerLock,
    }

    /// <summary>
    /// Identifies which physical trigger a ForceAdapt config targets.
    /// Value equals the channel-ID byte used in A4 BEGIN_CFG data[1]
    /// and in the 0x52 haptic packet's first data byte (after mapping 0x0A → 0x02).
    /// </summary>
    public enum TriggerSide : byte
    {
        /// <summary>Left trigger (L2). A4 data[1] = 0x09, 0x52 data[0] = 0x01, A5 data[2] = 0x32.</summary>
        LT = 0x09,
        /// <summary>Right trigger (R2). A4 data[1] = 0x0A, 0x52 data[0] = 0x02, A5 data[2] = 0x00.</summary>
        RT = 0x0A,
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
            /// in the USBPcap capture (cmd=0x11 sub=0x07 data=ff 00 ff ff ff 14 00).
            /// Arms the ForceAdapt channel before a new config session.
            /// </summary>
            public static byte[] BuildSetStatusPrefix() =>
                BuildCmd(CmdQuery, 0x07, [0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0x14, 0x00]);

            /// <summary>
            /// 0x51 sub=0x0A ACTIVATE: tells firmware to APPLY the effect just written via A5.
            /// Captured layout: 01 01 [slotMode] [P0 P1 P2 P3 P4] 00 00  (10 bytes)
            /// where P0..P4 is a verbatim copy of A5 data[16..20].
            /// Without this packet the device only stores the params but never drives the triggers
            /// — which is exactly the symptom we observed (ACKs returned, zero physical effect).
            /// </summary>
            public static byte[] BuildActivateForSlot(int slot)
            {
                var (set23, _) = GetSlot(slot);
                byte mode = set23[6];
                byte p0 = set23[16], p1 = set23[17], p2 = set23[18], p3 = set23[19], p4 = set23[20];
                return BuildCmd(CmdSaveProfile, SubSaveProfile,
                    [0x01, 0x01, mode, p0, p1, p2, p3, p4, 0x00, 0x00]);
            }

            /// <summary>
            /// 0x51 sub=0x0A FINALIZE: always sent immediately after the ACTIVATE packet
            /// in every SpaceStation capture. Payload is constant `01 02 00 00 00 00 00 00 00 00`.
            /// Believed to commit/lock the just-activated effect so it survives the next
            /// polling tick.
            /// </summary>
            public static byte[] BuildFinalizeCommit() =>
                BuildCmd(CmdSaveProfile, SubSaveProfile,
                    [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

            /// <summary>
            /// Full 6-packet activation sequence derived from USBPcap analysis.
            /// This is the MINIMUM set of commands needed to make the trigger actually move:
            ///   ① 0x11 SET_STATUS   ② 0xA4 BEGIN_CFG   ③ 0xA5 SET_EFFECT
            ///   ④ 0xA6 END_CFG      ⑤ 0x51 ACTIVATE    ⑥ 0x51 FINALIZE
            /// Previous 3-packet replays (A4/A5/A6 only) got ACKs but no physical response
            /// because steps ⑤+⑥ were missing — the firmware stored the config but never
            /// applied it to the trigger hardware.
            /// </summary>
            public static byte[][] BuildFullActivationSequence(
                int slot, byte? triggerMap8 = null, byte? triggerMap9 = null)
            {
                var (set23Src, end4) = GetSlot(slot);
                var set23 = (byte[])set23Src.Clone();
                if (triggerMap8.HasValue) set23[8] = triggerMap8.Value;
                if (triggerMap9.HasValue) set23[9] = triggerMap9.Value;

                // Activate payload must be derived from the (possibly mutated) set23
                byte mode = set23[6];
                byte p0 = set23[16], p1 = set23[17], p2 = set23[18], p3 = set23[19], p4 = set23[20];

                return [
                    BuildSetStatusPrefix(),
                    BuildCmd(CmdConfig, SubBeginConfig, BeginData6),
                    BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                    BuildCmd(CmdEndConfig, SubEndConfig, end4),
                    BuildCmd(CmdSaveProfile, SubSaveProfile,
                        [0x01, 0x01, mode, p0, p1, p2, p3, p4, 0x00, 0x00]),
                    BuildFinalizeCommit(),
                ];
            }

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

            // ================================================================
            //  V2 (LT/RT × 6-mode) path — derived from USBPcap capture v2
            //  (spacestation_cmds_v2.txt, 2026-05).
            //
            //  Major findings that make V2 correct where V1 was not:
            //   • A4 BEGIN_CFG data[1] is the TRIGGER CHANNEL ID, not a "both
            //     triggers enabled" mask. LT = 0x09, RT = 0x0A.
            //     data[4] = data[1] | 0xC0 (0xC9 for LT, 0xCA for RT).
            //   • A5 data[2] is the channel marker: 0x32 for LT, 0x00 for RT.
            //   • 0x51 sub=0x0A is TWO independent trigger slots, not
            //     ACTIVATE+FINALIZE:
            //        data[0..1] = 01 01  → LT slot state
            //        data[0..1] = 01 02  → RT slot state
            //     Every config flush sends BOTH packets; the untouched channel
            //     gets a zero-param payload but its currently-cached config
            //     remains live inside the firmware.
            //   • Mode 5 (Vibration) does NOT use 0x51 for its own slot.
            //     Instead it emits a 0x52 sub=0x0B packet carrying the 8-byte
            //     haptic config:
            //        data = [sideByte(01/02), 0x02, <8B haptic>, 0x00]
            //     0x51 for the vibrating channel is still sent, but with an
            //     all-zero payload (the slot is "cleared" on the 0x51 side and
            //     the haptic engine is driven solely via 0x52).
            //   • Mode 0 (Off/常规) uses the same "0a 32 01 01 01 80 00 00"
            //     body bytes as mode 5 but with all params zero; it is a pure
            //     clear-config operation.
            //
            //  The 12 templates below are VERBATIM bytes from the capture
            //  (including the unresolved A5 checksum byte [21] and the A6
            //  CRC bytes [0..2]). Until those algorithms are recovered, only
            //  the captured values are guaranteed to produce an ACK.
            // ================================================================

            /// <summary>A4 BEGIN_CFG data[6] for LT channel (data[1]=0x09).</summary>
            public static readonly byte[] BeginLT = [0x01, 0x09, 0x01, 0x14, 0xC9, 0x00];

            /// <summary>A4 BEGIN_CFG data[6] for RT channel (data[1]=0x0A).</summary>
            public static readonly byte[] BeginRT = [0x01, 0x0A, 0x01, 0x14, 0xCA, 0x00];

            // --- LT templates (A5 data[2]=0x32) ---
            private static readonly byte[] LT_Set_Mode0 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x00, 0x00, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x00,0x00,0x00,0x00,0x00, 0xAD, 0x00];
            private static readonly byte[] LT_End_Mode0  = [0xF7, 0x59, 0xFA, 0x00];

            private static readonly byte[] LT_Set_Mode1 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x01, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x0A,0x1E,0x00,0x00,0x00, 0xD5, 0x00];
            private static readonly byte[] LT_End_Mode1  = [0x5E, 0x14, 0x1C, 0x00];

            private static readonly byte[] LT_Set_Mode2 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x02, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x1E,0x32,0x32,0x11,0x01, 0x42, 0x00];
            private static readonly byte[] LT_End_Mode2  = [0xD6, 0x2E, 0xAE, 0x00];

            private static readonly byte[] LT_Set_Mode3 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x03, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x32,0x3C,0x3C,0x00,0x01, 0x5A, 0x00];
            private static readonly byte[] LT_End_Mode3  = [0x7F, 0xC3, 0xEC, 0x00];

            private static readonly byte[] LT_Set_Mode4 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x04, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x50,0xFF,0x01,0x00,0x00, 0x00, 0x00];
            private static readonly byte[] LT_End_Mode4  = [0xEA, 0xF9, 0x8D, 0x00];

            private static readonly byte[] LT_Set_Mode5 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x05, 0x02, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x01,0x80,0x01,0x5A,0x00, 0x90, 0x00];
            private static readonly byte[] LT_End_Mode5  = [0xA1, 0x84, 0xCF, 0x00];

            // --- RT templates (A5 data[2]=0x00) ---
            private static readonly byte[] RT_Set_Mode0 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x00, 0x00, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x00,0x00,0x00,0x00,0x00, 0x7B, 0x00];
            private static readonly byte[] RT_End_Mode0  = [0x8B, 0xC4, 0xF9, 0x00];

            private static readonly byte[] RT_Set_Mode1 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x01, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x0A,0x1E,0x00,0x00,0x00, 0xA3, 0x00];
            private static readonly byte[] RT_End_Mode1  = [0xF0, 0x1F, 0xB9, 0x00];

            private static readonly byte[] RT_Set_Mode2 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x02, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x1E,0x32,0x32,0x11,0x01, 0x10, 0x00];
            private static readonly byte[] RT_End_Mode2  = [0xB4, 0x53, 0xB1, 0x00];

            private static readonly byte[] RT_Set_Mode3 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x03, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x32,0x3C,0x3C,0x00,0x01, 0x28, 0x00];
            private static readonly byte[] RT_End_Mode3  = [0xD2, 0xF5, 0x71, 0x00];

            private static readonly byte[] RT_Set_Mode4 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x04, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x50,0xFF,0x01,0x00,0x00, 0xCE, 0x00];
            private static readonly byte[] RT_End_Mode4  = [0x64, 0xF2, 0x00, 0x00];

            private static readonly byte[] RT_Set_Mode5 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x05, 0x02, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x01,0x80,0x01,0x5A,0x00, 0x5E, 0x00];
            private static readonly byte[] RT_End_Mode5  = [0x43, 0xCD, 0xBA, 0x00];

            /// <summary>
            /// Look up the (SET_EFFECT data[23], END_CFG data[4]) template pair
            /// for a given (side, mode). Returned arrays are references into
            /// this class — DO NOT mutate; clone first if parameterising.
            /// </summary>
            public static (byte[] set23, byte[] end4) GetTemplate(TriggerSide side, ForceAdaptMode mode)
            {
                return (side, mode) switch
                {
                    (TriggerSide.LT, ForceAdaptMode.Off)         => (LT_Set_Mode0, LT_End_Mode0),
                    (TriggerSide.LT, ForceAdaptMode.Racing)      => (LT_Set_Mode1, LT_End_Mode1),
                    (TriggerSide.LT, ForceAdaptMode.Machinegun)  => (LT_Set_Mode2, LT_End_Mode2),
                    (TriggerSide.LT, ForceAdaptMode.Sniper)      => (LT_Set_Mode3, LT_End_Mode3),
                    (TriggerSide.LT, ForceAdaptMode.TriggerLock) => (LT_Set_Mode4, LT_End_Mode4),
                    (TriggerSide.LT, ForceAdaptMode.Vibration)   => (LT_Set_Mode5, LT_End_Mode5),
                    (TriggerSide.RT, ForceAdaptMode.Off)         => (RT_Set_Mode0, RT_End_Mode0),
                    (TriggerSide.RT, ForceAdaptMode.Racing)      => (RT_Set_Mode1, RT_End_Mode1),
                    (TriggerSide.RT, ForceAdaptMode.Machinegun)  => (RT_Set_Mode2, RT_End_Mode2),
                    (TriggerSide.RT, ForceAdaptMode.Sniper)      => (RT_Set_Mode3, RT_End_Mode3),
                    (TriggerSide.RT, ForceAdaptMode.TriggerLock) => (RT_Set_Mode4, RT_End_Mode4),
                    (TriggerSide.RT, ForceAdaptMode.Vibration)   => (RT_Set_Mode5, RT_End_Mode5),
                    _ => (LT_Set_Mode0, LT_End_Mode0),
                };
            }

            /// <summary>
            /// BEGIN_CFG data[6] appropriate for the given channel.
            /// </summary>
            public static byte[] GetBegin(TriggerSide side) =>
                side == TriggerSide.LT ? BeginLT : BeginRT;

            /// <summary>
            /// Build the V2 apply sequence for a single (side, mode) pair.
            /// Returns 6 packets for modes 0..4, or 7 packets for Vibration (mode 5)
            /// which additionally emits a 0x52 haptic command.
            ///
            ///  ① 0x11 SET_STATUS prefix
            ///  ② 0xA4 BEGIN_CFG  (channel-specific)
            ///  ③ 0xA5 SET_EFFECT (channel + mode template)
            ///  ④ 0xA6 END_CFG    (captured CRC bytes)
            ///  ⑤ 0x51 LT-slot    (data[0..1]=01 01, params or zero)
            ///  ⑥ 0x51 RT-slot    (data[0..1]=01 02, params or zero)
            ///  ⑦ 0x52 haptic     (mode=5 only, carries 8B haptic body)
            /// </summary>
            public static byte[][] BuildApplySequenceV2(TriggerSide side, ForceAdaptMode mode)
            {
                var (set23, end4) = GetTemplate(side, mode);
                var begin = GetBegin(side);

                // Extract params / haptic body from the captured template.
                byte modeByte = set23[6];
                byte p0 = set23[16], p1 = set23[17], p2 = set23[18], p3 = set23[19], p4 = set23[20];
                // Haptic body = data[8..15] (8 bytes) — used only when mode == Vibration.
                byte h0 = set23[8],  h1 = set23[9],  h2 = set23[10], h3 = set23[11];
                byte h4 = set23[12], h5 = set23[13], h6 = set23[14], h7 = set23[15];

                // LT slot (0x51 data[0..1] = 01 01).
                // Carries the LT mode+params when this call targets LT and
                // mode is non-vibration. Otherwise all zero (slot cleared /
                // irrelevant for this flush).
                byte[] ltSlot;
                if (side == TriggerSide.LT && mode != ForceAdaptMode.Vibration)
                    ltSlot = [0x01, 0x01, modeByte, p0, p1, p2, p3, p4, 0x00, 0x00];
                else
                    ltSlot = [0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

                // RT slot (0x51 data[0..1] = 01 02).
                byte[] rtSlot;
                if (side == TriggerSide.RT && mode != ForceAdaptMode.Vibration)
                    rtSlot = [0x01, 0x02, modeByte, p0, p1, p2, p3, p4, 0x00, 0x00];
                else
                    rtSlot = [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

                // Fast path — non-vibration modes just need the six-packet flush.
                if (mode != ForceAdaptMode.Vibration)
                {
                    return [
                        BuildSetStatusPrefix(),
                        BuildCmd(CmdConfig, SubBeginConfig, begin),
                        BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                        BuildCmd(CmdEndConfig, SubEndConfig, end4),
                        BuildCmd(CmdSaveProfile, SubSaveProfile, ltSlot),
                        BuildCmd(CmdSaveProfile, SubSaveProfile, rtSlot),
                    ];
                }

                // Vibration path — the vibrating side's 0x51 slot is cleared and
                // the real haptic body is delivered via 0x52 sub=0x0B.
                byte sideByte = side == TriggerSide.LT ? (byte)0x01 : (byte)0x02;
                byte[] hapticData = [sideByte, 0x02, h0, h1, h2, h3, h4, h5, h6, h7, 0x00];

                return [
                    BuildSetStatusPrefix(),
                    BuildCmd(CmdConfig, SubBeginConfig, begin),
                    BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                    BuildCmd(CmdEndConfig, SubEndConfig, end4),
                    BuildCmd(CmdSaveProfile, SubSaveProfile, ltSlot),
                    BuildCmd(CmdSaveProfile, SubSaveProfile, rtSlot),
                    BuildCmd(0x52, 0x0B, hapticData),
                ];
            }

            /// <summary>
            /// Human-readable name for a packet produced by <see cref="BuildApplySequenceV2"/>,
            /// indexed by position in the returned array.
            /// </summary>
            public static readonly string[] V2PacketNames =
                ["11 STATUS", "A4 BEGIN", "A5 SET", "A6 END", "51 LT", "51 RT", "52 HAPTIC"];
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
