// Controller Setup: installs Controller.exe (embedded in this file as a resource) for the current user, asks about the desktop shortcut,
// and registers an uninstall entry. Run again to update; the same file (copied to the install folder as uninstall.exe) removes it.
// Build: build_setup.bat (needs Controller.exe, app.ico and logo.png next to this file).
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ControllerSetup
{
    sealed class Options
    {
        public bool Uninstall, Silent, Desktop = true, StartMenu = true, Launch = true, KeepData = true;
        public string Dir;
    }

    static class Core
    {
        public const string Version = "2.6.0";            // the version of the embedded Controller.exe
        public const string ExeName = "Controller.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Controller";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Controller"); }
        }

        public static string InstalledDir()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    if (k != null) { object v = k.GetValue("InstallLocation"); if (v != null && Directory.Exists(v.ToString())) return v.ToString(); }
            }
            catch (Exception) { }
            return null;
        }

        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Controller.lnk"); } }
        static string MenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Controller.lnk"); } }

        // the program may run hidden in the tray: close it, then end it, so its file can be replaced
        static void StopProgram(string dir)
        {
            foreach (Process p in Process.GetProcessesByName("Controller"))
            {
                try
                {
                    string path = null;
                    try { path = p.MainModule.FileName; } catch (Exception) { }
                    if (path != null && dir != null && !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;   // another copy elsewhere: leave it
                    p.CloseMainWindow();
                    if (!p.WaitForExit(2500)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch (Exception) { }
            }
        }

        static void MakeLink(string link, string target, string dir)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { link });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Controller" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        public static void Install(Options o, Action<string, int> progress)
        {
            string dir = o.Dir;
            progress("Closing the program if it is running...", 8);
            StopProgram(dir);

            progress("Copying the program...", 30);
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, ExeName);
            using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("Controller.exe"))
            using (FileStream fs = new FileStream(exe, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[81920]; int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }
            string self = Assembly.GetExecutingAssembly().Location;
            string un = Path.Combine(dir, "uninstall.exe");
            if (!string.Equals(self, un, StringComparison.OrdinalIgnoreCase)) File.Copy(self, un, true);

            progress("Creating shortcuts...", 65);
            // an update replaces the files but the shortcuts keep pointing at the same place; they are only (re)made or removed by the choices
            if (o.Desktop) MakeLink(DesktopLink, exe, dir); else TryDelete(DesktopLink);
            if (o.StartMenu) MakeLink(MenuLink, exe, dir); else TryDelete(MenuLink);

            progress("Registering the program...", 85);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Controller");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "abo5lood10");
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + un + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)((new FileInfo(exe).Length + new FileInfo(un).Length) / 1024), RegistryValueKind.DWord);
            }
            progress("Done.", 100);
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { } }

        public static void Uninstall(Options o, Action<string, int> progress)
        {
            string dir = o.Dir ?? InstalledDir() ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            progress("Closing the program if it is running...", 10);
            StopProgram(dir);
            progress("Removing shortcuts...", 35);
            TryDelete(DesktopLink); TryDelete(MenuLink);
            progress("Removing the program...", 60);
            TryDelete(Path.Combine(dir, ExeName)); TryDelete(Path.Combine(dir, ExeName + ".new"));
            if (!o.KeepData)
            {
                TryDelete(Path.Combine(dir, "settings.ini"));
                try { string sd = Path.Combine(dir, "scripts"); if (Directory.Exists(sd)) Directory.Delete(sd, true); } catch (Exception) { }
            }
            progress("Cleaning up...", 85);
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) { k.DeleteValue("AL-SeRry", false); k.DeleteValue("Controller", false); } } } catch (Exception) { }
            try { Registry.CurrentUser.DeleteSubKey(UninstallKey, false); } catch (Exception) { }
            // this very file cannot delete itself while it runs: a hidden command does it a moment later
            string un = Path.Combine(dir, "uninstall.exe");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + un + "\" & rmdir \"" + dir + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception) { }
            progress("Done.", 100);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Options o = new Options();
            string self = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
            if (self.Equals("uninstall", StringComparison.OrdinalIgnoreCase)) o.Uninstall = true;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--uninstall") o.Uninstall = true;
                else if (a == "--silent") o.Silent = true;
                else if (a == "--nodesktop") o.Desktop = false;
                else if (a == "--nomenu") o.StartMenu = false;
                else if (a == "--nolaunch") o.Launch = false;
                else if (a == "--deletedata") o.KeepData = false;
                else if (a == "--dir" && i + 1 < args.Length) o.Dir = args[++i];
            }
            if (o.Dir == null) o.Dir = o.Uninstall ? Core.InstalledDir() : (Core.InstalledDir() ?? Core.DefaultDir);
            if (o.Silent)
            {
                try
                {
                    if (o.Uninstall) Core.Uninstall(o, delegate { }); else Core.Install(o, delegate { });
                    if (!o.Uninstall && o.Launch) Process.Start(Path.Combine(o.Dir, Core.ExeName));
                    return 0;
                }
                catch (Exception) { return 1; }
            }
            Application app = new Application();
            return app.Run(new Wizard(o).Win);
        }
    }

    // the window: dark green glass like the program itself
    sealed class Wizard
    {
        public readonly Window Win;
        readonly Options o;
        readonly bool update;
        FrameworkElement Get(string n) { return (FrameworkElement)Win.FindName(n); }

        const string Xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
  Title='Controller Setup' Width='580' Height='470' WindowStyle='None' ResizeMode='NoResize' WindowStartupLocation='CenterScreen'
  FontFamily='Bahnschrift, Segoe UI' FontSize='14' Foreground='#E2F0E6'>
  <Window.Background>
    <LinearGradientBrush StartPoint='0.27,0.06' EndPoint='0.73,0.94'><GradientStop Color='#345240' Offset='0'/><GradientStop Color='#0F2116' Offset='1'/></LinearGradientBrush>
  </Window.Background>
  <Window.Resources>
    <Style TargetType='TextBlock'><Setter Property='Foreground' Value='#E2F0E6'/></Style>
    <Style x:Key='Muted' TargetType='TextBlock'><Setter Property='Foreground' Value='#7EA08E'/></Style>
    <Style x:Key='Btn' TargetType='Button'>
      <Setter Property='Foreground' Value='#E2F0E6'/><Setter Property='Padding' Value='24,10'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='bd' CornerRadius='14' Background='{TemplateBinding Background}' BorderBrush='#55FFFFFF' BorderThickness='1' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#AAFFFFFF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style TargetType='CheckBox'>
      <Setter Property='Foreground' Value='#E2F0E6'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Margin' Value='0,0,0,12'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <StackPanel Orientation='Horizontal'>
            <Border x:Name='box' Width='24' Height='24' CornerRadius='8' Background='#22FFFFFF' BorderBrush='#66FFFFFF' BorderThickness='1.5' VerticalAlignment='Center'>
              <Path x:Name='tick' Data='M 5,12 L 10,17 L 19,6' Stroke='White' StrokeThickness='2.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Visibility='Collapsed'/>
            </Border>
            <ContentPresenter Margin='12,0,0,0' VerticalAlignment='Center'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'><Setter TargetName='box' Property='Background' Value='#34C759'/><Setter TargetName='box' Property='BorderBrush' Value='#8034C759'/><Setter TargetName='tick' Property='Visibility' Value='Visible'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
  </Window.Resources>
  <Border BorderBrush='#5534C759' BorderThickness='1.5'>
    <Grid Margin='34,28,34,28'>
      <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
      <StackPanel Orientation='Horizontal' Margin='0,0,0,20'>
        <Image x:Name='imgLogo' Width='64' Height='64' Margin='0,0,16,0'/>
        <StackPanel VerticalAlignment='Center'>
          <TextBlock x:Name='txtTitle' FontSize='26' FontWeight='SemiBold'/>
          <TextBlock x:Name='txtSub' Style='{StaticResource Muted}' Margin='0,2,0,0'/>
        </StackPanel>
      </StackPanel>

      <StackPanel x:Name='pgOptions' Grid.Row='1'>
        <TextBlock Text='Install folder' Style='{StaticResource Muted}' Margin='0,0,0,6'/>
        <Grid Margin='0,0,0,20'>
          <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <TextBox x:Name='txtDir' Background='#33000000' Foreground='#E2F0E6' BorderBrush='#5534C759' BorderThickness='1.5' Padding='10,8' CaretBrush='White'/>
          <Button x:Name='btnBrowse' Grid.Column='1' Content='Browse...' Style='{StaticResource Btn}' Background='#26FFFFFF' Padding='16,8' Margin='10,0,0,0'/>
        </Grid>
        <CheckBox x:Name='chkDesktop' Content='Create a shortcut on the desktop'/>
        <CheckBox x:Name='chkMenu' Content='Create a Start menu shortcut'/>
        <CheckBox x:Name='chkLaunch' Content='Start Controller when finished'/>
      </StackPanel>

      <StackPanel x:Name='pgRemove' Grid.Row='1' Visibility='Collapsed'>
        <TextBlock Text='This removes Controller from this computer.' TextWrapping='Wrap' Margin='0,0,0,18'/>
        <CheckBox x:Name='chkData' Content='Also delete my settings and scripts'/>
      </StackPanel>

      <StackPanel x:Name='pgProgress' Grid.Row='1' Visibility='Collapsed' VerticalAlignment='Center'>
        <TextBlock x:Name='txtStatus' FontSize='16' Margin='0,0,0,16'/>
        <Border Height='12' CornerRadius='6' Background='#26FFFFFF'>
          <Border x:Name='barFill' HorizontalAlignment='Left' Width='0' CornerRadius='6' Background='#34C759'/>
        </Border>
      </StackPanel>

      <StackPanel Grid.Row='2' Orientation='Horizontal' HorizontalAlignment='Right'>
        <Button x:Name='btnCancel' Content='Cancel' Style='{StaticResource Btn}' Background='#26FFFFFF' Margin='0,0,10,0'/>
        <Button x:Name='btnGo' Style='{StaticResource Btn}' Background='#2AAC47'/>
      </StackPanel>
    </Grid>
  </Border>
</Window>";

        public Wizard(Options options)
        {
            o = options;
            update = !o.Uninstall && Core.InstalledDir() != null;
            Win = (Window)XamlReader.Parse(Xaml);
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                {
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit(); bi.StreamSource = s; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                    ((Image)Get("imgLogo")).Source = bi;
                }
            }
            catch (Exception) { }
            Win.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed && !(e.OriginalSource is TextBox)) try { Win.DragMove(); } catch (Exception) { } };
            Win.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(Win).Handle;
                int on = 1, round = 2;
                DwmSetWindowAttribute(h, 20, ref on, 4);
                DwmSetWindowAttribute(h, 33, ref round, 4);
            };

            Button go = (Button)Get("btnGo"), cancel = (Button)Get("btnCancel");
            TextBlock title = (TextBlock)Get("txtTitle"), sub = (TextBlock)Get("txtSub");
            if (o.Uninstall)
            {
                title.Text = "Remove Controller";
                sub.Text = "Version " + Core.Version;
                Get("pgOptions").Visibility = Visibility.Collapsed;
                Get("pgRemove").Visibility = Visibility.Visible;
                go.Content = "Remove";
            }
            else
            {
                title.Text = update ? "Update Controller" : "Install Controller";
                sub.Text = "Version " + Core.Version;
                ((TextBox)Get("txtDir")).Text = o.Dir;
                ((CheckBox)Get("chkDesktop")).IsChecked = o.Desktop;
                ((CheckBox)Get("chkMenu")).IsChecked = o.StartMenu;
                ((CheckBox)Get("chkLaunch")).IsChecked = o.Launch;
                go.Content = update ? "Update" : "Install";
                if (update) ((TextBox)Get("txtDir")).IsEnabled = false;   // an update goes where it already is
            }
            ((Button)Get("btnBrowse")).Click += delegate
            {
                using (System.Windows.Forms.FolderBrowserDialog d = new System.Windows.Forms.FolderBrowserDialog())
                {
                    d.Description = "Choose the install folder";
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) ((TextBox)Get("txtDir")).Text = Path.Combine(d.SelectedPath, "Controller");
                }
            };
            ((Button)Get("btnBrowse")).IsEnabled = !update;
            cancel.Click += delegate { Win.Close(); };
            go.Click += delegate
            {
                if (finished)
                {
                    if (!o.Uninstall && o.Launch) { try { Process.Start(Path.Combine(o.Dir, Core.ExeName)); } catch (Exception) { } }
                    Win.Close();
                    return;
                }
                Start(go, cancel);
            };
        }

        bool finished;   // the work is done: the main button now closes the window (and starts the program)

        void Start(Button go, Button cancel)
        {
            if (!o.Uninstall)
            {
                o.Dir = ((TextBox)Get("txtDir")).Text.Trim();
                if (o.Dir.Length == 0) return;
                o.Desktop = ((CheckBox)Get("chkDesktop")).IsChecked == true;
                o.StartMenu = ((CheckBox)Get("chkMenu")).IsChecked == true;
                o.Launch = ((CheckBox)Get("chkLaunch")).IsChecked == true;
            }
            else o.KeepData = ((CheckBox)Get("chkData")).IsChecked != true;
            Get("pgOptions").Visibility = Visibility.Collapsed;
            Get("pgRemove").Visibility = Visibility.Collapsed;
            Get("pgProgress").Visibility = Visibility.Visible;
            go.IsEnabled = false; cancel.IsEnabled = false;
            Border fill = (Border)Get("barFill");
            TextBlock status = (TextBlock)Get("txtStatus");
            Thread t = new Thread(delegate ()
            {
                string error = null;
                try
                {
                    Action<string, int> prog = delegate (string text, int pct)
                    {
                        Win.Dispatcher.BeginInvoke(new Action(delegate { status.Text = text; fill.Width = 512.0 * pct / 100.0; }));
                        Thread.Sleep(180);   // so each step can be read
                    };
                    if (o.Uninstall) Core.Uninstall(o, prog); else Core.Install(o, prog);
                }
                catch (Exception ex) { error = ex.Message; }
                Win.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (error != null)
                    {
                        status.Text = "Something went wrong: " + error;
                        cancel.Content = "Close"; cancel.IsEnabled = true;
                        return;
                    }
                    status.Text = o.Uninstall ? "Controller was removed." : (update ? "Controller was updated." : "Controller is installed.");
                    go.Content = o.Uninstall ? "Close" : (o.Launch ? "Finish and start" : "Finish");
                    go.IsEnabled = true;
                    finished = true;
                    cancel.Visibility = Visibility.Collapsed;
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
