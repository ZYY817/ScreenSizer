using System.IO;
using System.Windows;

namespace ScreenSizer;

public partial class App : Application
{
    private bool _crashShown;

    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            var path = ScreenSizer.Services.AppPaths.File("crash.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, e.Exception.ToString());
            }
            catch { }
            if (!_crashShown)
            {
                _crashShown = true;
                MessageBox.Show($"出错了：{e.Exception.Message}\n详情已写入 {path}", "AimFrame 准框");
            }
            e.Handled = true;
        };
    }
}
