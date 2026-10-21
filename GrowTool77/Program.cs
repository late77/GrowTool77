using GrowTool77.Aura;
using GrowTool77.Forms;
using GrowTool77.Modules;

namespace GrowTool77;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Short-lived elevated helper started by [AURA] to edit the hosts file.
        // It shows no window and exits immediately.
        if (args.Length == 2 && args[0] == HostsFile.ElevatedArg)
            return HostsFile.Apply(args[1]);

        ApplicationConfiguration.Initialize();
        Theme.Init();
        Application.Run(new MainForm());
        return 0;
    }
}
