using System.Windows;

namespace StellarForceAdapt;

public partial class App : Application
{
    private const string MutexName = "StellarForceAdapt_SingleInstance";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
        var mutex = new System.Threading.Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("StellarForceAdapt 已在运行中", "StellarForceAdapt",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
