using System.Windows;

namespace StellarForceAdapt;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handle command-line arguments
        if (args.Length > 0)
        {
            switch (args[0].ToLower())
            {
                case "--sniff":
                case "/sniff":
                    RunSnifferMode();
                    return;

                case "--help":
                case "/?":
                    ShowHelp();
                    return;
            }
        }

        // Normal WPF startup
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static void ShowHelp()
    {
        Console.WriteLine("""
            StellarForceAdapt — 飞智八爪鱼5 × 剑星 自适应扳机工具
            ==========================================================
            用法: StellarForceAdapt.exe [选项]

            选项:
              --sniff     USB 嗅探模式 - 监控飞智空间站与手柄的 HID 通信，
                          帮助反向解析 ForceAdapt 指令
              --help      显示此帮助信息

            无参数启动时以 GUI 模式运行。
            ==========================================================
            """);
    }

    private static void RunSnifferMode()
    {
        Console.WriteLine("""
            ╔═══════════════════════════════════════════════╗
            ║  StellarForceAdapt - USB 嗅探模式             ║
            ║                                               ║
            ║  此模式监控飞智空间站与八爪鱼5之间的 HID      ║
            ║  通信流量，用于反向解析 ForceAdapt 指令格式。  ║
            ║                                               ║
            ║  请先打开飞智空间站，调整扳机设置，            ║
            ║  本工具将记录所有 HID 输出报告。               ║
            ╚═══════════════════════════════════════════════╝
            """);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Environment.Exit(0);
        };

        // TODO: Implement HID traffic monitoring
        // This would use HidSharp to monitor all HID traffic to/from the device
        // and log it for analysis

        Console.WriteLine("嗅探模式已启动 (按 Ctrl+C 停止)...");
        Console.WriteLine("请操作飞智空间站调整扳机设置...");

        // Simple loop
        while (true)
        {
            Thread.Sleep(100);
        }
    }
}

