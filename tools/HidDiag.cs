using HidSharp;

Console.WriteLine("=== Extended Button Test ===\n");

var ig01 = DeviceList.Local.GetHidDevices()
    .FirstOrDefault(d => d.DevicePath.Contains("ig_01") && d.VendorID == 0x37D7);
if (ig01 == null) { Console.WriteLine("NOT FOUND"); return; }

using var stream = ig01.Open();
stream.ReadTimeout = 100;

// Wait for idle
byte[] idle = null!;
for (int i = 0; i < 30; i++)
{
    var buf = new byte[ig01.GetMaxInputReportLength()];
    try { int r = stream.Read(buf, 0, buf.Length); if (r > 0) idle = buf; }
    catch (TimeoutException) { }
}
Console.WriteLine($"Idle: {BitConverter.ToString(idle)}\n");
Console.WriteLine("Now press and release A, B, X, Y one at a time (10 seconds)...\n");

DateTime start = DateTime.UtcNow;
byte[] prev = (byte[])idle.Clone();
int changes = 0;
while ((DateTime.UtcNow - start).TotalSeconds < 12 && changes < 100)
{
    var buf = new byte[ig01.GetMaxInputReportLength()];
    try
    {
        int read = stream.Read(buf, 0, buf.Length);
        if (read > 0 && !buf.SequenceEqual(prev))
        {
            double t = (DateTime.UtcNow - start).TotalSeconds;
            var diff = new List<string>();
            for (int i = 0; i < read; i++)
                if (buf[i] != prev[i])
                    diff.Add($"B{i}:{prev[i]:X2}→{buf[i]:X2}");
            Console.WriteLine($"[{t:F1}s] {string.Join(", ", diff)}");
            prev = (byte[])buf.Clone();
            changes++;
        }
    }
    catch (TimeoutException) { }
}

Console.WriteLine("\nDone.");
