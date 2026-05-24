using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;

namespace CardioSimulator.Launcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        
        Form picker = new Form
        {
            Text = "CardioSimulatorWin - Setup",
            Size = new Size(400, 300),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false
        };

        Label title = new Label
        {
            Text = "Select Installation Language\nВыберите язык установки",
            Bounds = new Rectangle(20, 20, 360, 60),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12, FontStyle.Bold)
        };
        picker.Controls.Add(title);

        int btnWidth = 150;
        int btnHeight = 40;
        int startX = 40;
        int startY = 100;

        AddLanguageButton(picker, "English", "1033", startX, startY);
        AddLanguageButton(picker, "Русский", "1049", startX + btnWidth + 20, startY);
        AddLanguageButton(picker, "简体中文", "2052", startX, startY + btnHeight + 20);
        AddLanguageButton(picker, "Español", "3082", startX + btnWidth + 20, startY + btnHeight + 20);

        Application.Run(picker);
    }

    static void AddLanguageButton(Form form, string text, string lcid, int x, int y)
    {
        Button btn = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, 150, 40),
            Tag = lcid,
            Font = new Font("Segoe UI", 10)
        };
        btn.Click += (s, e) => LaunchSetup(lcid);
        form.Controls.Add(btn);
    }

    static void LaunchSetup(string lcid)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CardioSimulatorWin_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string setupExe = Path.Combine(tempDir, "setup_internal.exe");

            var assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream("CardioSimulator.Launcher.setup.bin"))
            {
                if (input == null) throw new Exception("Setup payload not found.");
                using (Stream output = File.Create(setupExe))
                {
                    input.CopyTo(output);
                }
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = $"/lang {lcid}",
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error starting setup: {ex.Message}", "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
