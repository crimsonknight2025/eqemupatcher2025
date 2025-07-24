using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.WindowsAPICodePack.Taskbar;
using System.Diagnostics;
using System.Threading;

namespace EQEmu_Patcher
{
    public partial class MainForm : Form
    {
        public static string serverName;
        public static string filelistUrl;
        public static string patcherUrl;
        public static string version;
        string fileName;
        bool isPatching = false;
        bool isPatchCancelled = false;
        bool isPendingPatch = false;
        string myHash;
        bool isNeedingSelfUpdate;  // you can remove this field if you like
        bool isLoading;
        bool isAutoPatch = false;
        bool isAutoPlay = false;
        CancellationTokenSource cts;
        Process process;

        public static List<VersionTypes> supportedClients = new List<VersionTypes>
        {
            VersionTypes.Rain_Of_Fear,
            VersionTypes.Rain_Of_Fear_2
        };

        private Dictionary<VersionTypes, ClientVersion> clientVersions = new Dictionary<VersionTypes, ClientVersion>();
        VersionTypes currentVersion;

        public MainForm()
        {
            InitializeComponent();
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            isLoading = true;
            version = Assembly.GetEntryAssembly().GetName().Version.ToString();
            cts = new CancellationTokenSource();

            // Read metadata
            serverName = Assembly.GetExecutingAssembly().GetCustomAttribute<ServerName>().Value;
#if DEBUG
            serverName = "EQEMU Patcher";
#endif
            if (string.IsNullOrEmpty(serverName))
            {
                MessageBox.Show("Missing ServerName attribute.", "Error");
                Close();
                return;
            }

            fileName = Assembly.GetExecutingAssembly().GetCustomAttribute<FileName>().Value;
#if DEBUG
            fileName = "eqemupatcher";
#endif
            if (string.IsNullOrEmpty(fileName))
            {
                MessageBox.Show("Missing FileName attribute.", "Error");
                Close();
                return;
            }

            filelistUrl = Assembly.GetExecutingAssembly().GetCustomAttribute<FileListUrl>().Value;
#if DEBUG
            filelistUrl = "https://github.com/crimsonknight2025/eqemupatcher2025/releases/latest/";
#endif
            if (string.IsNullOrEmpty(filelistUrl))
            {
                MessageBox.Show("Missing FileListUrl.", serverName);
                Close();
                return;
            }
            if (!filelistUrl.EndsWith("/")) filelistUrl += "/";

            patcherUrl = Assembly.GetExecutingAssembly().GetCustomAttribute<PatcherUrl>().Value;
#if DEBUG
            patcherUrl = "https://github.com/crimsonknight2025/eqemupatcher2025/releases/latest/";
#endif
            if (string.IsNullOrEmpty(patcherUrl))
            {
                MessageBox.Show("Missing PatcherUrl.", serverName);
                Close();
                return;
            }
            if (!patcherUrl.EndsWith("/")) patcherUrl += "/";

            // UI initial
            txtList.Visible = false;
            splashLogo.Visible = true;
            this.Width = Math.Max(this.Width, 432);
            this.Height = Math.Max(this.Height, 550);

            buildClientVersions();
            IniLibrary.Load();

            // FORCE RoF2 client
            detectClientVersion();

            isAutoPlay = IniLibrary.instance.AutoPlay.ToLower() == "true";
            isAutoPatch = IniLibrary.instance.AutoPatch.ToLower() == "true";
            chkAutoPlay.Checked = isAutoPlay;
            chkAutoPatch.Checked = isAutoPatch;

            // Remove old self-update
            try { File.Delete(Application.ExecutablePath + ".old"); } catch { }

            // Determine suffix (we always force "rof")
            string suffix = "rof";

            // ----- bypass unsupported-client check -----
            /*
            bool isSupported = false;
            foreach (var ver in supportedClients)
                if (ver == currentVersion) { isSupported = true; break; }
            if (!isSupported)
            {
                MessageBox.Show($"The server {serverName} does not work with this copy of EverQuest ({currentVersion})",
                                serverName);
                Close();
                return;
            }
            */
            // ----- end bypass -----

            // Window title
            this.Text = $"{serverName} (Client: {currentVersion.ToString().Replace('_', ' ')})";

            // Progress bar
            progressBar.Minimum = 0;
            progressBar.Maximum = 10000;
            progressBar.Value = 0;

            // Subscribe to logs/progress
            StatusLibrary.SubscribeProgress(val => {
                Invoke((MethodInvoker)(() => {
                    progressBar.Value = val;
                    if (Environment.OSVersion.Version.Major >= 6)
                    {
                        var tb = TaskbarManager.Instance;
                        tb.SetProgressValue(val, 10000);
                        tb.SetProgressState(val == 10000
                            ? TaskbarProgressBarState.NoProgress
                            : TaskbarProgressBarState.Normal);
                    }
                }));
            });
            StatusLibrary.SubscribeLogAdd(msg => {
                Invoke((MethodInvoker)(() => {
                    if (!txtList.Visible)
                    {
                        txtList.Visible = true;
                        splashLogo.Visible = false;
                    }
                    txtList.AppendText(msg + "\r\n");
                }));
            });
            StatusLibrary.SubscribePatchState(patching => {
                Invoke((MethodInvoker)(() => {
                    btnCheck.BackColor = SystemColors.Control;
                    btnCheck.Text = patching ? "Cancel" : "Patch";
                }));
            });

            // Fetch filelist
            string webUrl = $"{filelistUrl}{suffix}/filelist_{suffix}.yml";
            var resp = await DownloadFile(cts, webUrl, "filelist.yml");
            if (!string.IsNullOrEmpty(resp))
            {
                MessageBox.Show($"Failed to fetch filelist from {webUrl}: {resp}");
                Close();
                return;
            }

            // Self-update
            try
            {
                var data = await Download(cts, $"{patcherUrl}{fileName}-hash.txt");
                var remoteHash = System.Text.Encoding.Default.GetString(data).ToUpper();
                myHash = UtilityLibrary.GetMD5(Application.ExecutablePath);
                if (remoteHash != myHash)
                {
                    isNeedingSelfUpdate = true;
                    if (!isPendingPatch) btnCheck.BackColor = Color.Red;
                }
            }
            catch { }

            // Load manifest and auto-play logic
            FileList filelist;
            using (var input = File.OpenText("filelist.yml"))
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(new CamelCaseNamingConvention())
                    .Build();
                filelist = deserializer.Deserialize<FileList>(input);
            }
            if (filelist.version != IniLibrary.instance.LastPatchedVersion)
                btnCheck.BackColor = Color.Red;
            else if (isAutoPlay)
                PlayGame();

            isLoading = false;
            var splash = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqemupatcher.png");
            if (File.Exists(splash)) splashLogo.Load(splash);
            cts.Cancel();
        }

        private void detectClientVersion()
        {
            // always force Rain of Fear 2
            currentVersion = VersionTypes.Rain_Of_Fear_2;
            splashLogo.Image = Properties.Resources.rof;
        }

        private void buildClientVersions()
        {
            clientVersions.Clear();
            clientVersions.Add(VersionTypes.Titanium, new ClientVersion("Titanium", "titanium"));
            clientVersions.Add(VersionTypes.Secrets_Of_Feydwer, new ClientVersion("Secrets of Feydwer", "sof"));
            clientVersions.Add(VersionTypes.Seeds_Of_Destruction, new ClientVersion("Seeds of Destruction", "sod"));
            clientVersions.Add(VersionTypes.Rain_Of_Fear, new ClientVersion("Rain of Fear", "rof"));
            clientVersions.Add(VersionTypes.Rain_Of_Fear_2, new ClientVersion("Rain of Fear 2", "rof2"));
            clientVersions.Add(VersionTypes.Underfoot, new ClientVersion("Underfoot", "underfoot"));
            clientVersions.Add(VersionTypes.Broken_Mirror, new ClientVersion("Broken Mirror", "brokenmirror"));
        }

        private void btnStart_Click(object sender, EventArgs e) => PlayGame();

        private void PlayGame()
        {
            try
            {
                process = UtilityLibrary.StartEverquest();
                if (process != null) Close();
                else MessageBox.Show("The process failed to start");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error launching EQ: " + ex.Message);
            }
        }

        private void btnCheck_Click(object sender, EventArgs e)
        {
            if (isLoading && !isPendingPatch)
            {
                isPendingPatch = true;
                pendingPatchTimer.Enabled = true;
                StatusLibrary.Log("Checking for updates...");
                btnCheck.Text = "Cancel";
                return;
            }
            if (isPatching)
            {
                isPatchCancelled = true;
                cts.Cancel();
                return;
            }
            StartPatch();
        }

        public static async Task<string> DownloadFile(CancellationTokenSource cts, string url, string path)
        {
            path = path.Replace("/", "\\");
            if (path.Contains("\\"))
            {
                var dir = Path.GetDirectoryName(Application.ExecutablePath) + "\\" +
                          Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
            }
            return await UtilityLibrary.DownloadFile(cts, url, path);
        }

        public static async Task<byte[]> Download(CancellationTokenSource cts, string url)
            => await UtilityLibrary.Download(cts, url);

        private void StartPatch()
        {
            if (isPatching) return;
            cts = new CancellationTokenSource();
            isPatchCancelled = false;
            txtList.Text = "";
            StatusLibrary.SetPatchState(true);
            isPatching = true;

            Task.Run(async () =>
            {
                try { await AsyncPatch(); }
                catch (Exception ex) { StatusLibrary.Log($"Patch exception: {ex.Message}"); }
                StatusLibrary.SetPatchState(false);
                isPatching = false;
                isPatchCancelled = false;
                cts.Cancel();
                if (isAutoPlay) PlayGame();
            });
        }

        private async Task AsyncPatch()
        {
            var sw = Stopwatch.StartNew();
            StatusLibrary.Log($"Patching with version {version}...");
            StatusLibrary.SetProgress(0);

            FileList filelist;
            using (var input = File.OpenText("filelist.yml"))
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(new CamelCaseNamingConvention())
                    .Build();
                filelist = deserializer.Deserialize<FileList>(input);
            }

            double total = 0, done = 1, patched = 0;
            foreach (var e in filelist.downloads) total += e.size;
            if (total == 0) total = 1;

            foreach (var entry in filelist.downloads)
            {
                if (isPatchCancelled)
                {
                    StatusLibrary.Log("Patching cancelled.");
                    return;
                }
                StatusLibrary.SetProgress((int)(done / total * 10000));

                var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, entry.name.Replace("/", "\\"));
                if (File.Exists(local))
                {
                    var md5 = UtilityLibrary.GetMD5(local);
                    if (string.Equals(md5, entry.md5, StringComparison.OrdinalIgnoreCase))
                    {
                        done += entry.size;
                        continue;
                    }
                }

                var url = filelist.downloadprefix + entry.name.Replace("\\", "/");
                var err = await DownloadFile(cts, url, entry.name);
                if (!string.IsNullOrEmpty(err))
                {
                    StatusLibrary.Log($"Failed {entry.name}: {err}");
                    return;
                }
                StatusLibrary.Log($"{entry.name} ({generateSize(entry.size)})");
                done += entry.size;
                patched += entry.size;
            }

            StatusLibrary.SetProgress(10000);
            if (patched == 0)
                StatusLibrary.Log("Already up to date.");
            else
            {
                StatusLibrary.Log($"Patched {generateSize(patched)} in {sw.Elapsed:ss\\.ff} seconds.");
                IniLibrary.instance.LastPatchedVersion = filelist.version;
                IniLibrary.Save();
            }
        }

        private string generateSize(double s)
        {
            string[] suf = { "bytes", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (s >= 1024 && i < suf.Length - 1) { s /= 1024; i++; }
            return $"{Math.Round(s, 2)} {suf[i]}";
        }

        private void chkAutoPlay_CheckedChanged(object sender, EventArgs e)
            => isAutoPlay = chkAutoPlay.Checked;

        private void chkAutoPatch_CheckedChanged(object sender, EventArgs e)
            => isAutoPatch = chkAutoPatch.Checked;

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (isAutoPatch && !isLoading) StartPatch();
            else if (isAutoPatch)
            {
                isPendingPatch = true;
                pendingPatchTimer.Enabled = true;
                btnCheck.Text = "Cancel";
            }
        }

        private void pendingPatchTimer_Tick(object sender, EventArgs e)
        {
            if (!isLoading)
            {
                pendingPatchTimer.Enabled = false;
                isPendingPatch = false;
                btnCheck.PerformClick();
            }
        }
    }

    public class FileList
    {
        public string version { get; set; }
        public string downloadprefix { get; set; }
        public List<FileEntry> downloads { get; set; }
        public List<FileEntry> deletes { get; set; }
        public List<FileEntry> unpacks { get; set; }
    }

    public class FileEntry
    {
        public string name { get; set; }
        public string md5 { get; set; }
        public string date { get; set; }
        public string zip { get; set; }
        public int size { get; set; }
    }
}
