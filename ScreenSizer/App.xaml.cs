using System.IO;
using System.Windows;

namespace ScreenSizer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenSizer", "crash.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, e.Exception.ToString());
            }
            catch { }
            MessageBox.Show($"出错了：{e.Exception.Message}\n详情已写入 {path}", "ScreenSizer");
            e.Handled = true;
        };
    }
}
