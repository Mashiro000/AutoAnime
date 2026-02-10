using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;

namespace AutoAnime
{
    // --- 服务商模型 ---
    public class ApiProvider
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string DefaultModel { get; set; } = "";
        public string Hint { get; set; } = "";
    }

    // --- 媒体信息模型 ---
    public class MediaInfo
    {
        public string? title { get; set; }
        public string type { get; set; } = "Anime";
        public string year { get; set; } = "";
        public int season { get; set; }
        public int episode { get; set; }
    }

    // --- 配置模型 ---
    public class AiProfile
    {
        public string Remark { get; set; } = "默认配置";
        public string ApiUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string TmdbKey { get; set; } = "";
        public int ProviderIndex { get; set; } = 0;
    }

    public class AppSettings
    {
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public bool IsHardLinkMode { get; set; } = true;
        public bool RunInBackground { get; set; } = true;
        public List<AiProfile> Profiles { get; set; } = new List<AiProfile>();
        public int LastProfileIndex { get; set; } = -1;
    }

    public partial class MainWindow : FluentWindow
    {
        private FileSystemWatcher? _watcher;
        private bool _isRunning = false;
        private static readonly HttpClient _http = new HttpClient();
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isLoadingProfile = false; // 互斥锁

        public ObservableCollection<AiProfile> Profiles { get; set; } = new ObservableCollection<AiProfile>();
        public ObservableCollection<ApiProvider> Providers { get; set; } = new ObservableCollection<ApiProvider>();

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public MainWindow()
        {
            InitializeComponent();
            _http.Timeout = TimeSpan.FromSeconds(60);

            InitProviders();
            InitNotifyIcon();
            LoadSettings();
            CheckAutoStartStatus();
        }

        private void InitProviders()
        {
            Providers.Add(new ApiProvider { Name = "🚀 硅基流动 (推荐)", Url = "https://api.siliconflow.cn/v1", DefaultModel = "deepseek-ai/DeepSeek-V3" });
            Providers.Add(new ApiProvider { Name = "🐋 DeepSeek (官方)", Url = "https://api.deepseek.com", DefaultModel = "deepseek-chat" });
            Providers.Add(new ApiProvider { Name = "🥨 豆包 (火山引擎)", Url = "https://ark.cn-beijing.volces.com/api/v3", DefaultModel = "", Hint = "请填接入点 ID (ep-...)" });
            Providers.Add(new ApiProvider { Name = "🌟 Gemini (官方)", Url = "https://generativelanguage.googleapis.com/v1beta/openai", DefaultModel = "gemini-1.5-flash" });
            Providers.Add(new ApiProvider { Name = "🤖 ChatGPT", Url = "https://api.openai.com/v1", DefaultModel = "gpt-4o-mini" });
            Providers.Add(new ApiProvider { Name = "🛠️ 自定义", Url = "", DefaultModel = "" });

            CmbProviders.ItemsSource = Providers;
            CmbProviders.SelectedIndex = -1;
        }

        // --- 服务商下拉框逻辑 ---
        private void CmbProviders_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoadingProfile) return; // 🔒 如果正在加载配置，禁止联动

            if (CmbProviders.SelectedItem is ApiProvider p)
            {
                if (!p.Name.Contains("自定义"))
                {
                    TxtApiUrl.Text = p.Url;
                    if (!string.IsNullOrEmpty(p.DefaultModel))
                    {
                        TxtModel.Text = p.DefaultModel;
                        TxtModel.PlaceholderText = "模型名称";
                    }
                    else
                    {
                        TxtModel.Text = "";
                        TxtModel.PlaceholderText = p.Hint;
                    }
                }
                else
                {
                    TxtApiUrl.PlaceholderText = "请输入 API 地址";
                    TxtModel.PlaceholderText = "请输入模型名称";
                }
            }
        }

        // --- 核心修复：把加载逻辑抽离出来 ---
        private void ApplyProfile(AiProfile p)
        {
            if (p == null) return;

            // 🔒 上锁
            _isLoadingProfile = true;

            // 1. 恢复服务商下拉框
            if (p.ProviderIndex >= 0 && p.ProviderIndex < Providers.Count)
            {
                CmbProviders.SelectedIndex = p.ProviderIndex;
            }
            else
            {
                // 模糊匹配
                CmbProviders.SelectedIndex = Providers.Count - 1; // 默认自定义
                for (int i = 0; i < Providers.Count; i++)
                {
                    if (!string.IsNullOrEmpty(Providers[i].Url) && p.ApiUrl.Contains(Providers[i].Url))
                    {
                        CmbProviders.SelectedIndex = i;
                        break;
                    }
                }
            }

            // 2. 恢复文本框
            TxtRemark.Text = p.Remark;
            TxtApiUrl.Text = p.ApiUrl;
            TxtModel.Text = p.Model;
            TxtApiKey.Password = p.ApiKey;
            TxtTmdbKey.Password = p.TmdbKey;

            // 🔓 解锁
            _isLoadingProfile = false;
        }

        // 事件 1：当选中项改变时触发（两个以上时会触发）
        private void CmbProfiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is AiProfile p)
            {
                ApplyProfile(p);
            }
        }

        // 事件 2：🔥 修复点 - 当下拉框关闭时触发（点击同一个也会触发）
        private void CmbProfiles_DropDownClosed(object sender, EventArgs e)
        {
            if (CmbProfiles.SelectedItem is AiProfile p)
            {
                ApplyProfile(p);
            }
        }

        // --- 保存逻辑 ---
        private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtRemark.Text))
            {
                Log("⚠️ 请填写【备注名】再保存");
                return;
            }

            var newProfile = new AiProfile
            {
                Remark = TxtRemark.Text,
                ApiUrl = TxtApiUrl.Text,
                Model = TxtModel.Text,
                ApiKey = TxtApiKey.Password,
                TmdbKey = TxtTmdbKey.Password,
                ProviderIndex = CmbProviders.SelectedIndex
            };

            bool found = false;
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (Profiles[i].Remark == newProfile.Remark)
                {
                    Profiles[i] = newProfile;
                    // 强制刷新选中项
                    _isLoadingProfile = true;
                    CmbProfiles.SelectedIndex = i;
                    _isLoadingProfile = false;

                    found = true;
                    Log($"💾 已更新配置: {newProfile.Remark}");
                    break;
                }
            }

            if (!found)
            {
                Profiles.Add(newProfile);
                _isLoadingProfile = true;
                CmbProfiles.SelectedIndex = Profiles.Count - 1;
                _isLoadingProfile = false;
                Log($"💾 新增配置: {newProfile.Remark}");
            }

            SaveSettings();
        }

        // --- 以下逻辑保持不变 ---

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is AiProfile p)
            {
                Profiles.Remove(p);
                TxtRemark.Text = ""; TxtApiKey.Password = ""; TxtTmdbKey.Password = "";
                SaveSettings();
                Log("🗑️ 配置已删除");
            }
        }

        private async void BtnManual_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "视频|*.mp4;*.mkv;*.avi;*.mov", Multiselect = true };
            if (d.ShowDialog() == true)
            {
                foreach (var f in d.FileNames)
                {
                    var info = await CallAI(Path.GetFileName(f));
                    if (info != null && !string.IsNullOrEmpty(info.title))
                    {
                        info = await CorrectByTmdb(info);
                        ProcessFile(f, info);
                    }
                }
                Log("✅ 手动处理完成");
            }
        }

        private async void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            if (Path.GetExtension(e.FullPath).ToLower() is not ".mp4" and not ".mkv") return;
            if (e.Name != null && (e.Name.Contains("part") || e.Name.Contains("!qB"))) return;
            await Task.Delay(2000);
            Log($"🧠 发现: {e.Name}");
            var info = await CallAI(e.Name ?? "");
            if (info != null && !string.IsNullOrEmpty(info.title))
            {
                info = await CorrectByTmdb(info);
                ProcessFile(e.FullPath, info);
            }
            else Log("⚠️ AI 无法识别");
        }

        private async Task<MediaInfo?> CallAI(string filename)
        {
            try
            {
                string model = "", key = "", url = "";
                Dispatcher.Invoke(() => { model = TxtModel.Text; key = TxtApiKey.Password; url = TxtApiUrl.Text.TrimEnd('/'); });

                var prompt = $@"
                你是一个专业的影音库整理专家。请分析文件名 ""{filename}"" 并提取信息。
                请返回严格的 JSON 格式：{{ ""title"": ""中文通用译名"", ""type"": ""类型(Anime/Movie/Doc/TV)"", ""year"": ""年份"", ""season"": 1, ""episode"": 1 }}
                规则：
                1. type: Anime(动画), Movie(电影), Doc(纪录片), TV(剧集)
                2. title: 去除副标题。
                3. year: 电影必填。
                ";

                var req = new { model = model, messages = new[] { new { role = "user", content = prompt } }, response_format = new { type = "json_object" } };
                var json = JsonSerializer.Serialize(req);
                _http.DefaultRequestHeaders.Clear(); _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
                if (!url.EndsWith("/chat/completions")) url += "/chat/completions";

                var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
                if (!res.IsSuccessStatusCode) { Dispatcher.Invoke(() => Log($"❌ API Error: {res.StatusCode}")); return null; }

                var str = await res.Content.ReadAsStringAsync();
                var node = JsonNode.Parse(str);
                var content = node?["choices"]?[0]?["message"]?["content"]?.ToString();
                if (content != null && content.Contains("```json")) content = content.Replace("```json", "").Replace("```", "").Trim();

                var info = content != null ? JsonSerializer.Deserialize<MediaInfo>(content) : null;
                if (info != null) Dispatcher.Invoke(() => Log($"🤖 AI 识别: [{info.type}] {info.title} (S{info.season}E{info.episode})"));
                return info;
            }
            catch { return null; }
        }

        private async Task<MediaInfo> CorrectByTmdb(MediaInfo info)
        {
            string key = ""; Dispatcher.Invoke(() => key = TxtTmdbKey.Password);
            if (string.IsNullOrWhiteSpace(key)) return info;
            try
            {
                bool isMovie = info.type == "Movie";
                string searchType = isMovie ? "movie" : "tv";
                string yearParam = isMovie && !string.IsNullOrEmpty(info.year) ? $"&year={info.year}" : "";

                Dispatcher.Invoke(() => Log($"🎬 TMDB 搜{searchType}: {info.title} {info.year}"));
                var res = await _http.GetStringAsync($"[https://api.themoviedb.org/3/search/](https://api.themoviedb.org/3/search/){searchType}?api_key={key}&query={Uri.EscapeDataString(info.title ?? "")}&language=zh-CN{yearParam}");
                var node = JsonNode.Parse(res);
                var results = node?["results"]?.AsArray();

                if (results != null && results.Count > 0)
                {
                    var officialName = isMovie ? results[0]?["title"]?.ToString() : results[0]?["name"]?.ToString();
                    var date = results[0]?["release_date"]?.ToString();
                    if (isMovie && !string.IsNullOrEmpty(date) && date.Length >= 4) info.year = date.Substring(0, 4);
                    if (officialName != null && officialName != info.title)
                    {
                        Dispatcher.Invoke(() => Log($"✅ TMDB 校正: {info.title} -> {officialName}"));
                        info.title = officialName;
                    }
                }
                else Dispatcher.Invoke(() => Log("⚠️ TMDB 未找到，保持原名"));
            }
            catch { Dispatcher.Invoke(() => Log("⚠️ TMDB 连接失败")); }
            return info;
        }

        private void ProcessFile(string src, MediaInfo info)
        {
            try
            {
                string root = "", safeTitle = info.title ?? "Unknown";
                Dispatcher.Invoke(() => root = TxtTarget.Text);
                foreach (var c in Path.GetInvalidFileNameChars()) safeTitle = safeTitle.Replace(c, '_');

                string categoryDir = info.type switch { "Anime" => "动漫", "Movie" => "电影", "Doc" => "纪录片", "TV" => "电视剧", _ => "其他" };
                string finalPath;

                if (info.type == "Movie")
                {
                    string yearSuffix = string.IsNullOrEmpty(info.year) ? "" : $" ({info.year})";
                    string dir = Path.Combine(root, categoryDir, $"{safeTitle}{yearSuffix}");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    finalPath = Path.Combine(dir, $"{safeTitle}{yearSuffix}{Path.GetExtension(src)}");
                }
                else
                {
                    string dir = Path.Combine(root, categoryDir, safeTitle, $"Season {info.season}");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    finalPath = Path.Combine(dir, $"{safeTitle} - S{info.season:D2}E{info.episode:D2}{Path.GetExtension(src)}");
                }

                if (File.Exists(finalPath)) { Dispatcher.Invoke(() => Log($"⚠️ 目标已存在: {Path.GetFileName(finalPath)}")); return; }
                bool link = true; Dispatcher.Invoke(() => link = CmbMode.SelectedIndex == 0);
                if (link) { if (CreateHardLink(finalPath, src, IntPtr.Zero)) Dispatcher.Invoke(() => Log($"🔗 硬链成功: {categoryDir}/{Path.GetFileName(finalPath)}")); else Dispatcher.Invoke(() => Log("❌ 硬链失败")); }
                else { File.Move(src, finalPath); Dispatcher.Invoke(() => Log($"📦 移动成功: {categoryDir}/{Path.GetFileName(finalPath)}")); }
            }
            catch (Exception ex) { Dispatcher.Invoke(() => Log($"❌ 操作失败: {ex.Message}")); }
        }

        private void LoadSettings() { try { if (File.Exists("settings.json")) { var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText("settings.json")); if (s != null) { TxtSource.Text = s.SourcePath; TxtTarget.Text = s.TargetPath; CmbMode.SelectedIndex = s.IsHardLinkMode ? 0 : 1; ChkTray.IsChecked = s.RunInBackground; Profiles.Clear(); if (s.Profiles != null) foreach (var p in s.Profiles) Profiles.Add(p); CmbProfiles.ItemsSource = Profiles; if (s.LastProfileIndex >= 0 && s.LastProfileIndex < Profiles.Count) CmbProfiles.SelectedIndex = s.LastProfileIndex; } } else CmbProfiles.ItemsSource = Profiles; } catch { } }
        private void SaveSettings() { try { var s = new AppSettings { SourcePath = TxtSource.Text, TargetPath = TxtTarget.Text, IsHardLinkMode = CmbMode.SelectedIndex == 0, RunInBackground = ChkTray.IsChecked == true, Profiles = new List<AiProfile>(Profiles), LastProfileIndex = CmbProfiles.SelectedIndex }; File.WriteAllText("settings.json", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
        private void InitNotifyIcon() { _notifyIcon = new System.Windows.Forms.NotifyIcon(); try { _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); } catch { } _notifyIcon.Text = "AutoMedia AI"; _notifyIcon.Visible = true; _notifyIcon.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; Activate(); }; var m = new System.Windows.Forms.ContextMenuStrip(); m.Items.Add("显示", null, (s, e) => { Show(); WindowState = WindowState.Normal; Activate(); }); m.Items.Add("退出", null, (s, e) => { if (_notifyIcon != null) _notifyIcon.Visible = false; System.Windows.Application.Current.Shutdown(); }); _notifyIcon.ContextMenuStrip = m; }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (ChkTray.IsChecked == true && _notifyIcon != null) { e.Cancel = true; this.Hide(); _notifyIcon.ShowBalloonTip(3000, "AutoMedia", "最小化到托盘", System.Windows.Forms.ToolTipIcon.Info); } else { if (_notifyIcon != null) _notifyIcon.Visible = false; base.OnClosing(e); } }
        private void CheckAutoStartStatus() { try { using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false); string? v = k?.GetValue("AutoAnime")?.ToString(); ChkAutoStart.Checked -= ChkAutoStart_Changed; ChkAutoStart.Unchecked -= ChkAutoStart_Changed; ChkAutoStart.IsChecked = (v != null && v == System.Windows.Forms.Application.ExecutablePath); ChkAutoStart.Checked += ChkAutoStart_Changed; ChkAutoStart.Unchecked += ChkAutoStart_Changed; } catch { } }
        private void ChkAutoStart_Changed(object s, RoutedEventArgs e) { try { using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); if (ChkAutoStart.IsChecked == true) k?.SetValue("AutoAnime", System.Windows.Forms.Application.ExecutablePath); else k?.DeleteValue("AutoAnime", false); } catch { ChkAutoStart.IsChecked = !ChkAutoStart.IsChecked; } }
        private void BtnRestart_Click(object s, RoutedEventArgs e) { SaveSettings(); if (_notifyIcon != null) _notifyIcon.Visible = false; Process.Start(Environment.ProcessPath!); System.Windows.Application.Current.Shutdown(); }
        private void BtnSelectSource_Click(object s, RoutedEventArgs e) => TxtSource.Text = SelectFolder();
        private void BtnSelectTarget_Click(object s, RoutedEventArgs e) => TxtTarget.Text = SelectFolder();
        private string SelectFolder() { using var d = new System.Windows.Forms.FolderBrowserDialog(); return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : ""; }
        private void BtnStart_Click(object s, RoutedEventArgs e) { if (_isRunning) { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; } Log("🛑 监控停止"); BtnStart.Content = "启动全能监控"; BtnStart.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary; } else { if (string.IsNullOrWhiteSpace(TxtSource.Text) || string.IsNullOrWhiteSpace(TxtApiKey.Password)) { Log("❌ 请填写配置"); return; } try { _watcher = new FileSystemWatcher(TxtSource.Text); _watcher.IncludeSubdirectories = true; _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite; _watcher.Created += OnFileCreated; _watcher.Renamed += OnFileCreated; _watcher.EnableRaisingEvents = true; Log($"🚀 监控启动: {TxtSource.Text}"); } catch (Exception ex) { Log($"❌ 启动失败: {ex.Message}"); return; } BtnStart.Content = "停止监控"; BtnStart.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger; SaveSettings(); } _isRunning = !_isRunning; }
        private void Log(string msg) { if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(msg)); return; } TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n"); TxtLog.ScrollToEnd(); }
    }
}